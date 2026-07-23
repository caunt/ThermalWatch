using Microsoft.Extensions.Logging;

namespace ThermalWatch.Api;

internal static partial class FirmsPollingLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Published FIRMS snapshot with {DetectionCount} detections; partially stale: {IsPartiallyStale}")]
    internal static partial void SnapshotPublished(
        ILogger logger,
        int detectionCount,
        bool isPartiallyStale);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "Refreshed FIRMS segment {Country} {Source} in {IngestionMode} mode with {DetectionCount} detections")]
    internal static partial void SegmentRefreshed(
        ILogger logger,
        string country,
        string source,
        string ingestionMode,
        int detectionCount);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "FIRMS segment refresh failed for {Country} {Source}: {SafeError}")]
    internal static partial void SegmentRefreshFailed(
        ILogger logger,
        string country,
        string source,
        string safeError);
}
