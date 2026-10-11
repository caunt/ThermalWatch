using System.Collections.Immutable;

namespace ThermalWatch.Core;

internal sealed record NearbyMappedContext(string? SettlementName, ImmutableArray<NearbyFeature> NearbyFeatures)
{
    public static NearbyMappedContext Empty { get; } = new(SettlementName: null, []);
}
