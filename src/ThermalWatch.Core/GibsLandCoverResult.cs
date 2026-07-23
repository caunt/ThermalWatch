using System.Collections.Immutable;

namespace ThermalWatch.Core;

public sealed record GibsLandCoverResult(
    bool IsAvailable,
    int? Year,
    ImmutableArray<byte> SampledClasses,
    bool HasBuiltUpWithinProximity)
{
    public static GibsLandCoverResult Unavailable(int? year = null) =>
        new(IsAvailable: false, year, [], HasBuiltUpWithinProximity: false);
}
