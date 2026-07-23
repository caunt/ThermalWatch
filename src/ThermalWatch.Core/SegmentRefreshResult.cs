using System.Collections.Immutable;

namespace ThermalWatch.Core;

public sealed record SegmentRefreshResult(
    SegmentKey Key,
    DateTimeOffset AttemptedAtUtc,
    DateTimeOffset CompletedAtUtc,
    bool Succeeded,
    ImmutableArray<Anomaly> Detections,
    string? Error,
    string IngestionMode)
{
    public static SegmentRefreshResult Success(
        SegmentKey key,
        DateTimeOffset attemptedAtUtc,
        DateTimeOffset completedAtUtc,
        ImmutableArray<Anomaly> detections,
        string ingestionMode) =>
        new(key, attemptedAtUtc, completedAtUtc, Succeeded: true, detections, Error: null, ingestionMode);

    public static SegmentRefreshResult Failure(
        SegmentKey key,
        DateTimeOffset attemptedAtUtc,
        DateTimeOffset completedAtUtc,
        string error) =>
        new(key, attemptedAtUtc, completedAtUtc, Succeeded: false, [], error, IngestionModes.None);
}
