using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ThermalWatch.Core;

/// <summary>Compares current cluster FRP with spatially matching historical clusters.</summary>
public static class NotificationHistoricalFrpPolicy
{
    /// <summary>Identifies the historical FRP notification criterion.</summary>
    public const string CriterionCode = "historical-cluster-frp";
    private const string CriterionLabel = "Historical location FRP";
    private const double HistoricalPercentileFraction = 0.95;
    private const double HistoricalPercentileMultiplier = 1.5;
    private const double HistoricalPercentileOffsetMegawatts = 75;
    private const string HistoricalRequirement =
        "Strictly greater than both 1.5 times the historical 95th percentile and the historical 95th percentile plus 75 MW";
    private static readonly ConditionalWeakTable<FirmsHistory, HistoryIndexCache> HistoryIndexes = [];

    /// <summary>Explains whether the current cluster exceeds its historical FRP thresholds.</summary>
    public static NotificationCriterionResult Explain(NotificationCluster cluster, FirmsHistory? history, NotificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(options);

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

    private static void AddReclusteredMatches(
        NotificationCluster current,
        FirmsHistoryDay day,
        ImmutableHashSet<string> currentMemberIds,
        NotificationOptions options,
        ImmutableArray<HistoricalMatch>.Builder matching
    )
    {
        Anomaly[] historicalAnomalies =
        [
            .. day.Anomalies.Where(anomaly => !currentMemberIds.Contains(anomaly.Id))
        ];

        foreach (NotificationCluster historicalCluster in NotificationClustering.Create(historicalAnomalies, options.ClusterRadiusKilometers, options.ClusterTimeWindow))
        {
            if (historicalCluster.TotalFrpMegawatts is not { } totalFrp)
                continue;

            if (SpatiallyMatches(current, historicalCluster.Members, options.ClusterRadiusKilometers))
                matching.Add(new(day.Date, totalFrp));
        }
    }

    private static double CalculateHistoricalPercentile(ImmutableArray<HistoricalMatch> matching)
    {
        double[] orderedValues =
        [
            .. matching
                .Select(static item => item.TotalFrpMegawatts)
                .Order()
        ];

        double position = (orderedValues.Length - 1) * HistoricalPercentileFraction;
        int lowerIndex = (int)Math.Floor(position);
        int upperIndex = (int)Math.Ceiling(position);
        double interpolationFraction = position - lowerIndex;

        return orderedValues[lowerIndex]
            + ((orderedValues[upperIndex] - orderedValues[lowerIndex]) * interpolationFraction);
    }

    private static NotificationCriterionResult Compare(double currentFrp, ImmutableArray<HistoricalMatch> matching)
    {
        double historicalPercentileFrp = CalculateHistoricalPercentile(matching);
        double multipliedThresholdFrp = historicalPercentileFrp * HistoricalPercentileMultiplier;
        double offsetThresholdFrp = historicalPercentileFrp + HistoricalPercentileOffsetMegawatts;
        int matchingDayCount = matching.Select(static item => item.Date).Distinct().Count();
        bool passed = currentFrp > multipliedThresholdFrp && currentFrp > offsetThresholdFrp;
        string historicalValue = NotificationPolicy.FormatNumber(historicalPercentileFrp);
        string multipliedThresholdValue = NotificationPolicy.FormatNumber(multipliedThresholdFrp);
        string offsetThresholdValue = NotificationPolicy.FormatNumber(offsetThresholdFrp);
        string matchSummary = $"{matching.Length.ToString(CultureInfo.InvariantCulture)} matching cluster(s) across {matchingDayCount.ToString(CultureInfo.InvariantCulture)} day(s)";

        return new(
            CriterionCode,
            CriterionLabel,
            passed ? NotificationCriterionOutcomes.Passed : NotificationCriterionOutcomes.Failed,
            $"{NotificationPolicy.FormatNumber(currentFrp)} MW current; {historicalValue} MW historical 95th percentile; thresholds {multipliedThresholdValue} MW (p95 x 1.5) and {offsetThresholdValue} MW (p95 + 75 MW)",
            $"Current total FRP must be greater than both {multipliedThresholdValue} MW (historical p95 x 1.5) and {offsetThresholdValue} MW (historical p95 + 75 MW)",
            passed
                ? $"The current total FRP is strictly greater than both thresholds derived from {matchSummary}."
                : $"The current total FRP is not strictly greater than both thresholds derived from {matchSummary}.",
            !passed
        );
    }

    private static NotificationCriterionResult CurrentFrpUnavailable(string currentValue)
    {
        return new(
            CriterionCode,
            CriterionLabel,
            NotificationCriterionOutcomes.Failed,
            currentValue,
            Requirement: "Current total FRP must be available",
            Explanation: "No current cluster member contains an available FRP value.",
            IsBlocking: true
        );
    }

    private static ImmutableArray<HistoricalMatch> FindMatchingClusters(NotificationCluster cluster, FirmsHistory history, NotificationOptions options)
    {
        var currentMemberIds = cluster.Members
            .Select(member => member.Id)
            .ToImmutableHashSet(StringComparer.Ordinal);

        HistoricalSpatialIndex index = HistoryIndexes
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

    private static string FormatCurrentValue(double? currentFrp)
    {
        return currentFrp is { } value
            ? $"{NotificationPolicy.FormatNumber(value)} MW"
            : "Not available";
    }

    private static NotificationCriterionResult HistoryUnavailable(string currentValue)
    {
        return new(
            CriterionCode,
            CriterionLabel,
            NotificationCriterionOutcomes.Unavailable,
            currentValue,
            HistoricalRequirement,
            Explanation: "The complete FIRMS history baseline is not currently available; the policy fails closed.",
            IsBlocking: true
        );
    }

    private static NotificationCriterionResult NoHistoricalMatch(string currentValue)
    {
        return new(
            CriterionCode,
            CriterionLabel,
            NotificationCriterionOutcomes.Passed,
            currentValue,
            HistoricalRequirement,
            Explanation: "No spatially matching historical cluster has an available total FRP value.",
            IsBlocking: false
        );
    }

    private static bool SpatiallyMatches(NotificationCluster current, ImmutableArray<Anomaly> historicalMembers, double radiusKilometers)
    {
        return current.Members.Any(
            currentAnomaly => historicalMembers.Any(historicalAnomaly => Geography.HaversineKilometers(currentAnomaly, historicalAnomaly) <= radiusKilometers)
        );
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct HistoricalClusterKey(DateOnly Date, string ClusterId);

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct HistoricalMatch(DateOnly Date, double TotalFrpMegawatts);

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct IndexedHistoricalMember(HistoricalClusterKey Cluster, Anomaly Anomaly);

    private sealed class HistoricalSpatialIndex
    {
        private readonly double _cellSize;
        private readonly Dictionary<GeographicCell, List<IndexedHistoricalMember>> _membersByCell = [];
        private readonly Dictionary<HistoricalClusterKey, double> _totalFrpByCluster = [];
        private readonly double _radiusKilometers;
        private ImmutableDictionary<string, DateOnly> _datesByAnomalyId = ImmutableDictionary.Create<string, DateOnly>(StringComparer.Ordinal);
        private readonly List<DateOnly> _invalidDates = [];

        public HistoricalSpatialIndex(FirmsHistory history, double radiusKilometers)
        {
            _radiusKilometers = radiusKilometers;
            _cellSize = Geography.ChordLength(radiusKilometers);

            foreach (FirmsHistoryDay day in history.Days.Where(day => day.Date < history.RetainedThroughDate))
                AddDay(day);
        }

        public ImmutableArray<HistoricalMatch> FindMatches(NotificationCluster current, HashSet<DateOnly> excludedDates)
        {
            var matchingClusters = new HashSet<HistoricalClusterKey>();
            ImmutableArray<HistoricalMatch>.Builder matches = ImmutableArray.CreateBuilder<HistoricalMatch>();

            foreach (Anomaly currentAnomaly in current.Members)
            {
                GeographicCell cell = Geography.GetCell(currentAnomaly.Latitude, currentAnomaly.Longitude, _cellSize);

                for (long horizontal = cell.Horizontal - 1; horizontal <= cell.Horizontal + 1; horizontal++)
                {
                    for (long vertical = cell.Vertical - 1; vertical <= cell.Vertical + 1; vertical++)
                    {
                        for (long depth = cell.Depth - 1; depth <= cell.Depth + 1; depth++)
                        {
                            if (!_membersByCell.TryGetValue(new(horizontal, vertical, depth), out List<IndexedHistoricalMember>? candidates))
                                continue;

                            foreach (IndexedHistoricalMember candidate in candidates)
                            {
                                if (excludedDates.Contains(candidate.Cluster.Date) || matchingClusters.Contains(candidate.Cluster))
                                    continue;

                                if (Geography.HaversineKilometers(currentAnomaly, candidate.Anomaly) <= _radiusKilometers)
                                {
                                    if (matchingClusters.Add(candidate.Cluster))
                                        matches.Add(new(candidate.Cluster.Date, _totalFrpByCluster[candidate.Cluster]));
                                }
                            }
                        }
                    }
                }
            }

            return matches.ToImmutable();
        }

        public HashSet<DateOnly> GetFallbackDates(IEnumerable<string> currentMemberIds)
        {
            List<DateOnly> dates = [.. _invalidDates];

            foreach (string memberId in currentMemberIds)
            {
                if (_datesByAnomalyId.TryGetValue(memberId, out DateOnly date))
                    dates.Add(date);
            }

            return [.. dates];
        }

        private void AddDay(FirmsHistoryDay day)
        {
            foreach (Anomaly anomaly in day.Anomalies)
            {
                if (!_datesByAnomalyId.ContainsKey(anomaly.Id))
                    _datesByAnomalyId = _datesByAnomalyId.SetItem(anomaly.Id, day.Date);
            }

            if (day.ClusterCount != day.Clusters.Length)
            {
                _invalidDates.Add(day.Date);

                return;
            }

            var anomaliesById = day.Anomalies.ToDictionary(static anomaly => anomaly.Id, StringComparer.Ordinal);

            foreach (NotificationClusterSummary cluster in day.Clusters)
            {
                if (cluster.TotalFrpMegawatts is not { } totalFrp)
                    continue;

                var key = new HistoricalClusterKey(day.Date, cluster.ClusterId);

                if (!_totalFrpByCluster.TryAdd(key, totalFrp))
                    continue;

                foreach (string memberId in cluster.MemberIds)
                {
                    if (!anomaliesById.TryGetValue(memberId, out Anomaly? anomaly))
                        continue;

                    GeographicCell cell = Geography.GetCell(anomaly.Latitude, anomaly.Longitude, _cellSize);

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

    private sealed class HistoryIndexCache(FirmsHistory history)
    {
        private readonly ConcurrentDictionary<double, Lazy<HistoricalSpatialIndex>> _indexes = [];

        public HistoricalSpatialIndex Get(double radiusKilometers)
        {
            return _indexes.GetOrAdd(radiusKilometers, radius => new(() => new(history, radius))).Value;
        }
    }
}
