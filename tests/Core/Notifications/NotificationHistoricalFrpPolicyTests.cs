using ThermalWatch.Api;
using ThermalWatch.Core;

namespace ThermalWatch.Tests.Core.Notifications;

/// <summary>Verifies historical FRP comparison, readiness, and spatial matching.</summary>
public sealed class NotificationHistoricalFrpPolicyTests
{
    private static readonly DateTimeOffset Now = new(year: 2026, month: 7, day: 27, hour: 12, minute: 0, second: 0, TimeSpan.Zero);

    /// <summary>Provides interpolated-percentile threshold boundary cases.</summary>
    public static TheoryData<double, string> PercentileCases => new()
    {
        { 168, NotificationCriterionOutcomes.Passed },
        { 167, NotificationCriterionOutcomes.Failed },
        { 166, NotificationCriterionOutcomes.Failed }
    };

    /// <summary>Provides boundary cases for the proportional and absolute FRP thresholds.</summary>
    public static TheoryData<double, double, string> ThresholdCases => new()
    {
        { 176, 100, NotificationCriterionOutcomes.Passed },
        { 175, 100, NotificationCriterionOutcomes.Failed },
        { 226, 150, NotificationCriterionOutcomes.Passed },
        { 225, 150, NotificationCriterionOutcomes.Failed },
        { 301, 200, NotificationCriterionOutcomes.Passed },
        { 300, 200, NotificationCriterionOutcomes.Failed }
    };

    /// <summary>Blocks selection until historical acquisition is complete.</summary>
    [Fact]
    public void ExplainFailsClosedWhenHistoryIsIncomplete()
    {
        NotificationCluster current = Cluster(Anomaly(id: "current", Now, longitude: 30, frp: 100));

        NotificationCriterionResult result = NotificationHistoricalFrpPolicy.Explain(current, History(isReady: false), Options());

        Assert.Equal(NotificationCriterionOutcomes.Unavailable, result.Outcome);
        Assert.True(result.IsBlocking);
    }

    /// <summary>Rejects a current cluster without measurable total FRP.</summary>
    [Fact]
    public void ExplainFailsWhenCurrentTotalFrpIsUnavailable()
    {
        NotificationCluster current = Cluster(Anomaly(id: "current", Now, longitude: 30, frp: null));

        NotificationCriterionResult result = NotificationHistoricalFrpPolicy.Explain(current, History(isReady: true), Options());

        Assert.Equal(NotificationCriterionOutcomes.Failed, result.Outcome);
        Assert.True(result.IsBlocking);
    }

    /// <summary>Matches a historical cluster through any nearby member.</summary>
    [Fact]
    public void ExplainMatchesAnyMembersWithinClusterRadius()
    {
        NotificationOptions options = Options();

        NotificationCluster current = Cluster(Anomaly(id: "current", Now, longitude: 30, frp: 376));

        FirmsHistory history = History(
            isReady: true,
            anomalies:
            [
                Anomaly(id: "near", Now.AddDays(days: -1), longitude: 30.04, frp: 50),
                Anomaly(id: "representative-far", Now.AddDays(days: -1), longitude: 30.08, frp: 200)
            ]
        );

        NotificationCriterionResult result = NotificationHistoricalFrpPolicy.Explain(current, history, options);

        Assert.Equal(NotificationCriterionOutcomes.Passed, result.Outcome);
        Assert.Contains(expectedSubstring: "250 MW historical 95th percentile", result.ActualValue, StringComparison.Ordinal);
    }

    /// <summary>Matches historical observations across the antimeridian.</summary>
    [Fact]
    public void ExplainMatchesRetainedMembersAcrossAntimeridian()
    {
        NotificationCluster current = Cluster(Anomaly(id: "current", Now, longitude: -179.99, frp: 200));

        FirmsHistory history = History(isReady: true, anomalies: [Anomaly(id: "historical", Now.AddDays(days: -1), longitude: 179.99, frp: 100)]);

        NotificationCriterionResult result = NotificationHistoricalFrpPolicy.Explain(current, history, Options());

        Assert.Equal(NotificationCriterionOutcomes.Passed, result.Outcome);
        Assert.Contains(expectedSubstring: "100 MW historical 95th percentile", result.ActualValue, StringComparison.Ordinal);
    }

