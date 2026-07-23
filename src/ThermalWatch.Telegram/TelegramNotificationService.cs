using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using ThermalWatch.Core;

namespace ThermalWatch.Telegram;

public sealed class TelegramNotificationService(
    TelegramOptions options,
    AnomalySnapshotStore snapshotStore,
    NotificationCandidateEngine candidateEngine,
    IHttpClientFactory httpClientFactory,
    ILogger<TelegramNotificationService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _manualSendGate = new(initialCount: 1, maxCount: 1);
    private ValidatedTelegram? _validated;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ValidatedTelegram? validation = await TryValidateAsync(stoppingToken).ConfigureAwait(false);
        if (validation is not { } validated)
            return;

        Volatile.Write(ref _validated, validated);
        try
        {
            await foreach (AnomalySnapshot? snapshot in snapshotStore.ReadUpdatesAsync(stoppingToken).ConfigureAwait(false))
            {
                if (!ReferenceEquals(Volatile.Read(ref _validated), validated))
                    return;

                NotificationAutomaticProcessingResult result = await candidateEngine.ProcessAutomaticAsync(
                    snapshot,
                    (candidate, cancellationToken) => TryDeliverAutomaticAsync(
                        validated,
                        candidate,
                        cancellationToken),
                    stoppingToken).ConfigureAwait(false);
                LogProcessingSummary(result.Summary);
                if (!result.ContinueProcessing)
                    return;
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _validated, value: null, validated);
        }
    }

    public async Task<ManualTelegramSendResult> SendTopAsync(
        int requestedCount,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _validated) is null)
            return ManualTelegramSendResult.TelegramUnavailable;

        if (!_manualSendGate.Wait(millisecondsTimeout: 0, cancellationToken))
            return ManualTelegramSendResult.AlreadyRunning;

        try
        {
            ValidatedTelegram? validated = Volatile.Read(ref _validated);
            if (validated is null)
                return ManualTelegramSendResult.TelegramUnavailable;

            return await ExecuteManualSendAsync(
                validated,
                requestedCount,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _manualSendGate.Release();
        }
    }

    private async Task<ValidatedTelegram?> TryValidateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var clientOptions = new TelegramBotClientOptions(options.BotToken!)
            {
                RetryCount = 2,
                RetryThreshold = 30
            };
            var client = new TelegramBotClient(
                clientOptions,
                httpClientFactory.CreateClient(name: "Telegram"),
                cancellationToken);
            var chatId = new ChatId(options.ChannelId!);
            User bot = await client.GetMe(cancellationToken).ConfigureAwait(false);
            ChatFullInfo chat = await client.GetChat(chatId, cancellationToken).ConfigureAwait(false);

            if (chat.Type != ChatType.Channel)
            {
                TelegramNotificationLog.InvalidChannel(logger);
                return null;
            }

            ChatMember member = await client.GetChatMember(chatId, bot.Id, cancellationToken).ConfigureAwait(false);
            if (member is not ChatMemberOwner
                && member is not ChatMemberAdministrator { CanPostMessages: true })
            {
                TelegramNotificationLog.CannotPost(logger);
                return null;
            }

            TelegramNotificationLog.Validated(logger);
            return new(client, chatId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            TelegramNotificationLog.ValidationFailed(logger);
            return null;
        }
    }

    private async Task<ManualTelegramSendResult> ExecuteManualSendAsync(
        ValidatedTelegram validated,
        int requestedCount,
        CancellationToken cancellationToken)
    {
        ManualNotificationCandidates preparation = await candidateEngine.PrepareManualAsync(
            snapshotStore.Current,
            requestedCount,
            cancellationToken).ConfigureAwait(false);
        if (preparation.Selected.IsEmpty)
        {
            return await CompleteEmptyManualSendAsync(
                validated,
                requestedCount,
                preparation.EligibleCount,
                cancellationToken).ConfigureAwait(false);
        }

        if (!await TrySendManualStatusAsync(
            validated,
            message: $"🔥 <b>Sending top {preparation.Selected.Length} anomalies manually</b>",
            cancellationToken).ConfigureAwait(false))
        {
            var response = new ManualTelegramSendResponse(
                requestedCount,
                preparation.EligibleCount,
                preparation.Selected.Length,
                SentCount: 0,
                FailedCount: 0,
                []);
            LogManualSendSummary(response);
            return ManualTelegramSendResult.StatusMessageFailed;
        }

        (int sentCount, List<string> failedIds) = await SendManualNotificationsAsync(
            validated,
            preparation.Selected,
            cancellationToken).ConfigureAwait(false);
        var completedResponse = new ManualTelegramSendResponse(
            requestedCount,
            preparation.EligibleCount,
            preparation.Selected.Length,
            sentCount,
            failedIds.Count,
            [.. failedIds]);
        LogManualSendSummary(completedResponse);
        return ManualTelegramSendResult.Completed(completedResponse);
    }

    private async Task<ManualTelegramSendResult> CompleteEmptyManualSendAsync(
        ValidatedTelegram validated,
        int requestedCount,
        int eligibleCount,
        CancellationToken cancellationToken)
    {
        var response = new ManualTelegramSendResponse(
            requestedCount,
            eligibleCount,
            SelectedCount: 0,
            SentCount: 0,
            FailedCount: 0,
            []);
        bool statusSent = await TrySendManualStatusAsync(
            validated,
            message: "ℹ️ <b>No anomalies currently pass all filters</b>",
            cancellationToken).ConfigureAwait(false);
        LogManualSendSummary(response);
        return statusSent
            ? ManualTelegramSendResult.Completed(response)
            : ManualTelegramSendResult.StatusMessageFailed;
    }

    private async Task<(int SentCount, List<string> FailedIds)> SendManualNotificationsAsync(
        ValidatedTelegram validated,
        IReadOnlyList<PreparedNotificationCandidate> selected,
        CancellationToken cancellationToken)
    {
        int sentCount = 0;
        var failedIds = new List<string>();
        foreach (PreparedNotificationCandidate candidate in selected)
        {
            try
            {
                await SendNotificationAsync(validated, candidate, cancellationToken).ConfigureAwait(false);
                sentCount++;
                TelegramNotificationLog.ManualNotificationSent(logger, candidate.Cluster.Id);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                failedIds.Add(candidate.Cluster.Id);
                TelegramNotificationLog.ManualNotificationFailed(logger, candidate.Cluster.Id);
            }
        }

        return (sentCount, failedIds);
    }

    private async Task<bool> TrySendManualStatusAsync(
        ValidatedTelegram validated,
        string message,
        CancellationToken cancellationToken)
    {
        try
        {
            await validated.Client.SendMessage(
                validated.ChatId,
                message,
                parseMode: ParseMode.Html,
                linkPreviewOptions: new() { IsDisabled = true },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            TelegramNotificationLog.ManualStatusFailed(logger);
            return false;
        }
    }

    private async Task<NotificationDeliveryOutcome> TryDeliverAutomaticAsync(
        ValidatedTelegram validated,
        PreparedNotificationCandidate candidate,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendNotificationAsync(validated, candidate, cancellationToken).ConfigureAwait(false);
            TelegramNotificationLog.NotificationSent(
                logger,
                candidate.Cluster.Id,
                candidate.Cluster.Representative.Satellite,
                candidate.Cluster.Representative.AcquiredAtUtc);
            return NotificationDeliveryOutcome.Delivered;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ApiRequestException exception) when (exception.ErrorCode is 400 or 401 or 403)
        {
            DisableValidatedTelegram(validated);
            TelegramNotificationLog.PermanentSendFailure(logger);
            return NotificationDeliveryOutcome.Stop;
        }
        catch (Exception)
        {
            TelegramNotificationLog.TransientSendFailure(logger, candidate.Cluster.Id);
            return NotificationDeliveryOutcome.RetryLater;
        }
    }

    private static async Task SendNotificationAsync(
        ValidatedTelegram validated,
        PreparedNotificationCandidate candidate,
        CancellationToken cancellationToken)
    {
        InlineKeyboardMarkup keyboard = CreateLocationKeyboard(candidate.Cluster);
        string message = TelegramMessageFormatter.Format(
            candidate.Cluster,
            candidate.Preview,
            candidate.PreviewSelection.Dimensions,
            candidate.PreviewSelection.ClusterDiameterKilometers,
            candidate.LandCoverSummary,
            candidate.NearbyFeatures);

        if (candidate.Preview.PngBytes is { } pngBytes)
        {
            using var stream = new MemoryStream(pngBytes, writable: false);
            await validated.Client.SendPhoto(
                validated.ChatId,
                InputFile.FromStream(stream, fileName: "thermal-anomaly.png"),
                caption: message,
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await validated.Client.SendMessage(
                validated.ChatId,
                message,
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                linkPreviewOptions: new() { IsDisabled = true },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    internal static InlineKeyboardMarkup CreateLocationKeyboard(NotificationCluster cluster)
    {
        Anomaly representative = cluster.Representative;
        string yandexMapsUrl = string.Create(
            CultureInfo.InvariantCulture,
            handler: $"https://yandex.com/maps/?ll={representative.Longitude:0.######}%2C{representative.Latitude:0.######}&pt={representative.Longitude:0.######}%2C{representative.Latitude:0.######}&z=12&l=sat");
        return new(
        [
            [
                InlineKeyboardButton.WithUrl(
                    text: "🗺 Google Maps",
                    url: representative.GoogleMapsUrl),
                InlineKeyboardButton.WithUrl(
                    text: "🗺 Yandex Maps",
                    url: yandexMapsUrl)
            ]
        ]);
    }

    private void DisableValidatedTelegram(ValidatedTelegram validated) =>
        Interlocked.CompareExchange(ref _validated, value: null, validated);

    private void LogProcessingSummary(NotificationProcessingSummary summary)
    {
        if (summary.PrimedDetectionCount > 0)
            TelegramNotificationLog.DeduplicationPrimed(logger, summary.PrimedDetectionCount);

        if (summary.NewDetectionCount > 0)
        {
            TelegramNotificationLog.ZonesCreated(
                logger,
                summary.CandidateClusterCount,
                summary.NewDetectionCount);
        }

        if (logger.IsEnabled(LogLevel.Information)
            && (summary.CandidateClusterCount > 0
            || summary.AcceptedClusterCount > 0
            || summary.RejectedClusterCount > 0
            || summary.DuplicateEpisodeCount > 0
            || summary.PendingPreviewCount > 0
            || summary.PreviewTimeoutCount > 0
            || summary.SendFailureCount > 0))
        {
            int nighttimeCount = summary.RejectionCount(NotificationRejectionReason.Nighttime);
            int insufficientDetectionsCount = summary.RejectionCount(NotificationRejectionReason.InsufficientDetections);
            int lowConfidenceCount = summary.RejectionCount(NotificationRejectionReason.LowConfidence);
            int lowFrpCount = summary.RejectionCount(NotificationRejectionReason.LowFrp);
            int lowThermalContrastCount = summary.RejectionCount(NotificationRejectionReason.LowThermalContrast);
            int missingRequiredValueCount = summary.RejectionCount(NotificationRejectionReason.MissingRequiredValue);
            int previewUnavailableCount = summary.RejectionCount(NotificationRejectionReason.PreviewUnavailable);
            TelegramNotificationLog.VisibilitySummary(
                logger,
                summary.CandidateClusterCount,
                summary.AcceptedClusterCount,
                summary.RejectedClusterCount,
                summary.DuplicateEpisodeCount,
                summary.PendingPreviewCount,
                summary.PreviewTimeoutCount,
                summary.SendFailureCount,
                nighttimeCount,
                insufficientDetectionsCount,
                lowConfidenceCount,
                lowFrpCount,
                lowThermalContrastCount,
                missingRequiredValueCount,
                previewUnavailableCount);
        }

        if (summary.LandCoverCandidateCount > 0)
        {
            TelegramNotificationLog.LandCoverSummary(
                logger,
                summary.LandCoverCandidateCount,
                summary.VegetationSuppressedCount,
                summary.LandCoverUnavailableCount,
                summary.LandCoverYear);
        }
    }

    private void LogManualSendSummary(ManualTelegramSendResponse response)
    {
        TelegramNotificationLog.ManualSendSummary(
            logger,
            response.RequestedCount,
            response.EligibleCount,
            response.SelectedCount,
            response.SentCount,
            response.FailedCount);
    }

    private sealed record ValidatedTelegram(TelegramBotClient Client, ChatId ChatId);
}
