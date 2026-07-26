using System.Collections.Immutable;

namespace ThermalWatch.Core;

internal readonly record struct NearbyMappedContext(
    string? SettlementName,
    ImmutableArray<NearbyFeature> NearbyFeatures)
{
    public static NearbyMappedContext Empty { get; } = new(
        SettlementName: null,
        NearbyFeatures: []);
}
