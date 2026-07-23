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
        GibsLandCoverResult landCover = await gibsClient.GetLandCoverAsync(
            cluster.Members,
            options.BuiltUpProximityKilometers,
            cancellationToken).ConfigureAwait(false);
        return Evaluate(cluster, options, landCover);
    }

    internal static LandCoverFilterResult Evaluate(
        NotificationCluster cluster,
        TelegramLandCoverOptions options,
        GibsLandCoverResult landCover)
    {
        if (GetUnavailableResult(landCover) is { } unavailableResult)
            return unavailableResult;

        int landCoverYear = landCover.Year!.Value;
        (double vegetationPercent, string formattedPercent, string formattingSummary) =
            SummarizeLandCover(landCover);

        if (vegetationPercent < options.VegetationPercentThreshold)
        {
            return LandCoverFilterResult.Retained(
                landCoverYear,
                vegetationPercent,
                landCover.HasBuiltUpWithinProximity,
                reason: $"Vegetation {formattedPercent}% is below {FormatNumber(options.VegetationPercentThreshold)}%",
                formattingSummary);
        }

        if (landCover.HasBuiltUpWithinProximity)
        {
            return LandCoverFilterResult.Retained(
                landCoverYear,
                vegetationPercent,
                hasBuiltUpWithinProximity: true,
                reason: $"NASA class 13 is within {FormatNumber(options.BuiltUpProximityKilometers)} km",
                formattingSummary);
        }

        if (options.KeepHighFrpVegetation
            && cluster.Representative.FrpMegawatts is { } frp
            && frp >= options.VegetationMaximumFrpMegawatts)
        {
            return LandCoverFilterResult.Retained(
                landCoverYear,
                vegetationPercent,
                hasBuiltUpWithinProximity: false,
                reason: $"Configured high-FRP vegetation exception retained {FormatNumber(frp)} MW",
                formattingSummary);
        }

        bool hasMultipleSatellites = cluster.Members
            .Select(member => member.Satellite)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Skip(count: 1)
            .Any();
        if (options.KeepMultiSatelliteVegetation && hasMultipleSatellites)
        {
            return LandCoverFilterResult.Retained(
                landCoverYear,
                vegetationPercent,
                hasBuiltUpWithinProximity: false,
                reason: "Configured multi-satellite vegetation exception retained cluster",
                formattingSummary);
        }

        return LandCoverFilterResult.Suppressed(
            landCoverYear,
            vegetationPercent,
            reason: $"Vegetation {formattedPercent}% meets {FormatNumber(options.VegetationPercentThreshold)}%; no NASA class 13 within {FormatNumber(options.BuiltUpProximityKilometers)} km",
            formattingSummary);
    }

    private static LandCoverFilterResult? GetUnavailableResult(GibsLandCoverResult landCover)
    {
        if (!landCover.IsAvailable)
        {
            return LandCoverFilterResult.Unavailable(
                landCover.Year,
                reason: "NASA land-cover data unavailable");
        }

        if (landCover.Year is null
            || landCover.SampledClasses.IsDefaultOrEmpty
            || landCover.SampledClasses.Any(landCoverClass => landCoverClass is < 1 or > 17))
        {
            return LandCoverFilterResult.Unavailable(
                landCover.Year,
                reason: "NASA land-cover data invalid");
        }

        return null;
    }

    private static (double VegetationPercent, string FormattedPercent, string FormattingSummary)
        SummarizeLandCover(GibsLandCoverResult landCover)
    {
        int vegetationCount = landCover.SampledClasses.Count(IsVegetation);
        double vegetationPercent = vegetationCount * 100d / landCover.SampledClasses.Length;
        string formattedPercent = FormatNumber(vegetationPercent);
        string formattingSummary = landCover.HasBuiltUpWithinProximity
            ? $"Urban/built-up nearby · vegetation {formattedPercent}%"
            : $"Vegetation · {formattedPercent}%";
        return (vegetationPercent, formattedPercent, formattingSummary);
    }

    private static bool IsVegetation(byte landCoverClass) =>
        landCoverClass is >= 1 and <= 12 or 14;

    private static string FormatNumber(double value) =>
        value.ToString(format: "0.##", CultureInfo.InvariantCulture);
}
