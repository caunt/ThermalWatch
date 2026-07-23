using System.Collections.Immutable;

namespace ThermalWatch.Core;

public sealed record NotificationCluster(
    string Id,
    Anomaly Representative,
    ImmutableArray<Anomaly> Members);
