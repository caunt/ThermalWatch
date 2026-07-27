using ThermalWatch.Api;
using ThermalWatch.Core;

namespace ThermalWatch.Tests;

public sealed class NotificationHistoricalFrpPolicyTests
{
    private static readonly DateTimeOffset s_now = new(
        year: 2026,
        month: 7,
        day: 27,
        hour: 12,
        minute: 0,
        second: 0,
        TimeSpan.Zero);

    [Theory]
    [InlineData(101, 100, NotificationCriterionOutcomes.Passed)]
    [InlineData(100, 100, NotificationCriterionOutcomes.Failed)]
    [InlineData(99, 100, NotificationCriterionOutcomes.Failed)]
    public void ExplainRequiresCurrentTotalToBeStrictlyGreaterThan95thPercentile(
        double currentFrp,
        double historicalFrp,
        string expectedOutcome)
    {
        NotificationOptions options = Options();
        NotificationCluster current = Cluster(Anomaly(
            id: "current",
            acquiredAtUtc: s_now,
            longitude: 30,
            frp: currentFrp));
        FirmsHistory history = History(
            isReady: true,
            anomalies: [Anomaly(
                id: "historical",
                acquiredAtUtc: s_now.AddDays(days: -1),
                longitude: 30,
                frp: historicalFrp)]);

        NotificationCriterionResult result = NotificationHistoricalFrpPolicy.Explain(current, history, options);

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(!string.Equals(expectedOutcome, NotificationCriterionOutcomes.Passed, StringComparison.Ordinal), result.IsBlocking);
    }

