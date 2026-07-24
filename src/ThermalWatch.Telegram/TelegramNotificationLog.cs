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

    [LoggerMessage(EventId = 6, Level = LogLevel.Information,
        Message = "Sent manual Telegram notification for cluster {ClusterId}")]
    internal static partial void ManualNotificationSent(ILogger logger, string clusterId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning,
        Message = "Manual Telegram notification failed for cluster {ClusterId}")]
    internal static partial void ManualNotificationFailed(ILogger logger, string clusterId);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning,
        Message = "Manual Telegram introductory message failed")]
    internal static partial void ManualStatusFailed(ILogger logger);

    [LoggerMessage(EventId = 18, Level = LogLevel.Information,
        Message = "Sent Telegram notification for cluster {ClusterId}, {Satellite} at {AcquiredAtUtc}")]
    internal static partial void NotificationSent(
        ILogger logger,
        string clusterId,
        string satellite,
        DateTimeOffset acquiredAtUtc);

    [LoggerMessage(EventId = 19, Level = LogLevel.Error,
        Message = "Telegram notifier disabled after a permanent send failure")]
    internal static partial void PermanentSendFailure(ILogger logger);

    [LoggerMessage(EventId = 20, Level = LogLevel.Warning,
        Message = "Telegram send failed transiently for cluster {ClusterId}")]
    internal static partial void TransientSendFailure(ILogger logger, string clusterId);

    [LoggerMessage(EventId = 22, Level = LogLevel.Information,
        Message = "Notification policy processed {ActiveClusterCount} active clusters; evaluated {EvaluatedClusterCount}; delivered {DeliveredClusterCount}; rejected {RejectedClusterCount}; startup incidents suppressed {StartupSuppressedIncidentCount}; duplicate delivered episodes {DuplicateEpisodeCount}; send failures {SendFailureCount}. Rejections: nighttime {NighttimeCount}; insufficient detections {InsufficientDetectionsCount}; low confidence {LowConfidenceCount}; low FRP {LowFrpCount}; low thermal contrast {LowThermalContrastCount}; missing required value {MissingRequiredValueCount}; preview unavailable {PreviewUnavailableCount}")]
    internal static partial void VisibilitySummary(
        ILogger logger,
        int activeClusterCount,
        int evaluatedClusterCount,
        int deliveredClusterCount,
        int rejectedClusterCount,
        int startupSuppressedIncidentCount,
        int duplicateEpisodeCount,
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
        Message = "Manual Telegram send processed {RequestedClusterCount} requested clusters; eligible {EligibleClusterCount}; selected {SelectedClusterCount}; sent {SentClusterCount}; failed {FailedClusterCount}")]
    internal static partial void ManualSendSummary(
        ILogger logger,
        int requestedClusterCount,
        int eligibleClusterCount,
        int selectedClusterCount,
        int sentClusterCount,
        int failedClusterCount);
}
