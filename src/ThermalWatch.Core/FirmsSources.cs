using System.Collections.Immutable;

namespace ThermalWatch.Core;

public static class FirmsSources
{
    public static readonly ImmutableArray<string> All =
    [
        "MODIS_NRT",
        "VIIRS_SNPP_NRT",
        "VIIRS_NOAA20_NRT",
        "VIIRS_NOAA21_NRT"
    ];
}
