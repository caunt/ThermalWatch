using System.Collections.Immutable;

namespace ThermalWatch.Core;

/// <summary>The independently ingested FIRMS near-real-time datasets.</summary>
public static class FirmsSources
{
    /// <summary>The MODIS and three VIIRS feeds in deterministic ingestion order.</summary>
    public static readonly ImmutableArray<string> All =
    [
        "MODIS_NRT",
        "VIIRS_SNPP_NRT",
        "VIIRS_NOAA20_NRT",
        "VIIRS_NOAA21_NRT"
    ];
}
