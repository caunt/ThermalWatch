using System.Collections.Immutable;

namespace ThermalWatch.Core;

public sealed record FirmsHistoryDay(
    DateOnly Date,
    bool IsComplete,
    bool IsPartiallyStale,
    ImmutableArray<SegmentStatus> Segments,
    int AnomalyCount,
    ImmutableArray<Anomaly> Anomalies,
    int ClusterCount,
    ImmutableArray<NotificationClusterSummary> Clusters);
