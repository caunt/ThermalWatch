using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
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

                AutomaticNotificationProcessingResult result = await candidateEngine.ProcessAutomaticNotificationsAsync(
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

    public async Task<ManualTelegramSendResult> SendTopClustersAsync(
        int requestedClusterCount,
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
                requestedClusterCount,
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
            var channelId = new ChatId(options.ChannelId!);
            User bot = await client.GetMe(cancellationToken).ConfigureAwait(false);
            ChatFullInfo channel = await client.GetChat(channelId, cancellationToken).ConfigureAwait(false);

            if (channel.Type != ChatType.Channel)
            {
                TelegramNotificationLog.InvalidChannel(logger);
                return null;
            }

            ChatMember channelMember = await client.GetChatMember(channelId, bot.Id, cancellationToken).ConfigureAwait(false);
            if (channelMember is not ChatMemberOwner
                && channelMember is not ChatMemberAdministrator { CanPostMessages: true })
            {
                TelegramNotificationLog.CannotPost(logger);
                return null;
            }

            ChatId? discussionId = await TryResolveDiscussionAsync(
                client,
                channel,
                bot.Id,
                cancellationToken).ConfigureAwait(false);
            if (discussionId is null)
                return null;

            TelegramNotificationLog.Validated(logger);
            return new(client, channelId, new(channel.Id), discussionId);
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

    private async Task<ChatId?> TryResolveDiscussionAsync(
        ITelegramBotClient client,
        ChatFullInfo channel,
        long botId,
        CancellationToken cancellationToken)
    {
        if (channel.LinkedChatId is not { } discussionChatId)
        {
            TelegramNotificationLog.MissingLinkedDiscussion(logger);
            return null;
        }

        var discussionId = new ChatId(discussionChatId);
        ChatFullInfo discussion = await client.GetChat(discussionId, cancellationToken).ConfigureAwait(false);
        if (discussion.Type != ChatType.Supergroup || discussion.LinkedChatId != channel.Id)
        {
            TelegramNotificationLog.InvalidLinkedDiscussion(logger);
            return null;
        }

        ChatMember member = await client.GetChatMember(discussionId, botId, cancellationToken).ConfigureAwait(false);
        if (!CanSendToDiscussion(member))
        {
            TelegramNotificationLog.CannotComment(logger);
            return null;
        }

        return discussionId;
    }

    private async Task<ManualTelegramSendResult> ExecuteManualSendAsync(
        ValidatedTelegram validated,
        int requestedClusterCount,
        CancellationToken cancellationToken)
    {
        ManualNotificationCandidateSelection preparation = await candidateEngine.PrepareManualCandidatesAsync(
            snapshotStore.Current,
            requestedClusterCount,
            cancellationToken).ConfigureAwait(false);
        if (preparation.SelectedCandidates.IsEmpty)
        {
            return await CompleteEmptyManualSendAsync(
                validated,
                requestedClusterCount,
                preparation.EligibleClusterCount,
                cancellationToken).ConfigureAwait(false);
        }

        if (!await TrySendManualStatusAsync(
            validated,
            message: $"🔥 <b>Sending top {preparation.SelectedCandidates.Length} anomaly clusters manually</b>",
            cancellationToken).ConfigureAwait(false))
        {
            var response = new ManualTelegramSendResponse(
                requestedClusterCount,
                preparation.EligibleClusterCount,
                preparation.SelectedCandidates.Length,
                SentClusterCount: 0,
                FailedClusterCount: 0,
                []);
            LogManualSendSummary(response);
            return ManualTelegramSendResult.StatusMessageFailed;
        }

        (int sentClusterCount, List<string> failedClusterIds) = await SendManualNotificationsAsync(
            validated,
            preparation.SelectedCandidates,
            cancellationToken).ConfigureAwait(false);
        var completedResponse = new ManualTelegramSendResponse(
            requestedClusterCount,
            preparation.EligibleClusterCount,
            preparation.SelectedCandidates.Length,
            sentClusterCount,
            failedClusterIds.Count,
            [.. failedClusterIds]);
        LogManualSendSummary(completedResponse);
        return ManualTelegramSendResult.Completed(completedResponse);
    }

    private async Task<ManualTelegramSendResult> CompleteEmptyManualSendAsync(
        ValidatedTelegram validated,
        int requestedClusterCount,
        int eligibleClusterCount,
        CancellationToken cancellationToken)
    {
        var response = new ManualTelegramSendResponse(
            requestedClusterCount,
            eligibleClusterCount,
            SelectedClusterCount: 0,
            SentClusterCount: 0,
            FailedClusterCount: 0,
            []);
        bool statusSent = await TrySendManualStatusAsync(
            validated,
            message: "ℹ️ <b>No anomaly clusters currently pass all filters</b>",
            cancellationToken).ConfigureAwait(false);
        LogManualSendSummary(response);
        return statusSent
            ? ManualTelegramSendResult.Completed(response)
            : ManualTelegramSendResult.StatusMessageFailed;
    }

    private async Task<(int SentClusterCount, List<string> FailedClusterIds)> SendManualNotificationsAsync(
        ValidatedTelegram validated,
        IReadOnlyList<PreparedNotificationCandidate> selectedCandidates,
        CancellationToken cancellationToken)
    {
        int sentClusterCount = 0;
        var failedClusterIds = new List<string>();
        foreach (PreparedNotificationCandidate candidate in selectedCandidates)
        {
            try
            {
                await SendNotificationAsync(validated, candidate, cancellationToken).ConfigureAwait(false);
                sentClusterCount++;
                TelegramNotificationLog.ManualNotificationSent(logger, candidate.Cluster.Id);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                failedClusterIds.Add(candidate.Cluster.Id);
                TelegramNotificationLog.ManualNotificationFailed(logger, candidate.Cluster.Id);
            }
        }

        return (sentClusterCount, failedClusterIds);
    }

    private async Task<bool> TrySendManualStatusAsync(
        ValidatedTelegram validated,
        string message,
        CancellationToken cancellationToken)
    {
        try
        {
            await validated.Client.SendMessage(
                validated.ChannelId,
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

    private Task SendNotificationAsync(
        ValidatedTelegram validated,
        PreparedNotificationCandidate candidate,
        CancellationToken cancellationToken) =>
        SendNotificationAsync(
            validated.Client,
            validated.ChannelId,
            validated.ChannelReplyId,
            validated.DiscussionId,
            candidate,
            logger,
            cancellationToken);

    internal static async Task SendNotificationAsync(
        ITelegramBotClient client,
        ChatId channelId,
        ChatId channelReplyId,
        ChatId discussionId,
        PreparedNotificationCandidate candidate,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        TelegramNotificationMessages messages = TelegramMessageFormatter.FormatMessages(
            candidate.Cluster,
            candidate.Preview,
            candidate.PreviewSelection.Dimensions,
            candidate.PreviewSelection.ClusterDiameterKilometers,
            candidate.LandCoverSummary,
            candidate.NearbyFeatures);

        Message channelPost;
        if (candidate.Preview.PngBytes is { } pngBytes)
        {
            using var stream = new MemoryStream(pngBytes, writable: false);
            channelPost = await client.SendPhoto(
                channelId,
                InputFile.FromStream(stream, fileName: "thermal-anomaly.png"),
                caption: messages.MainMessage,
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else
        {
            channelPost = await client.SendMessage(
                channelId,
                messages.MainMessage,
                parseMode: ParseMode.Html,
                linkPreviewOptions: new() { IsDisabled = true },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await client.SendMessage(
                discussionId,
                messages.CommentMessage,
                parseMode: ParseMode.Html,
                replyParameters: new()
                {
                    MessageId = channelPost.Id,
                    ChatId = channelReplyId
                },
                linkPreviewOptions: new() { IsDisabled = true },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            TelegramNotificationLog.CommentFailed(logger, candidate.Cluster.Id);
        }
    }

    private static bool CanSendToDiscussion(ChatMember member) => member switch
    {
        ChatMemberOwner or ChatMemberAdministrator or ChatMemberMember => true,
        ChatMemberRestricted { IsMember: true, CanSendMessages: true } => true,
        _ => false
    };

    private void DisableValidatedTelegram(ValidatedTelegram validated) =>
        Interlocked.CompareExchange(ref _validated, value: null, validated);

    private void LogProcessingSummary(AutomaticNotificationProcessingSummary summary)
    {
        if (logger.IsEnabled(LogLevel.Information)
            && (summary.ActiveClusterCount > 0
            || summary.DeliveredClusterCount > 0
            || summary.RejectedClusterCount > 0
            || summary.DuplicateEpisodeCount > 0
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
                summary.ActiveClusterCount,
                summary.EvaluatedClusterCount,
                summary.DeliveredClusterCount,
                summary.RejectedClusterCount,
                summary.StartupSuppressedIncidentCount,
                summary.DuplicateEpisodeCount,
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
            response.RequestedClusterCount,
            response.EligibleClusterCount,
            response.SelectedClusterCount,
            response.SentClusterCount,
            response.FailedClusterCount);
    }

    private sealed record ValidatedTelegram(
        ITelegramBotClient Client,
        ChatId ChannelId,
        ChatId ChannelReplyId,
        ChatId DiscussionId);
}
