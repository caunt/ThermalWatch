using System.Collections.Immutable;
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
    private readonly List<PendingNotification> _pending = [];
    private readonly SemaphoreSlim _manualSendGate = new(1, 1);
    private ValidatedTelegram? _validated;
    private bool _firstReadySnapshot = true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var validation = await TryValidateAsync(stoppingToken);
        if (validation is not { } validated)
            return;

        Volatile.Write(ref _validated, validated);
        try
        {
            await foreach (var snapshot in snapshotStore.ReadUpdatesAsync(stoppingToken))
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
                    stoppingToken);
                var continueRunning = await SendPendingAsync(
                    validated,
                    summary,
                    stoppingToken);
                LogVisibilitySummary(summary);
                LogLandCoverSummary(landCoverSummary);
                if (!continueRunning)
                    return;
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _validated, null, validated);
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
                httpClientFactory.CreateClient("Telegram"),
                cancellationToken);
            var chatId = new ChatId(options.ChannelId!);
            var bot = await client.GetMe(cancellationToken);
            var chat = await client.GetChat(chatId, cancellationToken);

            if (chat.Type != ChatType.Channel)
            {
                logger.LogError("Telegram notifier disabled: TELEGRAM_CHANNEL_ID is not a channel");
                return null;
            }

            var member = await client.GetChatMember(chatId, bot.Id, cancellationToken);
            if (member is not ChatMemberOwner
                && member is not ChatMemberAdministrator { CanPostMessages: true })
            {
                logger.LogError("Telegram notifier disabled: bot cannot post to the configured channel");
                return null;
            }

            logger.LogInformation("Telegram notifier validated and enabled");
            return new(client, chatId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            logger.LogError("Telegram notifier disabled: validation failed");
            return null;
        }
    }

    public async Task<ManualTelegramSendResult> SendTopAsync(
        int requestedCount,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _validated) is null)
            return ManualTelegramSendResult.TelegramUnavailable;

        if (!_manualSendGate.Wait(0))
            return ManualTelegramSendResult.AlreadyRunning;

        try
        {
            var validated = Volatile.Read(ref _validated);
            if (validated is null)
                return ManualTelegramSendResult.TelegramUnavailable;

            return await ExecuteManualSendAsync(
                validated,
                requestedCount,
                cancellationToken);
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
        var snapshot = snapshotStore.Current;
        var clusters = TelegramNotificationClustering.Create(
            snapshot.Items,
            snapshot.Items,
            options.ClusterRadiusKilometers,
            options.ClusterTimeWindow,
            includeActiveContext: false);
        var eligible = new List<PreparedNotification>();

        foreach (var cluster in clusters)
        {
            var evaluation = await EvaluateClusterAsync(cluster, cancellationToken);
            if (!evaluation.IsAccepted)
                continue;

            var previewSelection = SelectPreview(cluster);
            var preview = await gibsClient.GetPreviewAsync(
                cluster.Representative,
                previewSelection.Dimensions,
                cancellationToken);
            if (!preview.IsAvailable
                && options.Visibility.Enabled
                && options.Visibility.RequirePreview)
            {
                logger.LogDebug(
                    "Visibility filter rejected manual Telegram cluster {NotificationId}: preview unavailable",
                    cluster.Id);
                continue;
            }

            eligible.Add(new(
                cluster,
                preview,
                previewSelection,
                evaluation.LandCoverResult?.FormattingSummary));
        }

        var selected = eligible
            .OrderByDescending(candidate => candidate.Cluster.Representative.FrpMegawatts.HasValue)
            .ThenByDescending(candidate => candidate.Cluster.Representative.FrpMegawatts)
            .ThenByDescending(candidate => candidate.Cluster.Members.Length)
            .ThenByDescending(candidate => candidate.PreviewSelection.ClusterDiameterKilometers)
            .ThenByDescending(candidate => candidate.Cluster.Representative.AcquiredAtUtc)
            .ThenBy(candidate => candidate.Cluster.Id, StringComparer.Ordinal)
            .Take(requestedCount)
            .ToArray();

        if (selected.Length == 0)
        {
            var response = new ManualTelegramSendResponse(
                requestedCount,
                eligible.Count,
                0,
                0,
                0,
                []);
            if (!await TrySendManualStatusAsync(
                validated,
                "ℹ️ <b>No anomalies currently pass all filters</b>",
                cancellationToken))
            {
                LogManualSendSummary(response);
                return ManualTelegramSendResult.StatusMessageFailed;
            }

            LogManualSendSummary(response);
            return ManualTelegramSendResult.Completed(response);
        }

        if (!await TrySendManualStatusAsync(
            validated,
            $"🔥 <b>Sending top {selected.Length} anomalies manually</b>",
            cancellationToken))
        {
            var response = new ManualTelegramSendResponse(
                requestedCount,
                eligible.Count,
                selected.Length,
                0,
                0,
                []);
            LogManualSendSummary(response);
            return ManualTelegramSendResult.StatusMessageFailed;
        }

        var sentCount = 0;
        var failedIds = new List<string>();
        foreach (var candidate in selected)
        {
            try
            {
                await SendNotificationAsync(
                    validated,
                    candidate.Cluster,
                    candidate.Preview,
                    candidate.PreviewSelection,
                    candidate.LandCoverSummary,
                    cancellationToken);
                sentCount++;
                logger.LogInformation(
                    "Sent manual Telegram notification {NotificationId}",
                    candidate.Cluster.Id);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                failedIds.Add(candidate.Cluster.Id);
                logger.LogWarning(
                    "Manual Telegram notification failed for {NotificationId}",
                    candidate.Cluster.Id);
            }
        }

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
                cancellationToken: cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            logger.LogWarning("Manual Telegram introductory message failed");
            return false;
        }
    }

    private async Task TrackNewDetectionsAsync(
        AnomalySnapshot snapshot,
        VisibilityProcessingSummary summary,
        LandCoverProcessingSummary landCoverSummary,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        ExpireSeen(now);

        if (_firstReadySnapshot && !options.NotifyExistingOnStartup)
        {
            foreach (var detection in snapshot.Items)
                _seen[detection.Id] = now;

            _firstReadySnapshot = false;
            TrimSeen();
            logger.LogInformation(
                "Primed Telegram deduplication with {NewDetectionCount} existing detections",
                snapshot.Items.Length);
            return;
        }

        _firstReadySnapshot = false;
        var newDetections = snapshot.Items
            .Where(detection => !_seen.ContainsKey(detection.Id))
            .ToArray();

        foreach (var detection in newDetections)
            _seen[detection.Id] = now;

        TrimSeen();
        var clusters = TelegramNotificationClustering.Create(
            snapshot.Items,
            newDetections,
            options.ClusterRadiusKilometers,
            options.ClusterTimeWindow,
            options.Visibility.Enabled && options.Visibility.MinimumClusterDetections > 1);

        foreach (var cluster in clusters)
        {
            summary.AddCandidate();
            var evaluation = await EvaluateClusterAsync(cluster, cancellationToken);
            if (evaluation.VisibilityRejectionReason is { } visibilityRejectionReason)
            {
                summary.Reject(visibilityRejectionReason);
                continue;
            }

            if (evaluation.LandCoverResult is { } landCoverResult)
            {
                landCoverSummary.AddCandidate();
                if (landCoverResult.LandCoverYear is { } landCoverYear)
                    landCoverSummary.RecordYear(landCoverYear);
                if (landCoverResult.Decision == LandCoverFilterDecision.Unavailable)
                    landCoverSummary.AddUnavailable();
                else if (landCoverResult.Decision == LandCoverFilterDecision.Suppressed)
                    landCoverSummary.AddSuppressed();
            }

            if (!evaluation.IsAccepted)
                continue;

            _pending.Add(new(
                cluster,
                now,
                SelectPreview(cluster),
                evaluation.LandCoverResult?.FormattingSummary));
        }

        if (newDetections.Length > 0)
        {
            logger.LogInformation(
                "Created {ZoneCount} Telegram zones from {NewDetectionCount} new detections",
                clusters.Length,
                newDetections.Length);
        }
    }

    private async Task<ClusterEvaluation> EvaluateClusterAsync(
        NotificationCluster cluster,
        CancellationToken cancellationToken)
    {
        var visibilityResult = TelegramVisibilityFilter.EvaluateMetadata(
            cluster,
            options.Visibility);
        if (!visibilityResult.IsAccepted)
        {
            var reason = visibilityResult.RejectionReason!.Value;
            logger.LogDebug(
                "Visibility filter rejected Telegram cluster {NotificationId}: {RejectionReason}",
                cluster.Id,
                reason);
            return new(false, reason, null);
        }

        if (!options.LandCover.Enabled)
            return ClusterEvaluation.Accepted;

        var landCoverResult = await TelegramLandCoverFilter.EvaluateAsync(
            cluster,
            options.LandCover,
            gibsClient,
            cancellationToken);
        if (landCoverResult.LandCoverYear is { } landCoverYear)
        {
            logger.LogDebug(
                "Selected NASA land-cover year {LandCoverYear} for Telegram cluster {NotificationId}",
                landCoverYear,
                cluster.Id);
        }

        if (landCoverResult.Decision == LandCoverFilterDecision.Unavailable)
        {
            logger.LogWarning(
                "NASA land-cover filter retained Telegram cluster {NotificationId}: {LandCoverReason}",
                cluster.Id,
                landCoverResult.Reason);
        }
        else if (landCoverResult.Decision == LandCoverFilterDecision.Suppressed)
        {
            logger.LogDebug(
                "NASA land-cover filter suppressed Telegram cluster {NotificationId}: {LandCoverReason}",
                cluster.Id,
                landCoverResult.Reason);
        }

        return new(
            landCoverResult.Decision != LandCoverFilterDecision.Suppressed,
            null,
            landCoverResult);
    }

    private async Task<bool> SendPendingAsync(
        ValidatedTelegram validated,
        VisibilityProcessingSummary summary,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < _pending.Count;)
        {
            var pending = _pending[index];
            var preview = await gibsClient.GetPreviewAsync(
                pending.Cluster.Representative,
                pending.PreviewSelection.Dimensions,
                cancellationToken);
            var previewExpired = timeProvider.GetUtcNow() - pending.FirstSeenUtc >= options.PreviewRetryWindow;

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
                logger.LogDebug(
                    "Visibility filter discarded Telegram cluster {NotificationId}: preview unavailable after retry timeout",
                    pending.Cluster.Id);
                _pending.RemoveAt(index);
                continue;
            }

            try
            {
                await SendNotificationAsync(
                    validated,
                    pending.Cluster,
                    preview,
                    pending.PreviewSelection,
                    pending.LandCoverSummary,
                    cancellationToken);

                logger.LogInformation(
                    "Sent Telegram notification {NotificationId} for {Satellite} at {AcquiredAtUtc}",
                    pending.Cluster.Id,
                    pending.Cluster.Representative.Satellite,
                    pending.Cluster.Representative.AcquiredAtUtc);
                summary.AddAccepted();
                _pending.RemoveAt(index);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ApiRequestException exception) when (exception.ErrorCode is 400 or 401 or 403)
            {
                summary.AddSendFailure();
                DisableValidatedTelegram(validated);
                logger.LogError("Telegram notifier disabled after a permanent send failure");
                return false;
            }
            catch (Exception)
            {
                summary.AddSendFailure();
                logger.LogWarning(
                    "Telegram send failed transiently for notification {NotificationId}",
                    pending.Cluster.Id);
                return true;
            }
        }

        return true;
    }

    private static async Task SendNotificationAsync(
        ValidatedTelegram validated,
        NotificationCluster cluster,
        GibsPreview preview,
        PreviewSelection previewSelection,
        string? landCoverSummary,
        CancellationToken cancellationToken)
    {
        var keyboard = CreateLocationKeyboard(cluster);
        var message = TelegramMessageFormatter.Format(
            cluster,
            preview.IsAvailable,
            previewSelection.Dimensions,
            previewSelection.ClusterDiameterKilometers,
            landCoverSummary);

        if (preview.PngBytes is { } pngBytes)
        {
            using var stream = new MemoryStream(pngBytes, writable: false);
            await validated.Client.SendPhoto(
                validated.ChatId,
                InputFile.FromStream(stream, "thermal-anomaly.png"),
                caption: message,
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: cancellationToken);
        }
        else
        {
            await validated.Client.SendMessage(
                validated.ChatId,
                message,
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                linkPreviewOptions: new() { IsDisabled = true },
                cancellationToken: cancellationToken);
        }
    }

    internal static InlineKeyboardMarkup CreateLocationKeyboard(NotificationCluster cluster) =>
        new(
        [
            [InlineKeyboardButton.WithUrl(
                "🗺 Open in Google Maps",
                cluster.Representative.GoogleMapsUrl)]
        ]);

    private void DisableValidatedTelegram(ValidatedTelegram validated) =>
        Interlocked.CompareExchange(ref _validated, null, validated);

    private PreviewSelection SelectPreview(NotificationCluster cluster)
    {
        var representative = cluster.Representative;
        var clusterDiameterKilometers = Geography.ClusterDiameterKilometers(cluster.Members);
        var previewOptions = options.Preview;
        var isLargePreview =
            cluster.Members.Length >= previewOptions.LargeClusterMinimumDetections
            || representative.FrpMegawatts is { } frp
                && frp >= previewOptions.LargeClusterMinimumFrpMegawatts
            || clusterDiameterKilometers >= previewOptions.LargeClusterMinimumDiameterKilometers;
        var previewSize = isLargePreview
            ? previewOptions.LargePreviewSize
            : previewOptions.PreviewSize;
        var dimensions = new GibsPreviewDimensions(
            previewSize.WidthKilometers,
            previewSize.HeightKilometers,
            previewOptions.PixelWidth,
            previewOptions.PixelHeight);

        logger.LogDebug(
            "Selected Telegram preview size for {NotificationId}: {DetectionCount} detections; representative FRP {RepresentativeFrpMegawatts}; diameter {ClusterDiameterKm} km; large {IsLargePreview}; crop {PreviewWidthKm} x {PreviewHeightKm} km; image {PixelWidth} x {PixelHeight}",
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
        if (!options.Visibility.Enabled || !summary.HasActivity)
            return;

        logger.LogInformation(
            "Visibility filter processed {CandidateClusterCount} new Telegram clusters; accepted {AcceptedClusterCount}; rejected {RejectedClusterCount}; pending preview {PendingPreviewCount}; preview timeouts {PreviewTimeoutCount}; send failures {SendFailureCount}. Rejections: nighttime {NighttimeCount}; insufficient detections {InsufficientDetectionsCount}; low confidence {LowConfidenceCount}; low FRP {LowFrpCount}; low thermal contrast {LowThermalContrastCount}; missing required value {MissingRequiredValueCount}; preview unavailable {PreviewUnavailableCount}",
            summary.CandidateClusterCount,
            summary.AcceptedClusterCount,
            summary.RejectedClusterCount,
            summary.PendingPreviewCount,
            summary.PreviewTimeoutCount,
            summary.SendFailureCount,
            summary.RejectionCount(VisibilityRejectionReason.Nighttime),
            summary.RejectionCount(VisibilityRejectionReason.InsufficientDetections),
            summary.RejectionCount(VisibilityRejectionReason.LowConfidence),
            summary.RejectionCount(VisibilityRejectionReason.LowFrp),
            summary.RejectionCount(VisibilityRejectionReason.LowThermalContrast),
            summary.RejectionCount(VisibilityRejectionReason.MissingRequiredValue),
            summary.RejectionCount(VisibilityRejectionReason.PreviewUnavailable));
    }

    private void LogLandCoverSummary(LandCoverProcessingSummary summary)
    {
        if (!options.LandCover.Enabled || !summary.HasActivity)
            return;

        logger.LogInformation(
            "NASA land-cover filter processed {CandidateClusterCount} Telegram clusters; suppressed {VegetationSuppressedCount}; unavailable {LandCoverUnavailableCount}; selected year {LandCoverYear}",
            summary.CandidateClusterCount,
            summary.VegetationSuppressedCount,
            summary.LandCoverUnavailableCount,
            summary.LandCoverYear);
    }

    private void LogManualSendSummary(ManualTelegramSendResponse response)
    {
        logger.LogInformation(
            "Manual Telegram send processed {RequestedCount} requested clusters; eligible {EligibleCount}; selected {SelectedCount}; sent {SentCount}; failed {FailedCount}",
            response.RequestedCount,
            response.EligibleCount,
            response.SelectedCount,
            response.SentCount,
            response.FailedCount);
    }

    private void ExpireSeen(DateTimeOffset now)
    {
        var cutoff = now - options.SeenRetention;
        foreach (var id in _seen.Where(pair => pair.Value < cutoff).Select(pair => pair.Key).ToArray())
            _seen.Remove(id);
    }

    private void TrimSeen()
    {
        var excess = _seen.Count - MaximumSeenIds;
        if (excess <= 0)
            return;

        foreach (var id in _seen.OrderBy(pair => pair.Value).Take(excess).Select(pair => pair.Key).ToArray())
            _seen.Remove(id);
    }

    private sealed record PendingNotification(
        NotificationCluster Cluster,
        DateTimeOffset FirstSeenUtc,
        PreviewSelection PreviewSelection,
        string? LandCoverSummary);

    private sealed record PreparedNotification(
        NotificationCluster Cluster,
        GibsPreview Preview,
        PreviewSelection PreviewSelection,
        string? LandCoverSummary);

    private readonly record struct PreviewSelection(
        GibsPreviewDimensions Dimensions,
        double ClusterDiameterKilometers);

    private sealed record ValidatedTelegram(TelegramBotClient Client, ChatId ChatId);

    private readonly record struct ClusterEvaluation(
        bool IsAccepted,
        VisibilityRejectionReason? VisibilityRejectionReason,
        LandCoverFilterResult? LandCoverResult)
    {
        public static ClusterEvaluation Accepted { get; } = new(true, null, null);
    }

    private sealed class VisibilityProcessingSummary
    {
        private readonly Dictionary<VisibilityRejectionReason, int> _rejectionCounts = [];

        public int CandidateClusterCount { get; private set; }

        public int AcceptedClusterCount { get; private set; }

        public int RejectedClusterCount { get; private set; }

        public int PendingPreviewCount { get; private set; }

        public int PreviewTimeoutCount { get; private set; }

        public int SendFailureCount { get; private set; }

        public bool HasActivity =>
            CandidateClusterCount > 0
            || AcceptedClusterCount > 0
            || RejectedClusterCount > 0
            || PendingPreviewCount > 0
            || PreviewTimeoutCount > 0
            || SendFailureCount > 0;

        public void AddCandidate() => CandidateClusterCount++;

        public void AddAccepted() => AcceptedClusterCount++;

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

public enum ManualTelegramSendStatus
{
    Completed,
    TelegramUnavailable,
    AlreadyRunning,
    StatusMessageFailed
}

public sealed record ManualTelegramSendResponse(
    int RequestedCount,
    int EligibleCount,
    int SelectedCount,
    int SentCount,
    int FailedCount,
    string[] FailedIds);

public sealed record ManualTelegramSendResult(
    ManualTelegramSendStatus Status,
    ManualTelegramSendResponse? Response)
{
    public static ManualTelegramSendResult TelegramUnavailable { get; } =
        new(ManualTelegramSendStatus.TelegramUnavailable, null);

    public static ManualTelegramSendResult AlreadyRunning { get; } =
        new(ManualTelegramSendStatus.AlreadyRunning, null);

    public static ManualTelegramSendResult StatusMessageFailed { get; } =
        new(ManualTelegramSendStatus.StatusMessageFailed, null);

    public static ManualTelegramSendResult Completed(ManualTelegramSendResponse response) =>
        new(ManualTelegramSendStatus.Completed, response);
}

internal static class TelegramNotificationClustering
{
    public static ImmutableArray<NotificationCluster> Create(
        IReadOnlyList<Anomaly> activeDetections,
        IReadOnlyList<Anomaly> newDetections,
        double radiusKilometers,
        TimeSpan timeWindow,
        bool includeActiveContext)
    {
        if (newDetections.Count == 0)
            return [];

        var newIds = newDetections
            .Select(detection => detection.Id)
            .ToHashSet(StringComparer.Ordinal);
        var clusteringDetections = includeActiveContext
            ? activeDetections
                .Where(detection => newIds.Contains(detection.Id)
                    || newDetections.Any(newDetection => AreRelated(
                        detection,
                        newDetection,
                        radiusKilometers,
                        timeWindow)))
                .DistinctBy(detection => detection.Id)
                .ToArray()
            : newDetections
                .DistinctBy(detection => detection.Id)
                .ToArray();

        return
        [
            .. NotificationClustering.Create(
                    clusteringDetections,
                    radiusKilometers,
                    timeWindow)
                .Where(cluster => cluster.Members.Any(member => newIds.Contains(member.Id)))
        ];
    }

    private static bool AreRelated(
        Anomaly first,
        Anomaly second,
        double radiusKilometers,
        TimeSpan timeWindow) =>
        (first.AcquiredAtUtc - second.AcquiredAtUtc).Duration() <= timeWindow
        && Geography.HaversineKilometers(first, second) <= radiusKilometers;
}
