using System.Collections.Immutable;

namespace ThermalWatch.Core;

public sealed record AnomalySnapshot(
    DateTimeOffset GeneratedAtUtc,
    double ActiveWindowHours,
    bool IsReady,
    bool IsPartiallyStale,
    ImmutableArray<string> ConfiguredCountries,
    ImmutableArray<SourceStatus> Sources,
    int Count,
    ImmutableArray<Anomaly> Items);
