using System.Collections.Immutable;

namespace ThermalWatch.Core;

internal static class GibsLayers
{
    private static readonly GibsSourceDefinition Terra = new(
        FirmsSource: "MODIS_NRT",
        Satellite: "Terra",
        Instrument: "MODIS",
        DayBaseLayer: "MODIS_Terra_CorrectedReflectance_TrueColor",
        DayBaseTileMatrixSet: "250m",
        NightBaseLayer: "MODIS_Terra_Brightness_Temp_Band31_Night",
        NightBaseTileMatrixSet: "1km",
        OverlayLayer: "MODIS_Terra_Thermal_Anomalies_All",
        OverlayTileMatrixSet: "1km"
    );
    private static readonly GibsSourceDefinition Aqua = new(
        FirmsSource: "MODIS_NRT",
        Satellite: "Aqua",
        Instrument: "MODIS",
        DayBaseLayer: "MODIS_Aqua_CorrectedReflectance_TrueColor",
        DayBaseTileMatrixSet: "250m",
        NightBaseLayer: "MODIS_Aqua_Brightness_Temp_Band31_Night",
        NightBaseTileMatrixSet: "1km",
        OverlayLayer: "MODIS_Aqua_Thermal_Anomalies_All",
        OverlayTileMatrixSet: "1km"
    );
    private static readonly GibsSourceDefinition Noaa21 = CreateViirs(firmsSource: "VIIRS_NOAA21_NRT", satellite: "NOAA-21", prefix: "VIIRS_NOAA21");
    private static readonly GibsSourceDefinition Noaa20 = CreateViirs(firmsSource: "VIIRS_NOAA20_NRT", satellite: "NOAA-20", prefix: "VIIRS_NOAA20");
    private static readonly GibsSourceDefinition SuomiNpp = CreateViirs(firmsSource: "VIIRS_SNPP_NRT", satellite: "Suomi-NPP", prefix: "VIIRS_SNPP");
    private static readonly ImmutableArray<GibsSourceDefinition> ModisSources = [Terra, Aqua];
    private static readonly ImmutableArray<GibsSourceDefinition> ViirsSources =
        [Noaa21, Noaa20, SuomiNpp];
    internal static ImmutableArray<string> MapBaseLayers { get; } =
        [.. ModisSources.Select(static source => source.DayBaseLayer), .. ViirsSources.Select(static source => source.DayBaseLayer)];

    internal static ImmutableArray<GibsLayerCandidate> GetCandidates(Anomaly anomaly)
    {
        bool night = anomaly.DayNight.Equals(value: "N", StringComparison.Ordinal);
        bool isModis = anomaly.Source.Equals(value: "MODIS_NRT", StringComparison.Ordinal);
        ImmutableArray<GibsSourceDefinition> family = isModis ? ModisSources : ViirsSources;
        ImmutableArray<GibsSourceDefinition> otherFamily = isModis ? ViirsSources : ModisSources;
        GibsSourceDefinition? representative = family.FirstOrDefault(source => source.Matches(anomaly));

        if (representative is null)
            return [];

        ImmutableArray<GibsLayerCandidate>.Builder candidates = ImmutableArray.CreateBuilder<GibsLayerCandidate>(ModisSources.Length + ViirsSources.Length);

        AddCandidate(candidates, representative, representative, night);

        foreach (GibsSourceDefinition source in family)
        {
            if (source != representative)
                AddCandidate(candidates, source, representative, night);
        }

        foreach (GibsSourceDefinition source in otherFamily)
            AddCandidate(candidates, source, representative, night);

        return candidates.MoveToImmutable();
    }

    private static void AddCandidate(
        ImmutableArray<GibsLayerCandidate>.Builder candidates,
        GibsSourceDefinition baseSource,
        GibsSourceDefinition representative,
        bool night
    )
    {
        candidates.Add(
            new(
                night ? baseSource.NightBaseLayer : baseSource.DayBaseLayer,
                night ? baseSource.NightBaseTileMatrixSet : baseSource.DayBaseTileMatrixSet,
                representative.OverlayLayer,
                representative.OverlayTileMatrixSet,
                new(baseSource.FirmsSource, baseSource.Satellite, baseSource.Instrument)
            )
        );
    }

    private static GibsSourceDefinition CreateViirs(string firmsSource, string satellite, string prefix)
    {
        return new(
            firmsSource,
            satellite,
            Instrument: "VIIRS",
            $"{prefix}_CorrectedReflectance_TrueColor",
            DayBaseTileMatrixSet: "250m",
            $"{prefix}_Brightness_Temp_BandI5_Night",
            NightBaseTileMatrixSet: "250m",
            $"{prefix}_Thermal_Anomalies_375m_All",
            OverlayTileMatrixSet: "500m"
        );
    }

    private sealed record GibsSourceDefinition(
        string FirmsSource,
        string Satellite,
        string Instrument,
        string DayBaseLayer,
        string DayBaseTileMatrixSet,
        string NightBaseLayer,
        string NightBaseTileMatrixSet,
        string OverlayLayer,
        string OverlayTileMatrixSet
    )
    {
        public bool Matches(Anomaly anomaly)
        {
            return anomaly.Source.Equals(FirmsSource, StringComparison.Ordinal) && (!FirmsSource.Equals(value: "MODIS_NRT", StringComparison.Ordinal) || anomaly.Satellite.Equals(Satellite, StringComparison.OrdinalIgnoreCase)
                || (Satellite.Equals(value: "Terra", StringComparison.Ordinal)
                    && anomaly.Satellite.Equals(value: "T", StringComparison.OrdinalIgnoreCase))
                || (Satellite.Equals(value: "Aqua", StringComparison.Ordinal)
                    && anomaly.Satellite.Equals(value: "A", StringComparison.OrdinalIgnoreCase)));
        }
    }
}
