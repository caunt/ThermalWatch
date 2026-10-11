using System.Collections.Immutable;

namespace ThermalWatch.Core;

/// <summary>One UTC date's raw observations, clusters, and independent segment completeness.</summary>
/// <param name="Date">The UTC acquisition date.</param>
/// <param name="IsComplete">Whether every country/source slice is complete and fresh.</param>
/// <param name="IsPartiallyStale">Whether any successfully acquired slice is stale.</param>
/// <param name="Segments">The country/source slice diagnostics.</param>
/// <param name="AnomalyCount">The number of deduplicated raw observations.</param>
/// <param name="Anomalies">All valid observations acquired on this date.</param>
/// <param name="ClusterCount">The number of raw connected components.</param>
/// <param name="Clusters">Raw clusters without notification eligibility filters.</param>
public sealed record FirmsHistoryDay(
    DateOnly Date,
    bool IsComplete,
    bool IsPartiallyStale,
    ImmutableArray<SegmentStatus> Segments,
    int AnomalyCount,
    ImmutableArray<Anomaly> Anomalies,
    int ClusterCount,
    ImmutableArray<NotificationClusterSummary> Clusters
);
