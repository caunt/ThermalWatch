using Microsoft.Extensions.Logging;

namespace ThermalWatch.Telegram;

internal static partial class TelegramNotificationLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Error,
        Message = "Telegram notifier disabled: TELEGRAM_CHANNEL_ID is not a channel")]
    internal static partial void InvalidChannel(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error,
        Message = "Telegram notifier disabled: bot cannot post to the configured channel")]
    internal static partial void CannotPost(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "Telegram notifier validated and enabled")]
    internal static partial void Validated(ILogger logger);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error,
        Message = "Telegram notifier disabled: validation failed")]
    internal static partial void ValidationFailed(ILogger logger);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug,
        Message = "Visibility filter rejected manual Telegram cluster {NotificationId}: preview unavailable")]
    internal static partial void ManualPreviewUnavailable(ILogger logger, string notificationId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information,
        Message = "Sent manual Telegram notification {NotificationId}")]
    internal static partial void ManualNotificationSent(ILogger logger, string notificationId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning,
        Message = "Manual Telegram notification failed for {NotificationId}")]
    internal static partial void ManualNotificationFailed(ILogger logger, string notificationId);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning,
        Message = "Manual Telegram introductory message failed")]
    internal static partial void ManualStatusFailed(ILogger logger);

    [LoggerMessage(EventId = 9, Level = LogLevel.Information,
        Message = "Primed Telegram deduplication with {NewDetectionCount} existing detections")]
    internal static partial void DeduplicationPrimed(ILogger logger, int newDetectionCount);

    [LoggerMessage(EventId = 10, Level = LogLevel.Debug,
        Message = "Suppressed Telegram cluster {NotificationId}: continuing a delivered episode")]
    internal static partial void DeliveredEpisodeSuppressed(ILogger logger, string notificationId);

    [LoggerMessage(EventId = 11, Level = LogLevel.Information,
        Message = "Created {ZoneCount} Telegram zones from {NewDetectionCount} new detections")]
    internal static partial void ZonesCreated(ILogger logger, int zoneCount, int newDetectionCount);

    [LoggerMessage(EventId = 12, Level = LogLevel.Debug,
        Message = "Visibility filter rejected Telegram cluster {NotificationId}: {RejectionReason}")]
    internal static partial void VisibilityRejected(
        ILogger logger,
        string notificationId,
        VisibilityRejectionReason rejectionReason);

    [LoggerMessage(EventId = 13, Level = LogLevel.Debug,
        Message = "Selected NASA land-cover year {LandCoverYear} for Telegram cluster {NotificationId}")]
    internal static partial void LandCoverYearSelected(
        ILogger logger,
        int landCoverYear,
        string notificationId);

    [LoggerMessage(EventId = 14, Level = LogLevel.Warning,
        Message = "NASA land-cover filter retained Telegram cluster {NotificationId}: {LandCoverReason}")]
    internal static partial void LandCoverUnavailable(
        ILogger logger,
        string notificationId,
        string landCoverReason);

    [LoggerMessage(EventId = 15, Level = LogLevel.Debug,
        Message = "NASA land-cover filter suppressed Telegram cluster {NotificationId}: {LandCoverReason}")]
    internal static partial void LandCoverSuppressed(
        ILogger logger,
        string notificationId,
        string landCoverReason);

    [LoggerMessage(EventId = 16, Level = LogLevel.Debug,
        Message = "Suppressed pending Telegram cluster {NotificationId}: continuing a delivered episode")]
    internal static partial void PendingDeliveredEpisodeSuppressed(ILogger logger, string notificationId);

    [LoggerMessage(EventId = 17, Level = LogLevel.Debug,
        Message = "Visibility filter discarded Telegram cluster {NotificationId}: preview unavailable after retry timeout")]
    internal static partial void PreviewRetryExpired(ILogger logger, string notificationId);

    [LoggerMessage(EventId = 18, Level = LogLevel.Information,
        Message = "Sent Telegram notification {NotificationId} for {Satellite} at {AcquiredAtUtc}")]
    internal static partial void NotificationSent(
        ILogger logger,
        string notificationId,
        string satellite,
        DateTimeOffset acquiredAtUtc);

    [LoggerMessage(EventId = 19, Level = LogLevel.Error,
        Message = "Telegram notifier disabled after a permanent send failure")]
    internal static partial void PermanentSendFailure(ILogger logger);

    [LoggerMessage(EventId = 20, Level = LogLevel.Warning,
        Message = "Telegram send failed transiently for notification {NotificationId}")]
    internal static partial void TransientSendFailure(ILogger logger, string notificationId);

    [LoggerMessage(EventId = 21, Level = LogLevel.Debug,
        Message = "Selected Telegram preview size for {NotificationId}: {DetectionCount} detections; representative FRP {RepresentativeFrpMegawatts}; diameter {ClusterDiameterKm} km; large {IsLargePreview}; crop {PreviewWidthKm} x {PreviewHeightKm} km; image {PixelWidth} x {PixelHeight}")]
    internal static partial void PreviewSizeSelected(
        ILogger logger,
        string notificationId,
        int detectionCount,
        double? representativeFrpMegawatts,
        double clusterDiameterKm,
        bool isLargePreview,
        double previewWidthKm,
        double previewHeightKm,
        int pixelWidth,
        int pixelHeight);

    [LoggerMessage(EventId = 22, Level = LogLevel.Information,
        Message = "Visibility filter processed {CandidateClusterCount} new Telegram clusters; accepted {AcceptedClusterCount}; rejected {RejectedClusterCount}; duplicate episodes {DuplicateEpisodeCount}; pending preview {PendingPreviewCount}; preview timeouts {PreviewTimeoutCount}; send failures {SendFailureCount}. Rejections: nighttime {NighttimeCount}; insufficient detections {InsufficientDetectionsCount}; low confidence {LowConfidenceCount}; low FRP {LowFrpCount}; low thermal contrast {LowThermalContrastCount}; missing required value {MissingRequiredValueCount}; preview unavailable {PreviewUnavailableCount}")]
    internal static partial void VisibilitySummary(
        ILogger logger,
        int candidateClusterCount,
        int acceptedClusterCount,
        int rejectedClusterCount,
        int duplicateEpisodeCount,
        int pendingPreviewCount,
        int previewTimeoutCount,
        int sendFailureCount,
        int nighttimeCount,
        int insufficientDetectionsCount,
        int lowConfidenceCount,
        int lowFrpCount,
        int lowThermalContrastCount,
        int missingRequiredValueCount,
        int previewUnavailableCount);

    [LoggerMessage(EventId = 23, Level = LogLevel.Information,
        Message = "NASA land-cover filter processed {CandidateClusterCount} Telegram clusters; suppressed {VegetationSuppressedCount}; unavailable {LandCoverUnavailableCount}; selected year {LandCoverYear}")]
    internal static partial void LandCoverSummary(
        ILogger logger,
        int candidateClusterCount,
        int vegetationSuppressedCount,
        int landCoverUnavailableCount,
        int? landCoverYear);

    [LoggerMessage(EventId = 24, Level = LogLevel.Information,
        Message = "Manual Telegram send processed {RequestedCount} requested clusters; eligible {EligibleCount}; selected {SelectedCount}; sent {SentCount}; failed {FailedCount}")]
    internal static partial void ManualSendSummary(
        ILogger logger,
        int requestedCount,
        int eligibleCount,
        int selectedCount,
        int sentCount,
        int failedCount);
}
