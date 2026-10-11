namespace ThermalWatch.Core;

/// <summary>The vegetation policy decision and supporting presentation values.</summary>
/// <param name="Decision">Whether samples retain, suppress, or cannot evaluate the cluster.</param>
/// <param name="LandCoverYear">The common annual sample year when known.</param>
/// <param name="VegetationPercent">The percentage of valid samples classified as vegetation.</param>
/// <param name="HasBuiltUpWithinProximity">The available built-up proximity result.</param>
/// <param name="Reason">The explanation of the decision.</param>
/// <param name="FormattingSummary">The compact land-cover summary for delivery.</param>
public readonly record struct NotificationLandCoverResult(
    NotificationLandCoverDecision Decision,
    int? LandCoverYear,
    double? VegetationPercent,
    bool? HasBuiltUpWithinProximity,
    string Reason,
    string FormattingSummary
)
{
    /// <summary>Creates a retained decision from available samples.</summary>
    public static NotificationLandCoverResult Retained(int year, double vegetationPercent, bool hasBuiltUpWithinProximity, string reason, string formattingSummary)
    {
        return new(NotificationLandCoverDecision.Retained, year, vegetationPercent, hasBuiltUpWithinProximity, reason, formattingSummary);
    }

    /// <summary>Creates a vegetation suppression decision.</summary>
    public static NotificationLandCoverResult Suppressed(int year, double vegetationPercent, string reason, string formattingSummary)
    {
        return new(NotificationLandCoverDecision.Suppressed, year, vegetationPercent, HasBuiltUpWithinProximity: false, reason, formattingSummary);
    }

    /// <summary>Creates an unavailable decision that permits notification under the fail-open rule.</summary>
    public static NotificationLandCoverResult Unavailable(int? year, string reason)
    {
        return new(
            NotificationLandCoverDecision.Unavailable,
            year,
            VegetationPercent: null,
            HasBuiltUpWithinProximity: null,
            reason,
            FormattingSummary: "Land cover unavailable"
        );
    }
}
