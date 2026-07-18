using System.Globalization;
using ThermalWatch.Core;

namespace ThermalWatch.Telegram;

internal static class TelegramLandCoverFilter
{
    public static async Task<LandCoverFilterResult> EvaluateAsync(
        NotificationCluster cluster,
        TelegramLandCoverOptions options,
        GibsClient gibsClient,
        CancellationToken cancellationToken)
    {
        var landCover = await gibsClient.GetLandCoverAsync(
            cluster.Members,
            options.BuiltUpProximityKilometers,
            cancellationToken);
        return Evaluate(cluster, options, landCover);
    }

    internal static LandCoverFilterResult Evaluate(
        NotificationCluster cluster,
        TelegramLandCoverOptions options,
        GibsLandCoverResult landCover)
    {
        if (!landCover.IsAvailable)
        {
            return LandCoverFilterResult.Unavailable(
                landCover.Year,
                "NASA land-cover data unavailable");
        }

        if (landCover.Year is null
            || landCover.SampledClasses.IsDefaultOrEmpty
            || landCover.SampledClasses.Any(landCoverClass => landCoverClass is < 1 or > 17))
        {
            return LandCoverFilterResult.Unavailable(
                landCover.Year,
                "NASA land-cover data invalid");
        }

        var vegetationCount = landCover.SampledClasses.Count(IsVegetation);
        var vegetationPercent = vegetationCount * 100d / landCover.SampledClasses.Length;
        var formattedPercent = FormatNumber(vegetationPercent);
        var formattingSummary = landCover.HasBuiltUpWithinProximity
            ? $"Urban/built-up nearby · vegetation {formattedPercent}%"
            : $"Vegetation · {formattedPercent}%";

        if (vegetationPercent < options.VegetationPercentThreshold)
        {
            return LandCoverFilterResult.Retained(
                landCover.Year.Value,
                vegetationPercent,
                landCover.HasBuiltUpWithinProximity,
                $"Vegetation {formattedPercent}% is below {FormatNumber(options.VegetationPercentThreshold)}%",
                formattingSummary);
        }

        if (landCover.HasBuiltUpWithinProximity)
        {
            return LandCoverFilterResult.Retained(
                landCover.Year.Value,
                vegetationPercent,
                true,
                $"NASA class 13 is within {FormatNumber(options.BuiltUpProximityKilometers)} km",
                formattingSummary);
        }

        if (options.KeepHighFrpVegetation
            && cluster.Representative.FrpMegawatts is { } frp
            && frp >= options.VegetationMaximumFrpMegawatts)
        {
            return LandCoverFilterResult.Retained(
                landCover.Year.Value,
                vegetationPercent,
                false,
                $"Configured high-FRP vegetation exception retained {FormatNumber(frp)} MW",
                formattingSummary);
        }

        var hasMultipleSatellites = cluster.Members
            .Select(member => member.Satellite)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Skip(1)
            .Any();
        if (options.KeepMultiSatelliteVegetation && hasMultipleSatellites)
        {
            return LandCoverFilterResult.Retained(
                landCover.Year.Value,
                vegetationPercent,
                false,
                "Configured multi-satellite vegetation exception retained cluster",
                formattingSummary);
        }

        return LandCoverFilterResult.Suppressed(
            landCover.Year.Value,
            vegetationPercent,
            $"Vegetation {formattedPercent}% meets {FormatNumber(options.VegetationPercentThreshold)}%; no NASA class 13 within {FormatNumber(options.BuiltUpProximityKilometers)} km",
            formattingSummary);
    }

    private static bool IsVegetation(byte landCoverClass) =>
        landCoverClass is >= 1 and <= 12 or 14;

    private static string FormatNumber(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);
}

internal enum LandCoverFilterDecision
{
    Retained,
    Suppressed,
    Unavailable
}

internal readonly record struct LandCoverFilterResult(
    LandCoverFilterDecision Decision,
    int? LandCoverYear,
    double? VegetationPercent,
    bool? HasBuiltUpWithinProximity,
    string Reason,
    string FormattingSummary)
{
    public bool IsSuppressed => Decision == LandCoverFilterDecision.Suppressed;

    public static LandCoverFilterResult Retained(
        int year,
        double vegetationPercent,
        bool hasBuiltUpWithinProximity,
        string reason,
        string formattingSummary) =>
        new(
            LandCoverFilterDecision.Retained,
            year,
            vegetationPercent,
            hasBuiltUpWithinProximity,
            reason,
            formattingSummary);

    public static LandCoverFilterResult Suppressed(
        int year,
        double vegetationPercent,
        string reason,
        string formattingSummary) =>
        new(
            LandCoverFilterDecision.Suppressed,
            year,
            vegetationPercent,
            false,
            reason,
            formattingSummary);

    public static LandCoverFilterResult Unavailable(int? year, string reason) =>
        new(
            LandCoverFilterDecision.Unavailable,
            year,
            null,
            null,
            reason,
            "Land cover unavailable");
}
