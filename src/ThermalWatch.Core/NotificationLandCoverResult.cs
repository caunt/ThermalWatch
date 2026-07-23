namespace ThermalWatch.Core;

public readonly record struct NotificationLandCoverResult(
    NotificationLandCoverDecision Decision,
    int? LandCoverYear,
    double? VegetationPercent,
    bool? HasBuiltUpWithinProximity,
    string Reason,
    string FormattingSummary)
{
    public bool IsSuppressed => Decision == NotificationLandCoverDecision.Suppressed;

    public static NotificationLandCoverResult Retained(
        int year,
        double vegetationPercent,
        bool hasBuiltUpWithinProximity,
        string reason,
        string formattingSummary) =>
        new(
            NotificationLandCoverDecision.Retained,
            year,
            vegetationPercent,
            hasBuiltUpWithinProximity,
            reason,
            formattingSummary);

    public static NotificationLandCoverResult Suppressed(
        int year,
        double vegetationPercent,
        string reason,
        string formattingSummary) =>
        new(
            NotificationLandCoverDecision.Suppressed,
            year,
            vegetationPercent,
            HasBuiltUpWithinProximity: false,
            reason,
            formattingSummary);

    public static NotificationLandCoverResult Unavailable(int? year, string reason) =>
        new(
            NotificationLandCoverDecision.Unavailable,
            year,
            VegetationPercent: null,
            HasBuiltUpWithinProximity: null,
            reason,
            FormattingSummary: "Land cover unavailable");
}
