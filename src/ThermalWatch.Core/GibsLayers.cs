using System.Collections.Immutable;

namespace ThermalWatch.Core;

public static class GibsLayers
{
    private static readonly GibsSourceDefinition s_terra = new(
        FirmsSource: "MODIS_NRT",
        Satellite: "Terra",
        Instrument: "MODIS",
        DayBaseLayer: "MODIS_Terra_CorrectedReflectance_TrueColor",
        DayBaseTileMatrixSet: "250m",
        NightBaseLayer: "MODIS_Terra_Brightness_Temp_Band31_Night",
        NightBaseTileMatrixSet: "1km",
        OverlayLayer: "MODIS_Terra_Thermal_Anomalies_All",
        OverlayTileMatrixSet: "1km");
    private static readonly GibsSourceDefinition s_aqua = new(
        FirmsSource: "MODIS_NRT",
        Satellite: "Aqua",
        Instrument: "MODIS",
        DayBaseLayer: "MODIS_Aqua_CorrectedReflectance_TrueColor",
        DayBaseTileMatrixSet: "250m",
        NightBaseLayer: "MODIS_Aqua_Brightness_Temp_Band31_Night",
        NightBaseTileMatrixSet: "1km",
        OverlayLayer: "MODIS_Aqua_Thermal_Anomalies_All",
        OverlayTileMatrixSet: "1km");
    private static readonly GibsSourceDefinition s_noaa21 = CreateViirs(
        firmsSource: "VIIRS_NOAA21_NRT",
        satellite: "NOAA-21",
        prefix: "VIIRS_NOAA21");
    private static readonly GibsSourceDefinition s_noaa20 = CreateViirs(
        firmsSource: "VIIRS_NOAA20_NRT",
        satellite: "NOAA-20",
        prefix: "VIIRS_NOAA20");
    private static readonly GibsSourceDefinition s_suomiNpp = CreateViirs(
        firmsSource: "VIIRS_SNPP_NRT",
        satellite: "Suomi-NPP",
        prefix: "VIIRS_SNPP");
    private static readonly ImmutableArray<GibsSourceDefinition> s_modisSources = [s_terra, s_aqua];
    private static readonly ImmutableArray<GibsSourceDefinition> s_viirsSources =
        [s_noaa21, s_noaa20, s_suomiNpp];

    public static bool TryGet(Anomaly anomaly, out GibsLayerPair layers)
    {
        ImmutableArray<GibsLayerCandidate> candidates = GetCandidates(anomaly);
        if (!candidates.IsDefaultOrEmpty)
        {
            GibsLayerCandidate candidate = candidates[0];
            layers = new(
                candidate.BaseLayer,
                candidate.BaseTileMatrixSet,
                candidate.OverlayLayer,
                candidate.OverlayTileMatrixSet);
            return true;
        }

        layers = default;
        return false;
    }

    internal static ImmutableArray<GibsLayerCandidate> GetCandidates(Anomaly anomaly)
    {
        bool night = anomaly.DayNight.Equals(value: "N", StringComparison.Ordinal);
        bool isModis = anomaly.Source.Equals(value: "MODIS_NRT", StringComparison.Ordinal);
        ImmutableArray<GibsSourceDefinition> family = isModis ? s_modisSources : s_viirsSources;
        ImmutableArray<GibsSourceDefinition> otherFamily = isModis ? s_viirsSources : s_modisSources;
        GibsSourceDefinition? representative = family.FirstOrDefault(source => source.Matches(anomaly));
        if (representative is null)
            return [];

        ImmutableArray<GibsLayerCandidate>.Builder candidates = ImmutableArray.CreateBuilder<GibsLayerCandidate>(
            s_modisSources.Length + s_viirsSources.Length);
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
        bool night)
    {
        candidates.Add(new(
            night ? baseSource.NightBaseLayer : baseSource.DayBaseLayer,
            night ? baseSource.NightBaseTileMatrixSet : baseSource.DayBaseTileMatrixSet,
            representative.OverlayLayer,
            representative.OverlayTileMatrixSet,
            new(
                baseSource.FirmsSource,
                baseSource.Satellite,
                baseSource.Instrument)));
    }

    private static GibsSourceDefinition CreateViirs(
        string firmsSource,
        string satellite,
        string prefix) =>
        new(
            firmsSource,
            satellite,
            Instrument: "VIIRS",
            DayBaseLayer: $"{prefix}_CorrectedReflectance_TrueColor",
            DayBaseTileMatrixSet: "250m",
            NightBaseLayer: $"{prefix}_Brightness_Temp_BandI5_Night",
            NightBaseTileMatrixSet: "250m",
            OverlayLayer: $"{prefix}_Thermal_Anomalies_375m_All",
            OverlayTileMatrixSet: "500m");

    private sealed record GibsSourceDefinition(
        string FirmsSource,
        string Satellite,
        string Instrument,
        string DayBaseLayer,
        string DayBaseTileMatrixSet,
        string NightBaseLayer,
        string NightBaseTileMatrixSet,
        string OverlayLayer,
        string OverlayTileMatrixSet)
    {
        public bool Matches(Anomaly anomaly)
        {
            if (!anomaly.Source.Equals(FirmsSource, StringComparison.Ordinal))
                return false;

            if (!FirmsSource.Equals(value: "MODIS_NRT", StringComparison.Ordinal))
                return true;

            return anomaly.Satellite.Equals(Satellite, StringComparison.OrdinalIgnoreCase)
                || Satellite.Equals(value: "Terra", StringComparison.Ordinal)
                    && anomaly.Satellite.Equals(value: "T", StringComparison.OrdinalIgnoreCase)
                || Satellite.Equals(value: "Aqua", StringComparison.Ordinal)
                    && anomaly.Satellite.Equals(value: "A", StringComparison.OrdinalIgnoreCase);
        }
    }
}
