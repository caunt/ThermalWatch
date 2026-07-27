using System.Collections.Immutable;
using System.Globalization;

namespace ThermalWatch.Core;

public static class NotificationHistoricalFrpPolicy
{
    public const string CriterionCode = "historical-cluster-frp";
    private const string CriterionLabel = "Historical location FRP";
    private const string HistoricalRequirement =
        "Strictly greater than every matching cluster in the preceding 30 complete UTC days";

    public static NotificationCriterionResult Explain(
        NotificationCluster cluster,
        FirmsHistory? history,
        NotificationOptions options)
    {
        if (!options.HistoricalFrpFilterEnabled)
            return NotificationCriterionResult.Disabled(CriterionCode, CriterionLabel);

        string currentValue = FormatCurrentValue(cluster.TotalFrpMegawatts);
        if (history is null || !history.IsReady)
            return HistoryUnavailable(currentValue);

        if (cluster.TotalFrpMegawatts is not { } currentFrp)
            return CurrentFrpUnavailable(currentValue);

        ImmutableArray<HistoricalMatch> matching = FindMatchingClusters(cluster, history, options);
        return matching.IsEmpty
            ? NoHistoricalMatch(currentValue)
            : Compare(currentFrp, matching);
    }

    private static ImmutableArray<HistoricalMatch> FindMatchingClusters(
        NotificationCluster cluster,
        FirmsHistory history,
        NotificationOptions options)
    {
        var currentMemberIds = cluster.Members
            .Select(member => member.Id)
            .ToImmutableHashSet(StringComparer.Ordinal);
        ImmutableArray<HistoricalMatch>.Builder matching = ImmutableArray.CreateBuilder<HistoricalMatch>();
        foreach (FirmsHistoryDay day in history.Days.Where(day => day.Date < history.RetainedThroughDate))
        {
            Anomaly[] historicalAnomalies =
            [
                .. day.Anomalies.Where(anomaly => !currentMemberIds.Contains(anomaly.Id))
            ];
            foreach (NotificationCluster historicalCluster in NotificationClustering.Create(
                historicalAnomalies,
                options.ClusterRadiusKilometers,
                options.ClusterTimeWindow))
            {
                if (historicalCluster.TotalFrpMegawatts is not null
                    && SpatiallyMatches(cluster, historicalCluster, options.ClusterRadiusKilometers))
                {
                    matching.Add(new(day.Date, historicalCluster));
                }
            }
        }

        return matching.ToImmutable();
    }

    private static NotificationCriterionResult Compare(
        double currentFrp,
        ImmutableArray<HistoricalMatch> matching)
    {
        double maximumHistoricalFrp = matching.Max(item => item.Cluster.TotalFrpMegawatts!.Value);
        int matchingDayCount = matching.Select(item => item.Date).Distinct().Count();
        bool passed = currentFrp > maximumHistoricalFrp;
        string historicalValue = NotificationPolicy.FormatNumber(maximumHistoricalFrp);
        string matchSummary = $"{matching.Length.ToString(CultureInfo.InvariantCulture)} matching cluster(s) across {matchingDayCount.ToString(CultureInfo.InvariantCulture)} day(s)";
        return new(
            Code: CriterionCode,
            Label: CriterionLabel,
            Outcome: passed ? NotificationCriterionOutcomes.Passed : NotificationCriterionOutcomes.Failed,
            ActualValue: $"{NotificationPolicy.FormatNumber(currentFrp)} MW current; {historicalValue} MW historical maximum",
            Requirement: $"Current total FRP must be greater than {historicalValue} MW",
            Explanation: passed
                ? $"The current total FRP is strictly greater than {matchSummary}."
                : $"The current total FRP is not strictly greater than {matchSummary}.",
            IsBlocking: !passed);
    }

    private static NotificationCriterionResult HistoryUnavailable(string currentValue) =>
        new(
            Code: CriterionCode,
            Label: CriterionLabel,
            Outcome: NotificationCriterionOutcomes.Unavailable,
            ActualValue: currentValue,
            Requirement: HistoricalRequirement,
            Explanation: "The complete FIRMS history baseline is not currently available; the policy fails closed.",
            IsBlocking: true);

    private static NotificationCriterionResult CurrentFrpUnavailable(string currentValue) =>
        new(
            Code: CriterionCode,
            Label: CriterionLabel,
            Outcome: NotificationCriterionOutcomes.Failed,
            ActualValue: currentValue,
            Requirement: "Current total FRP must be available",
            Explanation: "No current cluster member contains an available FRP value.",
            IsBlocking: true);

    private static NotificationCriterionResult NoHistoricalMatch(string currentValue) =>
        new(
            Code: CriterionCode,
            Label: CriterionLabel,
            Outcome: NotificationCriterionOutcomes.Passed,
            ActualValue: currentValue,
            Requirement: HistoricalRequirement,
            Explanation: "No spatially matching historical cluster has an available total FRP value.",
            IsBlocking: false);

    private static string FormatCurrentValue(double? currentFrp) =>
        currentFrp is { } value
            ? $"{NotificationPolicy.FormatNumber(value)} MW"
            : "Not available";

    private static bool SpatiallyMatches(
        NotificationCluster current,
        NotificationCluster historical,
        double radiusKilometers) =>
        current.Members.Any(currentAnomaly => historical.Members.Any(historicalAnomaly =>
            Geography.HaversineKilometers(currentAnomaly, historicalAnomaly) <= radiusKilometers));

    private readonly record struct HistoricalMatch(
        DateOnly Date,
        NotificationCluster Cluster);
}