    /// <summary>Passes when no comparable historical FRP exists.</summary>
    [Fact]
    public void ExplainPassesWithoutComparableHistoricalFrp()
    {
        NotificationOptions options = Options();

        NotificationCluster current = Cluster(Anomaly(id: "current", Now, longitude: 30, frp: 100));

        FirmsHistory history = History(
            isReady: true,
            anomalies:
            [
                Anomaly(id: "distant", Now.AddDays(days: -1), longitude: 40, frp: 500),
                Anomaly(id: "missing", Now.AddDays(days: -2), longitude: 30, frp: null)
            ]
        );

        NotificationCriterionResult result = NotificationHistoricalFrpPolicy.Explain(current, history, options);

        Assert.Equal(NotificationCriterionOutcomes.Passed, result.Outcome);
        Assert.False(result.IsBlocking);
    }

    /// <summary>Removes current observation identifiers before reclustering their historical day.</summary>
    [Fact]
    public void ExplainRemovesCurrentMemberIdsAndReclustersHistoricalDay()
    {
        NotificationOptions options = Options();

        Anomaly currentAnomaly = Anomaly(id: "shared", Now.AddDays(days: -1), longitude: 30, frp: 126);

        NotificationCluster current = Cluster(currentAnomaly);

        FirmsHistory history = History(
            isReady: true,
            anomalies:
            [
                currentAnomaly,
                Anomaly(id: "older", Now.AddDays(days: -1).AddMinutes(minutes: -10), longitude: 30, frp: 50)
            ]
        );

        NotificationCriterionResult result = NotificationHistoricalFrpPolicy.Explain(current, history, options);

        Assert.Equal(NotificationCriterionOutcomes.Passed, result.Outcome);
        Assert.Contains(expectedSubstring: "50 MW historical 95th percentile", result.ActualValue, StringComparison.Ordinal);
    }

    /// <summary>Reports a nonblocking disabled criterion when filtering is off.</summary>
    [Fact]
    public void ExplainReportsDisabledWhenConfiguredOff()
    {
        NotificationOptions options = Options() with { HistoricalFrpFilterEnabled = false };

        NotificationCluster current = Cluster(Anomaly(id: "current", Now, longitude: 30, frp: null));

        NotificationCriterionResult result = NotificationHistoricalFrpPolicy.Explain(current, history: null, options);

        Assert.Equal(NotificationCriterionOutcomes.Disabled, result.Outcome);
        Assert.False(result.IsBlocking);
    }

    /// <summary>Requires the current FRP to exceed both historical thresholds strictly.</summary>
    [Theory]
    [MemberData(nameof(ThresholdCases))]
    public void ExplainRequiresCurrentTotalToBeStrictlyGreaterThanBothDerivedThresholds(double currentFrp, double historicalFrp, string expectedOutcome)
    {
        NotificationOptions options = Options();

        NotificationCluster current = Cluster(Anomaly(id: "current", Now, longitude: 30, currentFrp));

        FirmsHistory history = History(isReady: true, anomalies: [Anomaly(id: "historical", Now.AddDays(days: -1), longitude: 30, historicalFrp)]);

        NotificationCriterionResult result = NotificationHistoricalFrpPolicy.Explain(current, history, options);

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(!string.Equals(expectedOutcome, NotificationCriterionOutcomes.Passed, StringComparison.Ordinal), result.IsBlocking);
    }

