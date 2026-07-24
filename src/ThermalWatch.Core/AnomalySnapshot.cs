using System.Collections.Immutable;

namespace ThermalWatch.Core;

public sealed record AnomalySnapshot(
    DateTimeOffset GeneratedAtUtc,
    double ActiveWindowHours,
    bool IsReady,
    bool IsPartiallyStale,
    ImmutableArray<string> ConfiguredCountryCodes,
    ImmutableArray<SegmentStatus> Segments,
    int AnomalyCount,
    ImmutableArray<Anomaly> Anomalies);
