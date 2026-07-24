using System.Collections.Immutable;

namespace ThermalWatch.Core;

public sealed record EligibleNotificationClusters(
    DateTimeOffset SnapshotGeneratedAtUtc,
    int EvaluatedClusterCount,
    int EligibleClusterCount,
    ImmutableArray<EligibleNotificationCluster> Clusters);
