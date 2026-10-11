using System.Collections.Immutable;

namespace ThermalWatch.Core;

/// <summary>The eligible cluster summaries for one immutable snapshot publication.</summary>
/// <param name="SnapshotGeneratedAtUtc">The snapshot publication time.</param>
/// <param name="EvaluatedClusterCount">The number of clusters considered.</param>
/// <param name="EligibleClusterCount">The number passing notification selection.</param>
/// <param name="Clusters">The eligible clusters in notification order.</param>
public sealed record EligibleNotificationClusters(
    DateTimeOffset SnapshotGeneratedAtUtc,
    int EvaluatedClusterCount,
    int EligibleClusterCount,
    ImmutableArray<NotificationClusterSummary> Clusters
);
