namespace ThermalWatch.Telegram;

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
            HasBuiltUpWithinProximity: false,
            reason,
            formattingSummary);

    public static LandCoverFilterResult Unavailable(int? year, string reason) =>
        new(
            LandCoverFilterDecision.Unavailable,
            year,
            VegetationPercent: null,
            HasBuiltUpWithinProximity: null,
            reason,
            FormattingSummary: "Land cover unavailable");
}
