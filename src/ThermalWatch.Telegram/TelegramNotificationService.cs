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
    private bool _firstReadySnapshot = true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var validation = await TryValidateAsync(stoppingToken);
        if (validation is not { } validated)
            return;

        await foreach (var snapshot in snapshotStore.ReadUpdatesAsync(stoppingToken))
        {
            if (!snapshot.IsReady)
                continue;

            var summary = new VisibilityProcessingSummary();
            TrackNewDetections(snapshot, summary);
            var continueRunning = await SendPendingAsync(
                validated.Client,
                validated.ChatId,
                summary,
                stoppingToken);
            LogVisibilitySummary(summary);
            if (!continueRunning)
                return;
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

    private void TrackNewDetections(
        AnomalySnapshot snapshot,
        VisibilityProcessingSummary summary)
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
            var result = TelegramVisibilityFilter.EvaluateMetadata(cluster, options.Visibility);
            if (result.IsAccepted)
            {
                _pending.Add(new(cluster, now, SelectPreviewDimensions(cluster)));
                continue;
            }

            var reason = result.RejectionReason!.Value;
            summary.Reject(reason);
            logger.LogDebug(
                "Visibility filter rejected Telegram cluster {NotificationId}: {RejectionReason}",
                cluster.Id,
                reason);
        }

        if (newDetections.Length > 0)
        {
            logger.LogInformation(
                "Created {ZoneCount} Telegram zones from {NewDetectionCount} new detections",
                clusters.Length,
                newDetections.Length);
        }
    }

    private async Task<bool> SendPendingAsync(
        TelegramBotClient client,
        ChatId chatId,
        VisibilityProcessingSummary summary,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < _pending.Count;)
        {
            var pending = _pending[index];
            var preview = await gibsClient.GetPreviewAsync(
                pending.Cluster.Representative,
                pending.PreviewDimensions,
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
                var keyboard = new InlineKeyboardMarkup(
                [
                    [InlineKeyboardButton.WithUrl(
                        "🗺 Open in Google Maps",
                        pending.Cluster.Representative.GoogleMapsUrl)]
                ]);
                var message = TelegramMessageFormatter.Format(pending.Cluster, preview.IsAvailable);

                if (preview.PngBytes is { } pngBytes)
                {
                    using var stream = new MemoryStream(pngBytes, writable: false);
                    await client.SendPhoto(
                        chatId,
                        InputFile.FromStream(stream, "thermal-anomaly.png"),
                        caption: message,
                        parseMode: ParseMode.Html,
                        replyMarkup: keyboard,
                        cancellationToken: cancellationToken);
                }
                else
                {
                    await client.SendMessage(
                        chatId,
                        message,
                        parseMode: ParseMode.Html,
                        replyMarkup: keyboard,
                        linkPreviewOptions: new() { IsDisabled = true },
                        cancellationToken: cancellationToken);
                }

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

    private GibsPreviewDimensions SelectPreviewDimensions(NotificationCluster cluster)
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

        return dimensions;
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
        GibsPreviewDimensions PreviewDimensions);

    private sealed record ValidatedTelegram(TelegramBotClient Client, ChatId ChatId);

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