    /// <summary>Evaluates many clusters against complete history within a bounded test lifetime.</summary>
    [Fact(Timeout = 10_000)]
    public void ExplainScalesAcrossManyCurrentClustersAndCompleteHistory()
    {
        const int anomaliesPerDay = 1_000;

        Anomaly[] historicalAnomalies =
        [
            .. Enumerable.Range(start: 0, FirmsHistoryStore.CompletedDayCount * anomaliesPerDay)
                .Select(
                    index => Anomaly($"historical-{index}", Now.AddDays(-1 - (index / anomaliesPerDay)), -179.8 + (index % anomaliesPerDay * 0.3596), frp: 100)
                )
        ];

        FirmsHistory history = History(isReady: true, historicalAnomalies);
        NotificationOptions options = Options();

        foreach (int index in Enumerable.Range(start: 0, count: 2_000))
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();

            NotificationCluster current = Cluster(Anomaly($"current-{index}", Now, -179.8 + (index * 0.1798), frp: 200));

            NotificationCriterionResult result = NotificationHistoricalFrpPolicy.Explain(current, history, options);

            Assert.False(result.IsBlocking);
        }
    }

    /// <summary>Uses inclusive linear interpolation when computing the historical percentile.</summary>
    [Theory]
    [MemberData(nameof(PercentileCases))]
    public void ExplainUsesInclusiveLinearlyInterpolated95thPercentile(double currentFrp, string expectedOutcome)
    {
        NotificationCluster current = Cluster(Anomaly(id: "current", Now, longitude: 30, currentFrp));

        FirmsHistory history = History(
            isReady: true,
            Anomaly(id: "historical-10", Now.AddDays(days: -1), longitude: 30, frp: 10),
            Anomaly(id: "historical-20", Now.AddDays(days: -2), longitude: 30, frp: 20),
            Anomaly(id: "historical-100", Now.AddDays(days: -3), longitude: 30, frp: 100)
        );

        NotificationCriterionResult result = NotificationHistoricalFrpPolicy.Explain(current, history, Options());

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Contains(expectedSubstring: "92 MW historical 95th percentile", result.ActualValue, StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "thresholds 138 MW (p95 x 1.5) and 167 MW (p95 + 75 MW)", result.ActualValue, StringComparison.Ordinal);
    }

    /// <summary>Uses retained cluster totals for days without overlapping current observations.</summary>
    [Fact]
    public void ExplainUsesRetainedClusterTotalsForNonOverlappingHistoricalDays()
    {
        NotificationCluster current = Cluster(Anomaly(id: "current", Now, longitude: 30, frp: 101));

        Anomaly historical = Anomaly(id: "historical", Now.AddDays(days: -1), longitude: 30, frp: 100);

        FirmsHistory history = History(isReady: true, anomalies: [historical]);

        Assert.True(history.Days.Length == 1);
        FirmsHistoryDay retainedDay = history.Days[index: 0];
        Assert.True(retainedDay.Clusters.Length == 1);

        NotificationClusterSummary retainedSummary = retainedDay.Clusters[index: 0] with
        {
            TotalFrpMegawatts = 200
        };

        history = history with
        {
            Days =
            [
                retainedDay with
                {
                    Clusters = [retainedSummary]
                }
            ]
        };

        NotificationCriterionResult result = NotificationHistoricalFrpPolicy.Explain(current, history, Options());

        Assert.Equal(NotificationCriterionOutcomes.Failed, result.Outcome);
        Assert.Contains(expectedSubstring: "200 MW historical 95th percentile", result.ActualValue, StringComparison.Ordinal);
    }

    private static Anomaly Anomaly(string id, DateTimeOffset acquiredAtUtc, double longitude, double? frp)
    {
        return new(
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
            new Uri($"https://example.test/{id}")
        );
    }

    private static NotificationCluster Cluster(params Anomaly[] anomalies)
    {
        return Assert.Single(NotificationClustering.Create(anomalies, radiusKilometers: 5, TimeSpan.FromMinutes(minutes: 90)).ToArray());
    }

    private static FirmsHistory History(bool isReady, params Anomaly[] anomalies)
    {
        NotificationOptions options = Options();
        var today = DateOnly.FromDateTime(Now.UtcDateTime);

        FirmsHistoryDay[] days =
        [
            .. anomalies
                .GroupBy(anomaly => DateOnly.FromDateTime(anomaly.AcquiredAtUtc.UtcDateTime))
                .Select(
                    group =>
                {
                    Anomaly[] dayAnomalies = [.. group];

                    NotificationClusterSummary[] clusters =
                    [
                        .. NotificationClustering.Create(dayAnomalies, options.ClusterRadiusKilometers, options.ClusterTimeWindow)
                            .Select(NotificationClusterSummary.FromCluster)
                    ];

                    return new FirmsHistoryDay(group.Key, IsComplete: true, IsPartiallyStale: false, [], dayAnomalies.Length, [..dayAnomalies], clusters.Length, [..clusters]);
                }
                )
        ];

        return new(
            Now,
            FirmsHistoryStore.CompletedDayCount,
            today.AddDays(-FirmsHistoryStore.CompletedDayCount),
            today,
            today.AddDays(-FirmsHistoryStore.CompletedDayCount),
            today,
            isReady,
            !isReady,
            [.. days]
        );
    }

    private static NotificationOptions Options()
    {
        return ApplicationConfiguration.ParseNotificationOptions(name => null) with
        {
            ClusterRadiusKilometers = 5,
            ClusterTimeWindow = TimeSpan.FromMinutes(minutes: 90)
        };
    }
}
