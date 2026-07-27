using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ThermalWatch.Core;

public static class NotificationHistoricalFrpPolicy
{
    public const string CriterionCode = "historical-cluster-frp";
    private const double HistoricalPercentileFraction = 0.95;
    private const string CriterionLabel = "Historical location FRP";
    private const string HistoricalRequirement =
        "Strictly greater than the 95th percentile of matching cluster total FRP in the preceding 30 complete UTC days";
    private static readonly ConditionalWeakTable<FirmsHistory, HistoryIndexCache> s_historyIndexes = [];

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
        HistoricalSpatialIndex index = s_historyIndexes
            .GetValue(history, static value => new(value))
            .Get(options.ClusterRadiusKilometers);
        HashSet<DateOnly> fallbackDates = index.GetFallbackDates(currentMemberIds);
        ImmutableArray<HistoricalMatch>.Builder matching = ImmutableArray.CreateBuilder<HistoricalMatch>();
        matching.AddRange(index.FindMatches(cluster, fallbackDates));
        foreach (FirmsHistoryDay day in history.Days.Where(day => day.Date < history.RetainedThroughDate))
        {
            if (fallbackDates.Contains(day.Date))
                AddReclusteredMatches(cluster, day, currentMemberIds, options, matching);
        }

        return matching.ToImmutable();
    }

    private static void AddReclusteredMatches(
        NotificationCluster current,
        FirmsHistoryDay day,
        ImmutableHashSet<string> currentMemberIds,
        NotificationOptions options,
        ImmutableArray<HistoricalMatch>.Builder matching)
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
            if (historicalCluster.TotalFrpMegawatts is { } totalFrp
                && SpatiallyMatches(current, historicalCluster.Members, options.ClusterRadiusKilometers))
            {
                matching.Add(new(day.Date, totalFrp));
            }
        }
    }

    private static NotificationCriterionResult Compare(
        double currentFrp,
        ImmutableArray<HistoricalMatch> matching)
    {
        double historicalPercentileFrp = CalculateHistoricalPercentile(matching);
        int matchingDayCount = matching.Select(item => item.Date).Distinct().Count();
        bool passed = currentFrp > historicalPercentileFrp;
        string historicalValue = NotificationPolicy.FormatNumber(historicalPercentileFrp);
        string matchSummary = $"{matching.Length.ToString(CultureInfo.InvariantCulture)} matching cluster(s) across {matchingDayCount.ToString(CultureInfo.InvariantCulture)} day(s)";
        return new(
            Code: CriterionCode,
            Label: CriterionLabel,
            Outcome: passed ? NotificationCriterionOutcomes.Passed : NotificationCriterionOutcomes.Failed,
            ActualValue: $"{NotificationPolicy.FormatNumber(currentFrp)} MW current; {historicalValue} MW historical 95th percentile",
            Requirement: $"Current total FRP must be greater than the historical 95th percentile of {historicalValue} MW",
            Explanation: passed
                ? $"The current total FRP is strictly greater than the 95th percentile from {matchSummary}."
                : $"The current total FRP is not strictly greater than the 95th percentile from {matchSummary}.",
            IsBlocking: !passed);
    }

    private static double CalculateHistoricalPercentile(ImmutableArray<HistoricalMatch> matching)
    {
        double[] orderedValues =
        [
            .. matching
                .Select(item => item.TotalFrpMegawatts)
                .Order()
        ];
        double position = (orderedValues.Length - 1) * HistoricalPercentileFraction;
        int lowerIndex = (int)Math.Floor(position);
        int upperIndex = (int)Math.Ceiling(position);
        double interpolationFraction = position - lowerIndex;
        return orderedValues[lowerIndex]
            + ((orderedValues[upperIndex] - orderedValues[lowerIndex]) * interpolationFraction);
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
        IEnumerable<Anomaly> historicalMembers,
        double radiusKilometers) =>
        current.Members.Any(currentAnomaly => historicalMembers.Any(historicalAnomaly =>
            Geography.HaversineKilometers(currentAnomaly, historicalAnomaly) <= radiusKilometers));

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct HistoricalMatch(
        DateOnly Date,
        double TotalFrpMegawatts);

    private sealed class HistoryIndexCache(FirmsHistory history)
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<double, HistoricalSpatialIndex> _indexes = [];

        public HistoricalSpatialIndex Get(double radiusKilometers)
        {
            lock (_lock)
            {
                if (!_indexes.TryGetValue(radiusKilometers, out HistoricalSpatialIndex? index))
                {
                    index = new(history, radiusKilometers);
                    _indexes.Add(radiusKilometers, index);
                }

                return index;
            }
        }
    }

    private sealed class HistoricalSpatialIndex
    {
        private readonly double _cellSize;
        private readonly double _radiusKilometers;
        private readonly Dictionary<GeographicCell, List<IndexedHistoricalMember>> _membersByCell = [];
        private readonly Dictionary<HistoricalClusterKey, double> _totalFrpByCluster = [];
        private readonly Dictionary<string, DateOnly> _datesByAnomalyId = new(StringComparer.Ordinal);
        private readonly HashSet<DateOnly> _invalidDates = [];

        public HistoricalSpatialIndex(FirmsHistory history, double radiusKilometers)
        {
            _radiusKilometers = radiusKilometers;
            _cellSize = Geography.ChordLength(radiusKilometers);
            foreach (FirmsHistoryDay day in history.Days.Where(day => day.Date < history.RetainedThroughDate))
                AddDay(day);
        }

        public HashSet<DateOnly> GetFallbackDates(IEnumerable<string> currentMemberIds)
        {
            HashSet<DateOnly> dates = [.. _invalidDates];
            foreach (string memberId in currentMemberIds)
            {
                if (_datesByAnomalyId.TryGetValue(memberId, out DateOnly date))
                    dates.Add(date);
            }

            return dates;
        }

        public ImmutableArray<HistoricalMatch> FindMatches(
            NotificationCluster current,
            HashSet<DateOnly> excludedDates)
        {
            var matchingClusters = new HashSet<HistoricalClusterKey>();
            foreach (Anomaly currentAnomaly in current.Members)
            {
                GeographicCell cell = Geography.GetCell(
                    currentAnomaly.Latitude,
                    currentAnomaly.Longitude,
                    _cellSize);
                for (long x = cell.X - 1; x <= cell.X + 1; x++)
                {
                    for (long y = cell.Y - 1; y <= cell.Y + 1; y++)
                    {
                        for (long z = cell.Z - 1; z <= cell.Z + 1; z++)
                        {
                            if (!_membersByCell.TryGetValue(new(x, y, z), out List<IndexedHistoricalMember>? candidates))
                                continue;

                            foreach (IndexedHistoricalMember candidate in candidates)
                            {
                                if (!excludedDates.Contains(candidate.Cluster.Date)
                                    && Geography.HaversineKilometers(currentAnomaly, candidate.Anomaly) <= _radiusKilometers)
                                {
                                    matchingClusters.Add(candidate.Cluster);
                                }
                            }
                        }
                    }
                }
            }

            return
            [
                .. matchingClusters.Select(cluster => new HistoricalMatch(
                    cluster.Date,
                    _totalFrpByCluster[cluster]))
            ];
        }

        private void AddDay(FirmsHistoryDay day)
        {
            foreach (Anomaly anomaly in day.Anomalies)
                _datesByAnomalyId.TryAdd(anomaly.Id, day.Date);

            if (day.ClusterCount != day.Clusters.Length)
            {
                _invalidDates.Add(day.Date);
                return;
            }

            var anomaliesById = day.Anomalies.ToDictionary(
                anomaly => anomaly.Id,
                StringComparer.Ordinal);
            foreach (NotificationClusterSummary cluster in day.Clusters)
            {
                if (cluster.TotalFrpMegawatts is not { } totalFrp)
                    continue;

                var key = new HistoricalClusterKey(day.Date, cluster.ClusterId);
                _totalFrpByCluster.TryAdd(key, totalFrp);
                foreach (string memberId in cluster.MemberIds)
                {
                    if (!anomaliesById.TryGetValue(memberId, out Anomaly? anomaly))
                        continue;

                    GeographicCell cell = Geography.GetCell(
                        anomaly.Latitude,
                        anomaly.Longitude,
                        _cellSize);
                    if (!_membersByCell.TryGetValue(cell, out List<IndexedHistoricalMember>? members))
                    {
                        members = [];
                        _membersByCell.Add(cell, members);
                    }

                    members.Add(new(key, anomaly));
                }
            }
        }
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct HistoricalClusterKey(DateOnly Date, string ClusterId);

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct IndexedHistoricalMember(
        HistoricalClusterKey Cluster,
        Anomaly Anomaly);
}
