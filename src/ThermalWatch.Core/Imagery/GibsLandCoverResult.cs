using System.Collections.Immutable;

namespace ThermalWatch.Core;

/// <summary>The annual land-cover samples and built-up proximity for a cluster.</summary>
/// <param name="IsAvailable">Whether all required tiles and samples were obtained.</param>
/// <param name="Year">The common annual dataset year.</param>
/// <param name="SampledClasses">The IGBP class codes sampled around cluster members.</param>
/// <param name="HasBuiltUpWithinProximity">Whether a built-up class was found within the configured radius.</param>
public sealed record GibsLandCoverResult(bool IsAvailable, int? Year, ImmutableArray<byte> SampledClasses, bool HasBuiltUpWithinProximity)
{
    /// <summary>Creates a fail-open unavailable sampling result.</summary>
    public static GibsLandCoverResult Unavailable(int? year = null)
    {
        return new(IsAvailable: false, year, [], HasBuiltUpWithinProximity: false);
    }
}
