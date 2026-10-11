using System.Collections.Immutable;

namespace ThermalWatch.Core;

/// <summary>An immutable publication of active observations and independent ingestion-segment status.</summary>
/// <param name="GeneratedAtUtc">The publication timestamp.</param>
/// <param name="ActiveWindowHours">The rolling retention window in hours.</param>
/// <param name="IsReady">Whether any configured segment has completed successfully.</param>
/// <param name="IsPartiallyStale">Whether a ready snapshot contains stale segments.</param>
/// <param name="ConfiguredCountryCodes">The configured ingestion countries.</param>
/// <param name="Segments">The refresh status of every country/source segment.</param>
/// <param name="AnomalyCount">The number of active observations.</param>
/// <param name="Anomalies">All valid, deduplicated observations within the active window.</param>
public sealed record AnomalySnapshot(
    DateTimeOffset GeneratedAtUtc,
    double ActiveWindowHours,
    bool IsReady,
    bool IsPartiallyStale,
    ImmutableArray<string> ConfiguredCountryCodes,
    ImmutableArray<SegmentStatus> Segments,
    int AnomalyCount,
    ImmutableArray<Anomaly> Anomalies
);
