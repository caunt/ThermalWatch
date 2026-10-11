using System.Collections.Immutable;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

using ThermalWatch.Core;

namespace ThermalWatch.Telegram;

/// <summary>Validates Telegram transport and delivers Core-prepared notification candidates.</summary>
public sealed class TelegramNotificationService(
    TelegramOptions options,
    AnomalySnapshotStore snapshotStore,
    NotificationCandidateEngine candidateEngine,
    IHttpClientFactory httpClientFactory,
    ILogger<TelegramNotificationService> logger
) : BackgroundService
{
    private static readonly TimeSpan CommentWaitTimeout = TimeSpan.FromSeconds(seconds: 15);
    private static readonly TimeSpan UpdatePollingRetryDelay = TimeSpan.FromSeconds(seconds: 5);
    private int _manualSendRunning;
    private ValidatedTelegram? _validated;

    /// <summary>Delivers a bounded, priority-ordered manual selection when the notifier is validated.</summary>
    public async Task<ManualTelegramSendResult> SendTopClustersAsync(int requestedClusterCount, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _validated) is null)
            return ManualTelegramSendResult.TelegramUnavailable;

        cancellationToken.ThrowIfCancellationRequested();

        if (Interlocked.CompareExchange(ref _manualSendRunning, value: 1, comparand: 0) != 0)
            return ManualTelegramSendResult.AlreadyRunning;

        try
        {
            ValidatedTelegram? validated = Volatile.Read(ref _validated);

            return validated is null
                ? ManualTelegramSendResult.TelegramUnavailable
                : await ExecuteManualSendAsync(validated, requestedClusterCount, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }
        finally
        {
            Volatile.Write(ref _manualSendRunning, value: 0);
        }
    }

    internal static bool CanReadDiscussion(User bot, ChatMember member)
    {
        return bot.CanReadAllGroupMessages || member is ChatMemberOwner or ChatMemberAdministrator;
    }

    internal static async Task<int> GetInitialUpdateOffsetAsync(ITelegramBotClient client, CancellationToken cancellationToken)
    {
        Update[] updates = await client.GetUpdates(offset: -1, limit: 1, timeout: 0, allowedUpdates: [UpdateType.Message], cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        return updates.Length == 0 ? 0 : updates[^1].Id + 1;
    }

    internal static bool HasConfiguredWebhook(WebhookInfo webhook)
    {
        return !string.IsNullOrEmpty(webhook.Url);
    }

    internal static async Task<TelegramUpdateReceiverStopReason> ReceiveDiscussionUpdatesAsync(
        ITelegramBotClient client,
        int initialUpdateOffset,
        TelegramDiscussionMessageTracker discussionMessages,
        TimeSpan retryDelay,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        int updateOffset = initialUpdateOffset;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Update[] updates = await client.GetUpdates(offset: updateOffset, limit: 100, timeout: 5, allowedUpdates: [UpdateType.Message], cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

                foreach (Update update in updates)
                {
                    updateOffset = update.Id + 1;
                    discussionMessages.Observe(update);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return TelegramUpdateReceiverStopReason.Canceled;
            }
            catch (ApiRequestException exception) when (exception.ErrorCode is 400 or 401 or 403 or 409)
            {
                return TelegramUpdateReceiverStopReason.PermanentFailure;
            }
            catch (Exception exception) when (exception is ApiRequestException or HttpRequestException or IOException or OperationCanceledException)
            {
                TelegramNotificationLog.UpdatePollingFailed(logger);

                try
                {
                    await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return TelegramUpdateReceiverStopReason.Canceled;
                }
            }
        }

        return TelegramUpdateReceiverStopReason.Canceled;
    }

    internal static async Task SendNotificationAsync(
        ITelegramBotClient client,
        ChatId channelId,
        ChatId discussionId,
        TelegramDiscussionMessageTracker discussionMessages,
        PreparedNotificationCandidate candidate,
        TimeSpan commentWaitTimeout,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        TelegramNotificationMessages messages = TelegramMessageFormatter.FormatMessages(
            candidate.Cluster,
            candidate.Preview,
            candidate.PreviewSelection.Dimensions,
            candidate.PreviewSelection.ClusterDiameterKilometers,
            candidate.LandCoverSummary,
            candidate.SettlementName,
            candidate.NearbyFeatures
        );

        Message channelPost = await SendChannelPostAsync(client, channelId, candidate.Preview.PngBytes, messages.MainMessage, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        try
        {
            int? discussionMessageId = await discussionMessages.WaitAsync(channelPost.Id, commentWaitTimeout, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            if (discussionMessageId is null)
            {
                TelegramNotificationLog.CommentFailed(logger, candidate.Cluster.Id);

                return;
            }

            Message comment = await client.SendMessage(
                discussionId,
                messages.CommentMessage,
                parseMode: ParseMode.Html,
                replyParameters: new()
                {
                    MessageId = discussionMessageId.Value
                },
                linkPreviewOptions: new() { IsDisabled = true },
                cancellationToken: cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (comment.Id <= 0)
                TelegramNotificationLog.CommentFailed(logger, candidate.Cluster.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is ApiRequestException or HttpRequestException or IOException or OperationCanceledException)
        {
            TelegramNotificationLog.CommentFailed(logger, candidate.Cluster.Id);
        }
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var runtimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        ValidatedTelegram? validation = await TryValidateAsync(runtimeCancellation.Token).ConfigureAwait(continueOnCapturedContext: false);

        if (validation is not { } validated)
            return;

        Volatile.Write(ref _validated, validated);

        Task updateReceiver = MonitorDiscussionUpdatesAsync(validated, runtimeCancellation.Token);
        Task snapshotProcessor = ProcessSnapshotsAsync(validated, runtimeCancellation.Token);

        try
        {
            Task completed = await Task.WhenAny(updateReceiver, snapshotProcessor).ConfigureAwait(continueOnCapturedContext: false);
            await completed.ConfigureAwait(continueOnCapturedContext: false);
        }
        catch (OperationCanceledException) when (runtimeCancellation.IsCancellationRequested)
        {
            TelegramNotificationLog.ProcessingStopped(logger);
        }
        finally
        {
            await runtimeCancellation.CancelAsync().ConfigureAwait(continueOnCapturedContext: false);

            try
            {
                await Task.WhenAll(updateReceiver, snapshotProcessor).ConfigureAwait(continueOnCapturedContext: false);
            }
            catch (OperationCanceledException) when (runtimeCancellation.IsCancellationRequested)
            {
                TelegramNotificationLog.ProcessingStopped(logger);
            }
            finally
            {
                Volatile.Write(ref _validated, value: null);
            }
        }
    }

    private static bool CanSendToDiscussion(ChatMember member)
    {
        return member switch
        {
            ChatMemberOwner or ChatMemberAdministrator or ChatMemberMember => true,
            ChatMemberRestricted { IsMember: true, CanSendMessages: true } => true,
            _ => false
        };
    }

    private static async Task<Message> SendChannelPostAsync(
        ITelegramBotClient client,
        ChatId channelId,
        ReadOnlyMemory<byte>? pngBytes,
        string mainMessage,
        CancellationToken cancellationToken
    )
    {
        if (pngBytes is { Length: > 0 } payload)
        {
            using var stream = new MemoryStream(payload.ToArray(), writable: false);

            return await client.SendPhoto(
                channelId,
                InputFile.FromStream(stream, fileName: "thermal-anomaly.png"),
                caption: mainMessage,
                parseMode: ParseMode.Html,
                cancellationToken: cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);
        }

        return await client.SendMessage(
            channelId,
            mainMessage,
            parseMode: ParseMode.Html,
            linkPreviewOptions: new() { IsDisabled = true },
            cancellationToken: cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
    }

    private async Task<ManualTelegramSendResult> CompleteEmptyManualSendAsync(ValidatedTelegram validated, int requestedClusterCount, int eligibleClusterCount, CancellationToken cancellationToken)
    {
        var response = new ManualTelegramSendResponse(requestedClusterCount, eligibleClusterCount, SelectedClusterCount: 0, SentClusterCount: 0, FailedClusterCount: 0, []);

        bool statusSent = await TrySendManualStatusAsync(validated, message: "ℹ️ <b>No anomaly clusters currently pass all filters</b>", cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        LogManualSendSummary(response);

        return statusSent
            ? ManualTelegramSendResult.Completed(response)
            : ManualTelegramSendResult.StatusMessageFailed;
    }

    private TelegramBotClient CreateTelegramClient(CancellationToken cancellationToken)
    {
        string botToken = options.BotToken
            ?? throw new InvalidOperationException(message: "Telegram validation requires a configured bot token.");

        var clientOptions = new TelegramBotClientOptions(botToken)
        {
            RetryCount = 2,
            RetryThreshold = 30
        };

        return new TelegramBotClient(clientOptions, httpClientFactory.CreateClient(name: "Telegram"), cancellationToken);
    }

    private void DisableValidatedTelegram(ValidatedTelegram validated)
    {
        if (ReferenceEquals(Volatile.Read(ref _validated), validated))
            Volatile.Write(ref _validated, value: null);
    }

    private async Task<ManualTelegramSendResult> ExecuteManualSendAsync(ValidatedTelegram validated, int requestedClusterCount, CancellationToken cancellationToken)
    {
        ManualNotificationCandidateSelection preparation = await candidateEngine.PrepareManualCandidatesAsync(snapshotStore.Current, requestedClusterCount, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        if (preparation.SelectedCandidates.IsEmpty)
            return await CompleteEmptyManualSendAsync(validated, requestedClusterCount, preparation.EligibleClusterCount, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        bool statusSent = await TrySendManualStatusAsync(validated, $"🔥 <b>Sending top {preparation.SelectedCandidates.Length} anomaly clusters manually</b>", cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        if (!statusSent)
        {
            var response = new ManualTelegramSendResponse(
                requestedClusterCount,
                preparation.EligibleClusterCount,
                preparation.SelectedCandidates.Length,
                SentClusterCount: 0,
                FailedClusterCount: 0,
                []
            );

            LogManualSendSummary(response);

            return ManualTelegramSendResult.StatusMessageFailed;
        }

        ManualDeliverySummary delivery = await SendManualNotificationsAsync(validated, preparation.SelectedCandidates, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        var completedResponse = new ManualTelegramSendResponse(
            requestedClusterCount,
            preparation.EligibleClusterCount,
            preparation.SelectedCandidates.Length,
            delivery.SentClusterCount,
            delivery.FailedClusterIds.Length,
            delivery.FailedClusterIds
        );

        LogManualSendSummary(completedResponse);

        return ManualTelegramSendResult.Completed(completedResponse);
    }

    private void LogManualSendSummary(ManualTelegramSendResponse response)
    {
        TelegramNotificationLog.ManualSendSummary(
            logger,
            response.RequestedClusterCount,
            response.EligibleClusterCount,
            response.SelectedClusterCount,
            response.SentClusterCount,
            response.FailedClusterCount
        );
    }

    private void LogProcessingSummary(AutomaticNotificationProcessingSummary summary)
    {
        bool hasVisibilitySummary = logger.IsEnabled(LogLevel.Information)
            && (summary.ActiveClusterCount > 0
            || summary.DeliveredClusterCount > 0
            || summary.RejectedClusterCount > 0
            || summary.DuplicateEpisodeCount > 0
            || summary.SendFailureCount > 0);

        if (hasVisibilitySummary)
        {
            int nighttimeCount = summary.RejectionCount(NotificationRejectionReason.Nighttime);
            int insufficientDetectionsCount = summary.RejectionCount(NotificationRejectionReason.InsufficientDetections);
            int lowConfidenceCount = summary.RejectionCount(NotificationRejectionReason.LowConfidence);
            int lowFrpCount = summary.RejectionCount(NotificationRejectionReason.LowFrp);
            int lowClusterTotalFrpCount = summary.RejectionCount(NotificationRejectionReason.LowClusterTotalFrp);
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
                lowClusterTotalFrpCount,
                lowThermalContrastCount,
                missingRequiredValueCount,
                previewUnavailableCount
            );
        }

        if (summary.LandCoverCandidateCount > 0)
        {
            TelegramNotificationLog.LandCoverSummary(
                logger,
                summary.LandCoverCandidateCount,
                summary.VegetationSuppressedCount,
                summary.LandCoverUnavailableCount,
                summary.LandCoverYear
            );
        }
    }

    private async Task MonitorDiscussionUpdatesAsync(ValidatedTelegram validated, CancellationToken cancellationToken)
    {
        TelegramUpdateReceiverStopReason result = await ReceiveDiscussionUpdatesAsync(
            validated.Client,
            validated.InitialUpdateOffset,
            validated.DiscussionMessages,
            UpdatePollingRetryDelay,
            logger,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (result == TelegramUpdateReceiverStopReason.PermanentFailure)
        {
            DisableValidatedTelegram(validated);
            TelegramNotificationLog.PermanentUpdatePollingFailure(logger);
        }
    }

    private async Task ProcessSnapshotsAsync(ValidatedTelegram validated, CancellationToken cancellationToken)
    {
        await foreach (AnomalySnapshot snapshot in snapshotStore.ReadUpdatesAsync(cancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false))
        {
            if (!ReferenceEquals(Volatile.Read(ref _validated), validated))
                return;

            AutomaticNotificationProcessingResult result = await candidateEngine.ProcessAutomaticNotificationsAsync(snapshot, (candidate, token) => TryDeliverAutomaticAsync(validated, candidate, token), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            LogProcessingSummary(result.Summary);

            if (!result.ContinueProcessing)
                return;
        }
    }

    private async Task<ManualDeliverySummary> SendManualNotificationsAsync(
        ValidatedTelegram validated,
        ImmutableArray<PreparedNotificationCandidate> selectedCandidates,
        CancellationToken cancellationToken
    )
    {
        int sentClusterCount = 0;
        var failedClusterIds = new List<string>();

        foreach (PreparedNotificationCandidate candidate in selectedCandidates)
        {
            try
            {
                await SendNotificationAsync(validated, candidate, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                sentClusterCount++;
                TelegramNotificationLog.ManualNotificationSent(logger, candidate.Cluster.Id);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is ApiRequestException or HttpRequestException or IOException or OperationCanceledException)
            {
                failedClusterIds.Add(candidate.Cluster.Id);
                TelegramNotificationLog.ManualNotificationFailed(logger, candidate.Cluster.Id);
            }
        }

        return new(sentClusterCount, [.. failedClusterIds]);
    }

    private Task SendNotificationAsync(ValidatedTelegram validated, PreparedNotificationCandidate candidate, CancellationToken cancellationToken)
    {
        return SendNotificationAsync(
            validated.Client,
            validated.ChannelId,
            validated.DiscussionId,
            validated.DiscussionMessages,
            candidate,
            CommentWaitTimeout,
            logger,
            cancellationToken
        );
    }

    private async Task<NotificationDeliveryOutcome> TryDeliverAutomaticAsync(ValidatedTelegram validated, PreparedNotificationCandidate candidate, CancellationToken cancellationToken)
    {
        try
        {
            await SendNotificationAsync(validated, candidate, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            TelegramNotificationLog.NotificationSent(logger, candidate.Cluster.Id, candidate.Cluster.Representative.Satellite, candidate.Cluster.Representative.AcquiredAtUtc);

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
        catch (Exception exception) when (exception is ApiRequestException or HttpRequestException or IOException or OperationCanceledException)
        {
            TelegramNotificationLog.TransientSendFailure(logger, candidate.Cluster.Id);

            return NotificationDeliveryOutcome.RetryLater;
        }
    }

    private async Task<long?> TryResolveDiscussionAsync(ITelegramBotClient client, ChatFullInfo channel, User bot, CancellationToken cancellationToken)
    {
        if (channel.LinkedChatId is not { } discussionChatId)
        {
            TelegramNotificationLog.MissingLinkedDiscussion(logger);

            return null;
        }

        var discussionId = new ChatId(discussionChatId);
        ChatFullInfo discussion = await client.GetChat(discussionId, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        if (discussion.Type != ChatType.Supergroup || discussion.LinkedChatId != channel.Id)
        {
            TelegramNotificationLog.InvalidLinkedDiscussion(logger);

            return null;
        }

        ChatMember member = await client.GetChatMember(discussionId, bot.Id, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        if (!CanSendToDiscussion(member))
        {
            TelegramNotificationLog.CannotComment(logger);

            return null;
        }

        if (!CanReadDiscussion(bot, member))
        {
            TelegramNotificationLog.CannotReadDiscussion(logger);

            return null;
        }

        return discussionChatId;
    }

    private async Task<bool> TrySendManualStatusAsync(ValidatedTelegram validated, string message, CancellationToken cancellationToken)
    {
        try
        {
            Message status = await validated.Client.SendMessage(
                validated.ChannelId,
                message,
                parseMode: ParseMode.Html,
                linkPreviewOptions: new() { IsDisabled = true },
                cancellationToken: cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false);

            return status.Id > 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is ApiRequestException or HttpRequestException or IOException or OperationCanceledException)
        {
            TelegramNotificationLog.ManualStatusFailed(logger);

            return false;
        }
    }

    private async Task<ValidatedTelegram?> TryValidateAsync(CancellationToken cancellationToken)
    {
        try
        {
            TelegramBotClient client = CreateTelegramClient(cancellationToken);

            return await ValidateClientAsync(client, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ApiRequestException exception) when (exception.ErrorCode == 409)
        {
            TelegramNotificationLog.UpdateConsumerConflict(logger);

            return null;
        }
        catch (Exception exception) when (exception is ApiRequestException or HttpRequestException or IOException or OperationCanceledException)
        {
            TelegramNotificationLog.ValidationFailed(logger);

            return null;
        }
    }

    private async Task<ValidatedTelegram?> ValidateClientAsync(ITelegramBotClient client, CancellationToken cancellationToken)
    {
        WebhookInfo webhook = await client.GetWebhookInfo(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        if (HasConfiguredWebhook(webhook))
        {
            TelegramNotificationLog.WebhookConfigured(logger);

            return null;
        }

        string configuredChannel = options.ChannelId
            ?? throw new InvalidOperationException(message: "Telegram validation requires a configured channel.");

        var channelId = new ChatId(configuredChannel);
        User bot = await client.GetMe(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        ChatFullInfo channel = await client.GetChat(channelId, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        if (channel.Type != ChatType.Channel)
        {
            TelegramNotificationLog.InvalidChannel(logger);

            return null;
        }

        ChatMember channelMember = await client.GetChatMember(channelId, bot.Id, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        if (channelMember is not (ChatMemberOwner or ChatMemberAdministrator { CanPostMessages: true }))
        {
            TelegramNotificationLog.CannotPost(logger);

            return null;
        }

        long? discussionChatId = await TryResolveDiscussionAsync(client, channel, bot, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        if (discussionChatId is null)
            return null;

        int initialUpdateOffset = await GetInitialUpdateOffsetAsync(client, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        TelegramNotificationLog.Validated(logger);

        return new(
            client,
            channelId,
            new ChatId(discussionChatId.Value),
            new TelegramDiscussionMessageTracker(channel.Id, discussionChatId.Value),
            initialUpdateOffset
        );
    }

    private sealed record ManualDeliverySummary(int SentClusterCount, ImmutableArray<string> FailedClusterIds);

    private sealed record ValidatedTelegram(
        ITelegramBotClient Client,
        ChatId ChannelId,
        ChatId DiscussionId,
        TelegramDiscussionMessageTracker DiscussionMessages,
        int InitialUpdateOffset
    );
}