    [Theory]
    [InlineData(93, NotificationCriterionOutcomes.Passed)]
    [InlineData(92, NotificationCriterionOutcomes.Failed)]
    [InlineData(91, NotificationCriterionOutcomes.Failed)]
    public void ExplainUsesInclusiveLinearlyInterpolated95thPercentile(
        double currentFrp,
        string expectedOutcome)
    {
        NotificationCluster current = Cluster(Anomaly(
            id: "current",
            acquiredAtUtc: s_now,
            longitude: 30,
            frp: currentFrp));
        FirmsHistory history = History(
            isReady: true,
            Anomaly(id: "historical-10", acquiredAtUtc: s_now.AddDays(days: -1), longitude: 30, frp: 10),
            Anomaly(id: "historical-20", acquiredAtUtc: s_now.AddDays(days: -2), longitude: 30, frp: 20),
            Anomaly(id: "historical-100", acquiredAtUtc: s_now.AddDays(days: -3), longitude: 30, frp: 100));

        NotificationCriterionResult result = NotificationHistoricalFrpPolicy.Explain(
            current,
            history,
            Options());

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Contains(
            expectedSubstring: "92 MW historical 95th percentile",
            result.ActualValue,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExplainMatchesAnyMembersWithinClusterRadius()
    {
        NotificationOptions options = Options();
        NotificationCluster current = Cluster(Anomaly(
            id: "current",
            acquiredAtUtc: s_now,
            longitude: 30,
            frp: 300));
        FirmsHistory history = History(
            isReady: true,
            anomalies:
            [
                Anomaly(id: "near", acquiredAtUtc: s_now.AddDays(days: -1), longitude: 30.04, frp: 50),
                Anomaly(id: "representative-far", acquiredAtUtc: s_now.AddDays(days: -1), longitude: 30.08, frp: 200)
            ]);

        NotificationCriterionResult result = NotificationHistoricalFrpPolicy.Explain(current, history, options);

        Assert.Equal(NotificationCriterionOutcomes.Passed, result.Outcome);
        Assert.Contains(expectedSubstring: "250 MW historical 95th percentile", result.ActualValue, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplainRemovesCurrentMemberIdsAndReclustersHistoricalDay()
    {
        NotificationOptions options = Options();
        Anomaly currentAnomaly = Anomaly(
            id: "shared",
            acquiredAtUtc: s_now.AddDays(days: -1),
            longitude: 30,
            frp: 100);
        NotificationCluster current = Cluster(currentAnomaly);
        FirmsHistory history = History(
            isReady: true,
            anomalies:
            [
                currentAnomaly,
                Anomaly(
                    id: "older",
                    acquiredAtUtc: s_now.AddDays(days: -1).AddMinutes(minutes: -10),
                    longitude: 30,
                    frp: 50)
            ]);

        NotificationCriterionResult result = NotificationHistoricalFrpPolicy.Explain(current, history, options);

        Assert.Equal(NotificationCriterionOutcomes.Passed, result.Outcome);
        Assert.Contains(expectedSubstring: "50 MW historical 95th percentile", result.ActualValue, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplainUsesRetainedClusterTotalsForNonOverlappingHistoricalDays()
    {
        NotificationCluster current = Cluster(Anomaly(
            id: "current",
            acquiredAtUtc: s_now,
            longitude: 30,
            frp: 101));
        Anomaly historical = Anomaly(
            id: "historical",
            acquiredAtUtc: s_now.AddDays(days: -1),
            longitude: 30,
            frp: 100);
        FirmsHistory history = History(isReady: true, anomalies: [historical]);
        NotificationClusterSummary retainedSummary = Assert.Single(history.Days).Clusters.Single() with
        {
            TotalFrpMegawatts = 200
        };
        history = history with
        {
            Days =
            [
                history.Days.Single() with
                {
                    Clusters = [retainedSummary]
                }
            ]
        };

        NotificationCriterionResult result = NotificationHistoricalFrpPolicy.Explain(
            current,
            history,
            Options());

        Assert.Equal(NotificationCriterionOutcomes.Failed, result.Outcome);
        Assert.Contains(expectedSubstring: "200 MW historical 95th percentile", result.ActualValue, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplainPassesWithoutComparableHistoricalFrp()
    {
        NotificationOptions options = Options();
        NotificationCluster current = Cluster(Anomaly(
            id: "current",
            acquiredAtUtc: s_now,
            longitude: 30,
            frp: 100));
        FirmsHistory history = History(
            isReady: true,
            anomalies:
            [
                Anomaly(id: "distant", acquiredAtUtc: s_now.AddDays(days: -1), longitude: 40, frp: 500),
                Anomaly(id: "missing", acquiredAtUtc: s_now.AddDays(days: -2), longitude: 30, frp: null)
            ]);

        NotificationCriterionResult result = NotificationHistoricalFrpPolicy.Explain(current, history, options);

        Assert.Equal(NotificationCriterionOutcomes.Passed, result.Outcome);
        Assert.False(result.IsBlocking);
    }

    [Fact]
    public void ExplainFailsWhenCurrentTotalFrpIsUnavailable()
    {
        NotificationCluster current = Cluster(Anomaly(
            id: "current",
            acquiredAtUtc: s_now,
            longitude: 30,
            frp: null));

        NotificationCriterionResult result = NotificationHistoricalFrpPolicy.Explain(
            current,
            History(isReady: true),
            Options());

        Assert.Equal(NotificationCriterionOutcomes.Failed, result.Outcome);
        Assert.True(result.IsBlocking);
    }

    [Fact]
    public void ExplainFailsClosedWhenHistoryIsIncomplete()
    {
        NotificationCluster current = Cluster(Anomaly(
            id: "current",
            acquiredAtUtc: s_now,
            longitude: 30,
            frp: 100));

        NotificationCriterionResult result = NotificationHistoricalFrpPolicy.Explain(
            current,
            History(isReady: false),
            Options());

        Assert.Equal(NotificationCriterionOutcomes.Unavailable, result.Outcome);
        Assert.True(result.IsBlocking);
    }

    [Fact]
    public void ExplainReportsDisabledWhenConfiguredOff()
    {
        NotificationOptions options = Options() with { HistoricalFrpFilterEnabled = false };
        NotificationCluster current = Cluster(Anomaly(
            id: "current",
            acquiredAtUtc: s_now,
            longitude: 30,
            frp: null));

        NotificationCriterionResult result = NotificationHistoricalFrpPolicy.Explain(
            current,
            history: null,
            options);

        Assert.Equal(NotificationCriterionOutcomes.Disabled, result.Outcome);
        Assert.False(result.IsBlocking);
    }

    private static NotificationOptions Options() =>
        ApplicationConfiguration.ParseNotificationOptions(_ => null) with
        {
            ClusterRadiusKilometers = 5,
            ClusterTimeWindow = TimeSpan.FromMinutes(minutes: 90)
        };

    private static FirmsHistory History(bool isReady, params Anomaly[] anomalies)
    {
        NotificationOptions options = Options();
        var today = DateOnly.FromDateTime(s_now.UtcDateTime);
        FirmsHistoryDay[] days =
        [
            .. anomalies
                .GroupBy(anomaly => DateOnly.FromDateTime(anomaly.AcquiredAtUtc.UtcDateTime))
                .Select(group =>
                {
                    Anomaly[] dayAnomalies = [.. group];
                    NotificationClusterSummary[] clusters =
                    [
                        .. NotificationClustering.Create(
                                dayAnomalies,
                                options.ClusterRadiusKilometers,
                                options.ClusterTimeWindow)
                            .Select(NotificationClusterSummary.FromCluster)
                    ];
                    return new FirmsHistoryDay(
                        group.Key,
                        IsComplete: true,
                        IsPartiallyStale: false,
                        Segments: [],
                        AnomalyCount: dayAnomalies.Length,
                        Anomalies: [.. dayAnomalies],
                        ClusterCount: clusters.Length,
                        Clusters: [.. clusters]);
                })
        ];
        return new(
            s_now,
            FirmsHistoryStore.CompletedDayCount,
            today.AddDays(-FirmsHistoryStore.CompletedDayCount),
            today,
            today.AddDays(-FirmsHistoryStore.CompletedDayCount),
            today,
            isReady,
            IsPartiallyStale: !isReady,
            [.. days]);
    }

    private static NotificationCluster Cluster(params Anomaly[] anomalies) =>
        Assert.Single(NotificationClustering.Create(
            anomalies,
            radiusKilometers: 5,
            timeWindow: TimeSpan.FromMinutes(minutes: 90)));

    private static Anomaly Anomaly(
        string id,
        DateTimeOffset acquiredAtUtc,
        double longitude,
        double? frp) =>
        new(
            id,
            CountryCode: "UKR",
            Source: "VIIRS_SNPP_NRT",
            Satellite: "Suomi-NPP",
            Instrument: "VIIRS",
            Latitude: 50,
            longitude,
            acquiredAtUtc,
            DayNight: "D",
            BrightnessKelvin: 330,
            SecondaryBrightnessKelvin: 300,
            frp,
            ScanKilometers: 0.4,
            TrackKilometers: 0.4,
            ConfidenceRaw: "n",
            ConfidencePercent: null,
            ConfidenceCategory: "nominal",
            Version: "2.0NRT",
            GoogleMapsUrl: $"https://example.test/{id}");
}
