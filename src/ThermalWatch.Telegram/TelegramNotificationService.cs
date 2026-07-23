using System.Collections.Immutable;
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
    GibsClient gibsClient,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider,
    ILogger<TelegramNotificationService> logger) : BackgroundService
{
    private const int MaximumSeenIds = 100_000;
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly TelegramAutomaticNotificationState _automaticState = new(
        options.ClusterRadiusKilometers,
        options.ClusterTimeWindow,
        options.SeenRetention);
    private readonly SemaphoreSlim _manualSendGate = new(initialCount: 1, maxCount: 1);
    private ValidatedTelegram? _validated;
    private bool _firstReadySnapshot = true;

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

                if (!snapshot.IsReady)
                    continue;

                var summary = new VisibilityProcessingSummary();
                var landCoverSummary = new LandCoverProcessingSummary();
                await TrackNewDetectionsAsync(
                    snapshot,
                    summary,
                    landCoverSummary,
                    stoppingToken).ConfigureAwait(false);
                bool continueRunning = await SendPendingAsync(
                    validated,
                    summary,
                    stoppingToken).ConfigureAwait(false);
                LogVisibilitySummary(summary);
                LogLandCoverSummary(landCoverSummary);
                if (!continueRunning)
                    return;
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _validated, value: null, validated);
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

    private async Task<ManualTelegramSendResult> ExecuteManualSendAsync(
        ValidatedTelegram validated,
        int requestedCount,
        CancellationToken cancellationToken)
    {
        AnomalySnapshot snapshot = snapshotStore.Current;
        ImmutableArray<NotificationCluster> clusters = TelegramNotificationClustering.Create(
            snapshot.Items,
            snapshot.Items,
            options.ClusterRadiusKilometers,
            options.ClusterTimeWindow,
            includeActiveContext: false);
        List<PreparedNotification> eligible = await GetEligibleNotificationsAsync(
            clusters,
            cancellationToken).ConfigureAwait(false);
        PreparedNotification[] selected = SelectTopNotifications(eligible, requestedCount);

        if (selected.Length == 0)
        {
            return await CompleteEmptyManualSendAsync(
                validated,
                requestedCount,
                eligible.Count,
                cancellationToken).ConfigureAwait(false);
        }

        if (!await TrySendManualStatusAsync(
            validated,
            message: $"🔥 <b>Sending top {selected.Length} anomalies manually</b>",
            cancellationToken).ConfigureAwait(false))
        {
            var response = new ManualTelegramSendResponse(
                requestedCount,
                eligible.Count,
                selected.Length,
                SentCount: 0,
                FailedCount: 0,
                []);
            LogManualSendSummary(response);
            return ManualTelegramSendResult.StatusMessageFailed;
        }

        (int sentCount, List<string> failedIds) = await SendManualNotificationsAsync(
            validated,
            selected,
            cancellationToken).ConfigureAwait(false);

        var completedResponse = new ManualTelegramSendResponse(
            requestedCount,
            eligible.Count,
            selected.Length,
            sentCount,
            failedIds.Count,
            [.. failedIds]);
        LogManualSendSummary(completedResponse);
        return ManualTelegramSendResult.Completed(completedResponse);
    }

    private async Task<List<PreparedNotification>> GetEligibleNotificationsAsync(
        ImmutableArray<NotificationCluster> clusters,
        CancellationToken cancellationToken)
    {
        var eligible = new List<PreparedNotification>();
        foreach (NotificationCluster cluster in clusters)
        {
            ClusterEvaluation evaluation = await EvaluateClusterAsync(cluster, cancellationToken).ConfigureAwait(false);
            if (!evaluation.IsAccepted)
                continue;

            TelegramPreviewSelection previewSelection = SelectPreview(cluster);
            GibsPreview preview = await gibsClient.GetPreviewAsync(
                cluster.Representative,
                previewSelection.Dimensions,
                cancellationToken).ConfigureAwait(false);
            if (!preview.IsAvailable
                && options.Visibility.Enabled
                && options.Visibility.RequirePreview)
            {
                TelegramNotificationLog.ManualPreviewUnavailable(logger, cluster.Id);
                continue;
            }

            eligible.Add(new(
                cluster,
                preview,
                previewSelection,
                evaluation.LandCoverResult?.FormattingSummary));
        }

        return eligible;
    }

    private static PreparedNotification[] SelectTopNotifications(
        List<PreparedNotification> eligible,
        int requestedCount) =>
        [.. eligible
            .OrderByDescending(candidate => candidate.Cluster.Representative.FrpMegawatts.HasValue)
            .ThenByDescending(candidate => candidate.Cluster.Representative.FrpMegawatts)
            .ThenByDescending(candidate => candidate.Cluster.Members.Length)
            .ThenByDescending(candidate => candidate.PreviewSelection.ClusterDiameterKilometers)
            .ThenByDescending(candidate => candidate.Cluster.Representative.AcquiredAtUtc)
            .ThenBy(candidate => candidate.Cluster.Id, StringComparer.Ordinal)
            .Take(requestedCount)];

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
        PreparedNotification[] selected,
        CancellationToken cancellationToken)
    {
        int sentCount = 0;
        var failedIds = new List<string>();
        foreach (PreparedNotification candidate in selected)
        {
            try
            {
                await SendNotificationAsync(
                    validated,
                    candidate.Cluster,
                    candidate.Preview,
                    candidate.PreviewSelection,
                    candidate.LandCoverSummary,
                    cancellationToken).ConfigureAwait(false);
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

    private async Task TrackNewDetectionsAsync(
        AnomalySnapshot snapshot,
        VisibilityProcessingSummary summary,
        LandCoverProcessingSummary landCoverSummary,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        ExpireSeen(now);
        _automaticState.Expire(now);

        if (_firstReadySnapshot && !options.NotifyExistingOnStartup)
        {
            foreach (Anomaly detection in snapshot.Items)
                _seen[detection.Id] = now;

            _firstReadySnapshot = false;
            TrimSeen();
            TelegramNotificationLog.DeduplicationPrimed(logger, snapshot.Items.Length);
            return;
        }

        _firstReadySnapshot = false;
        Anomaly[] newDetections = [.. snapshot.Items.Where(detection => !_seen.ContainsKey(detection.Id))];

        foreach (Anomaly detection in newDetections)
            _seen[detection.Id] = now;

        TrimSeen();
        ImmutableArray<NotificationCluster> clusters = TelegramNotificationClustering.Create(
            snapshot.Items,
            newDetections,
            options.ClusterRadiusKilometers,
            options.ClusterTimeWindow,
            options.Visibility.Enabled && options.Visibility.MinimumClusterDetections > 1);

        await TrackClustersAsync(
            clusters,
            now,
            summary,
            landCoverSummary,
            cancellationToken).ConfigureAwait(false);

        if (newDetections.Length > 0)
        {
            TelegramNotificationLog.ZonesCreated(
                logger,
                clusters.Length,
                newDetections.Length);
        }
    }

    private async Task TrackClustersAsync(
        ImmutableArray<NotificationCluster> clusters,
        DateTimeOffset now,
        VisibilityProcessingSummary summary,
        LandCoverProcessingSummary landCoverSummary,
        CancellationToken cancellationToken)
    {
        foreach (NotificationCluster cluster in clusters)
        {
            summary.AddCandidate();
            TelegramCandidatePreparation preparation = _automaticState.PrepareCandidate(cluster, now);
            if (preparation.ContinuesDeliveredEpisode)
            {
                summary.AddDuplicateEpisode();
                TelegramNotificationLog.DeliveredEpisodeSuppressed(logger, preparation.Cluster.Id);
                continue;
            }

            NotificationCluster preparedCluster = preparation.Cluster;
            ClusterEvaluation evaluation = await EvaluateClusterAsync(preparedCluster, cancellationToken).ConfigureAwait(false);
            if (evaluation.VisibilityRejectionReason is { } visibilityRejectionReason)
            {
                summary.Reject(visibilityRejectionReason);
                continue;
            }

            RecordLandCoverResult(landCoverSummary, evaluation.LandCoverResult);
            if (!evaluation.IsAccepted)
                continue;

            _automaticState.AddPending(new(
                preparedCluster,
                preparation.FirstSeenUtc,
                SelectPreview(preparedCluster),
                evaluation.LandCoverResult?.FormattingSummary));
        }
    }

    private static void RecordLandCoverResult(
        LandCoverProcessingSummary summary,
        LandCoverFilterResult? result)
    {
        if (result is not { } landCoverResult)
            return;

        summary.AddCandidate();
        if (landCoverResult.LandCoverYear is { } landCoverYear)
            summary.RecordYear(landCoverYear);
        if (landCoverResult.Decision == LandCoverFilterDecision.Unavailable)
            summary.AddUnavailable();
        else if (landCoverResult.Decision == LandCoverFilterDecision.Suppressed)
            summary.AddSuppressed();
    }

    private async Task<ClusterEvaluation> EvaluateClusterAsync(
        NotificationCluster cluster,
        CancellationToken cancellationToken)
    {
        VisibilityFilterResult visibilityResult = TelegramVisibilityFilter.EvaluateMetadata(
            cluster,
            options.Visibility);
        if (!visibilityResult.IsAccepted)
        {
            VisibilityRejectionReason reason = visibilityResult.RejectionReason!.Value;
            TelegramNotificationLog.VisibilityRejected(
                logger,
                cluster.Id,
                reason);
            return new(IsAccepted: false, reason, LandCoverResult: null);
        }

        if (!options.LandCover.Enabled)
            return ClusterEvaluation.Accepted;

        LandCoverFilterResult landCoverResult = await TelegramLandCoverFilter.EvaluateAsync(
            cluster,
            options.LandCover,
            gibsClient,
            cancellationToken).ConfigureAwait(false);
        if (landCoverResult.LandCoverYear is { } landCoverYear)
        {
            TelegramNotificationLog.LandCoverYearSelected(
                logger,
                landCoverYear,
                cluster.Id);
        }

        if (landCoverResult.Decision == LandCoverFilterDecision.Unavailable)
        {
            TelegramNotificationLog.LandCoverUnavailable(
                logger,
                cluster.Id,
                landCoverResult.Reason);
        }
        else if (landCoverResult.Decision == LandCoverFilterDecision.Suppressed)
        {
            TelegramNotificationLog.LandCoverSuppressed(
                logger,
                cluster.Id,
                landCoverResult.Reason);
        }

        return new(
            landCoverResult.Decision != LandCoverFilterDecision.Suppressed,
            VisibilityRejectionReason: null,
            landCoverResult);
    }

    private async Task<bool> SendPendingAsync(
        ValidatedTelegram validated,
        VisibilityProcessingSummary summary,
        CancellationToken cancellationToken)
    {
        for (int index = 0; index < _automaticState.PendingCount;)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            PendingTelegramNotification pending = _automaticState.GetPending(index);
            if (_automaticState.TrySuppressPending(index, now))
            {
                summary.AddDuplicateEpisode();
                TelegramNotificationLog.PendingDeliveredEpisodeSuppressed(logger, pending.Cluster.Id);
                continue;
            }

            GibsPreview preview = await gibsClient.GetPreviewAsync(
                pending.Cluster.Representative,
                pending.PreviewSelection.Dimensions,
                cancellationToken).ConfigureAwait(false);
            bool previewExpired = now - pending.FirstSeenUtc >= options.PreviewRetryWindow;

            if (!preview.IsAvailable && !previewExpired)
            {
                summary.AddPendingPreview();
                index++;
                continue;
            }

            if (!preview.IsAvailable
                && options.Visibility.Enabled
                && options.Visibility.RequirePreview)
            {
                summary.Reject(VisibilityRejectionReason.PreviewUnavailable);
                summary.AddPreviewTimeout();
                TelegramNotificationLog.PreviewRetryExpired(logger, pending.Cluster.Id);
                _automaticState.RemovePendingAt(index);
                continue;
            }

            PendingSendResult sendResult = await TrySendPendingNotificationAsync(
                validated,
                pending,
                preview,
                summary,
                index,
                cancellationToken).ConfigureAwait(false);
            if (sendResult == PendingSendResult.Stop)
                return false;
            if (sendResult == PendingSendResult.RetryLater)
                return true;
        }

        return true;
    }

    private async Task<PendingSendResult> TrySendPendingNotificationAsync(
        ValidatedTelegram validated,
        PendingTelegramNotification pending,
        GibsPreview preview,
        VisibilityProcessingSummary summary,
        int pendingIndex,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendNotificationAsync(
                validated,
                pending.Cluster,
                preview,
                pending.PreviewSelection,
                pending.LandCoverSummary,
                cancellationToken).ConfigureAwait(false);

            _automaticState.RecordDelivered(pending.Cluster, timeProvider.GetUtcNow());
            TelegramNotificationLog.NotificationSent(
                logger,
                pending.Cluster.Id,
                pending.Cluster.Representative.Satellite,
                pending.Cluster.Representative.AcquiredAtUtc);
            summary.AddAccepted();
            _automaticState.RemovePendingAt(pendingIndex);
            return PendingSendResult.Sent;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ApiRequestException exception) when (exception.ErrorCode is 400 or 401 or 403)
        {
            summary.AddSendFailure();
            DisableValidatedTelegram(validated);
            TelegramNotificationLog.PermanentSendFailure(logger);
            return PendingSendResult.Stop;
        }
        catch (Exception)
        {
            summary.AddSendFailure();
            TelegramNotificationLog.TransientSendFailure(logger, pending.Cluster.Id);
            return PendingSendResult.RetryLater;
        }
    }

    private static async Task SendNotificationAsync(
        ValidatedTelegram validated,
        NotificationCluster cluster,
        GibsPreview preview,
        TelegramPreviewSelection previewSelection,
        string? landCoverSummary,
        CancellationToken cancellationToken)
    {
        InlineKeyboardMarkup keyboard = CreateLocationKeyboard(cluster);
        string message = TelegramMessageFormatter.Format(
            cluster,
            preview,
            previewSelection.Dimensions,
            previewSelection.ClusterDiameterKilometers,
            landCoverSummary);

        if (preview.PngBytes is { } pngBytes)
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
            handler: $"https://yandex.com/maps/?ll={representative.Longitude:0.######}%2C{representative.Latitude:0.######}&pt={representative.Longitude:0.######}%2C{representative.Latitude:0.######}&z=12&l=map");
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

    private TelegramPreviewSelection SelectPreview(NotificationCluster cluster)
    {
        Anomaly representative = cluster.Representative;
        double clusterDiameterKilometers = Geography.ClusterDiameterKilometers(cluster.Members);
        TelegramPreviewOptions previewOptions = options.Preview;
        bool isLargePreview =
            cluster.Members.Length >= previewOptions.LargeClusterMinimumDetections
            || representative.FrpMegawatts is { } frp
                && frp >= previewOptions.LargeClusterMinimumFrpMegawatts
            || clusterDiameterKilometers >= previewOptions.LargeClusterMinimumDiameterKilometers;
        TelegramPreviewSize previewSize = isLargePreview
            ? previewOptions.LargePreviewSize
            : previewOptions.PreviewSize;
        var dimensions = new GibsPreviewDimensions(
            previewSize.WidthKilometers,
            previewSize.HeightKilometers,
            previewOptions.PixelWidth,
            previewOptions.PixelHeight);

        TelegramNotificationLog.PreviewSizeSelected(
            logger,
            cluster.Id,
            cluster.Members.Length,
            representative.FrpMegawatts,
            clusterDiameterKilometers,
            isLargePreview,
            dimensions.WidthKilometers,
            dimensions.HeightKilometers,
            dimensions.PixelWidth,
            dimensions.PixelHeight);

        return new(dimensions, clusterDiameterKilometers);
    }

    private void LogVisibilitySummary(VisibilityProcessingSummary summary)
    {
        if (!options.Visibility.Enabled
            || !summary.HasActivity)
        {
            return;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            int nighttimeCount = summary.RejectionCount(VisibilityRejectionReason.Nighttime);
            int insufficientDetectionsCount = summary.RejectionCount(VisibilityRejectionReason.InsufficientDetections);
            int lowConfidenceCount = summary.RejectionCount(VisibilityRejectionReason.LowConfidence);
            int lowFrpCount = summary.RejectionCount(VisibilityRejectionReason.LowFrp);
            int lowThermalContrastCount = summary.RejectionCount(VisibilityRejectionReason.LowThermalContrast);
            int missingRequiredValueCount = summary.RejectionCount(VisibilityRejectionReason.MissingRequiredValue);
            int previewUnavailableCount = summary.RejectionCount(VisibilityRejectionReason.PreviewUnavailable);
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
    }

    private void LogLandCoverSummary(LandCoverProcessingSummary summary)
    {
        if (!options.LandCover.Enabled || !summary.HasActivity)
            return;

        TelegramNotificationLog.LandCoverSummary(
            logger,
            summary.CandidateClusterCount,
            summary.VegetationSuppressedCount,
            summary.LandCoverUnavailableCount,
            summary.LandCoverYear);
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

    private void ExpireSeen(DateTimeOffset now)
    {
        DateTimeOffset cutoff = now - options.SeenRetention;
        foreach (string id in _seen.Where(pair => pair.Value < cutoff).Select(pair => pair.Key).ToArray())
            _seen.Remove(id);
    }

    private void TrimSeen()
    {
        int excess = _seen.Count - MaximumSeenIds;
        if (excess <= 0)
            return;

        foreach (string id in _seen.OrderBy(pair => pair.Value).Take(excess).Select(pair => pair.Key).ToArray())
            _seen.Remove(id);
    }

    private sealed record PreparedNotification(
        NotificationCluster Cluster,
        GibsPreview Preview,
        TelegramPreviewSelection PreviewSelection,
        string? LandCoverSummary);

    private sealed record ValidatedTelegram(TelegramBotClient Client, ChatId ChatId);

    private enum PendingSendResult
    {
        Sent,
        Stop,
        RetryLater
    }

    private readonly record struct ClusterEvaluation(
        bool IsAccepted,
        VisibilityRejectionReason? VisibilityRejectionReason,
        LandCoverFilterResult? LandCoverResult)
    {
        public static ClusterEvaluation Accepted { get; } = new(IsAccepted: true, VisibilityRejectionReason: null, LandCoverResult: null);
    }

    private sealed class VisibilityProcessingSummary
    {
        private readonly Dictionary<VisibilityRejectionReason, int> _rejectionCounts = [];

        public int CandidateClusterCount { get; private set; }

        public int AcceptedClusterCount { get; private set; }

        public int RejectedClusterCount { get; private set; }

        public int DuplicateEpisodeCount { get; private set; }

        public int PendingPreviewCount { get; private set; }

        public int PreviewTimeoutCount { get; private set; }

        public int SendFailureCount { get; private set; }

        public bool HasActivity =>
            CandidateClusterCount > 0
            || AcceptedClusterCount > 0
            || RejectedClusterCount > 0
            || DuplicateEpisodeCount > 0
            || PendingPreviewCount > 0
            || PreviewTimeoutCount > 0
            || SendFailureCount > 0;

        public void AddCandidate() => CandidateClusterCount++;

        public void AddAccepted() => AcceptedClusterCount++;

        public void AddDuplicateEpisode() => DuplicateEpisodeCount++;

        public void AddPendingPreview() => PendingPreviewCount++;

        public void AddPreviewTimeout() => PreviewTimeoutCount++;

        public void AddSendFailure() => SendFailureCount++;

        public void Reject(VisibilityRejectionReason reason)
        {
            RejectedClusterCount++;
            _rejectionCounts[reason] = RejectionCount(reason) + 1;
        }

        public int RejectionCount(VisibilityRejectionReason reason) =>
            _rejectionCounts.GetValueOrDefault(reason);
    }

    private sealed class LandCoverProcessingSummary
    {
        public int CandidateClusterCount { get; private set; }

        public int VegetationSuppressedCount { get; private set; }

        public int LandCoverUnavailableCount { get; private set; }

        public int? LandCoverYear { get; private set; }

        public bool HasActivity => CandidateClusterCount > 0;

        public void AddCandidate() => CandidateClusterCount++;

        public void AddSuppressed() => VegetationSuppressedCount++;

        public void AddUnavailable() => LandCoverUnavailableCount++;

        public void RecordYear(int year) =>
            LandCoverYear = LandCoverYear is { } current ? Math.Max(current, year) : year;
    }
}
