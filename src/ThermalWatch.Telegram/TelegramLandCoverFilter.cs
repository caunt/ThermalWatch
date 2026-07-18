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
        var representativeFrp = cluster.Representative.FrpMegawatts;
        if (representativeFrp is null
            || representativeFrp >= options.VegetationMaximumFrpMegawatts)
        {
            return LandCoverFilterResult.Retained;
        }

        var hasMultipleSatellites = cluster.Members
            .Select(member => member.Satellite)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Skip(1)
            .Any();
        if (options.KeepMultiSatelliteClusters && hasMultipleSatellites)
            return LandCoverFilterResult.Retained;

        var landCover = await gibsClient.GetLandCoverAsync(
            cluster.Members,
            options.BuiltUpProximityKilometers,
            cancellationToken);
        if (!landCover.IsAvailable)
            return LandCoverFilterResult.Unavailable(landCover.Year);

        var vegetationCount = landCover.DetectionClasses.Count(IsVegetation);
        var vegetationPercent = vegetationCount * 100d / landCover.DetectionClasses.Length;
        var formattingSummary = landCover.HasBuiltUpWithinProximity
            ? "Urban/built-up nearby"
            : vegetationPercent >= options.VegetationPercentThreshold
                ? $"Mostly vegetation · {vegetationPercent.ToString("0.##", CultureInfo.InvariantCulture)}%"
                : "Mixed land cover";
        if (vegetationPercent < options.VegetationPercentThreshold
            || landCover.HasBuiltUpWithinProximity)
        {
            return LandCoverFilterResult.RetainedForYear(
                landCover.Year!.Value,
                vegetationPercent,
                formattingSummary);
        }

        return new(
            LandCoverFilterDecision.Suppressed,
            landCover.Year,
            vegetationPercent,
            representativeFrp.Value,
            formattingSummary);
    }

    private static bool IsVegetation(byte landCoverClass) =>
        landCoverClass is >= 1 and <= 12 or 14;
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
    double? RepresentativeFrpMegawatts,
    string? FormattingSummary)
{
    public static LandCoverFilterResult Retained { get; } =
        new(LandCoverFilterDecision.Retained, null, null, null, null);

    public static LandCoverFilterResult RetainedForYear(
        int year,
        double vegetationPercent,
        string formattingSummary) =>
        new(
            LandCoverFilterDecision.Retained,
            year,
            vegetationPercent,
            null,
            formattingSummary);

    public static LandCoverFilterResult Unavailable(int? year) =>
        new(
            LandCoverFilterDecision.Unavailable,
            year,
            null,
            null,
            "Land cover unavailable");
}
