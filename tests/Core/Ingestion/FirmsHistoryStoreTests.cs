using Microsoft.Extensions.Time.Testing;

using ThermalWatch.Api;
using ThermalWatch.Core;

namespace ThermalWatch.Tests.Core.Ingestion;

/// <summary>Verifies retained historical slices, replacement, and UTC rotation.</summary>
public sealed class FirmsHistoryStoreTests
{
    private static readonly DateTimeOffset Now = new(year: 2026, month: 7, day: 27, hour: 12, minute: 0, second: 0, TimeSpan.Zero);

    /// <summary>Rotates ready historical retention when the UTC date changes.</summary>
    [Fact]
    public void CompletePriorSlicesMakeHistoryReadyAndUtcRolloverRotatesRetention()
    {
        var timeProvider = new FakeTimeProvider(Now);
        FirmsHistoryStore store = CreateStore(timeProvider);
        DateOnly startDate = DateOnly.FromDateTime(Now.UtcDateTime).AddDays(-FirmsHistoryStore.CompletedDayCount);
        SegmentRefreshResult[] results = [.. FirmsSources.All.Select(source => Success(source, []))];

        FirmsHistory history = store.Publish(startDate, FirmsHistoryStore.CompletedDayCount, results);

        Assert.True(history.IsReady);
        Assert.False(history.Days[history.Days.Length - 1].IsComplete);

        timeProvider.Advance(TimeSpan.FromDays(days: 1));
        history = store.Current;

        Assert.Equal(startDate.AddDays(value: 1), history.RetainedFromDate);
        Assert.Equal(new DateOnly(year: 2026, month: 7, day: 28), history.RetainedThroughDate);
        Assert.False(history.IsReady);
        Assert.False(history.Days.Any(day => day.Date == startDate));
    }

    /// <summary>Starts with incomplete completed-day history and a live current day.</summary>
    [Fact]
    public void CurrentStartsWithThirtyCompletedDatesAndLiveTodayIncomplete()
    {
        FirmsHistoryStore store = CreateStore(new FakeTimeProvider(Now));

        FirmsHistory history = store.Current;

        Assert.Equal(FirmsHistoryStore.RetainedDayCount, history.Days.Length);
        Assert.Equal(new DateOnly(year: 2026, month: 6, day: 27), history.RetainedFromDate);
        Assert.Equal(new DateOnly(year: 2026, month: 7, day: 27), history.RetainedThroughDate);
        Assert.False(history.IsReady);
        Assert.True(history.IsPartiallyStale);

        foreach (FirmsHistoryDay day in history.Days)
            Assert.False(day.IsComplete);
    }

    /// <summary>Replaces successful source slices and retains stale slices after failure.</summary>
    [Fact]
    public void PublishBuildsRawMultiSatelliteClusterAndReplacesCompleteSlices()
    {
        var timeProvider = new FakeTimeProvider(Now);
        FirmsHistoryStore store = CreateStore(timeProvider);
        var date = new DateOnly(year: 2026, month: 7, day: 26);

        SegmentRefreshResult[] initial =
        [
            Success(
                source: "MODIS_NRT",
                [
                Anomaly(id: "terra", source: "MODIS_NRT", satellite: "Terra", date, frpMegawatts: 40),
                Anomaly(id: "aqua", source: "MODIS_NRT", satellite: "Aqua", date, frpMegawatts: 50)
            ]
            ),
            Success(source: "VIIRS_SNPP_NRT", [Anomaly(id: "snpp", source: "VIIRS_SNPP_NRT", satellite: "Suomi-NPP", date, frpMegawatts: 60)]),
            Success(source: "VIIRS_NOAA20_NRT", [Anomaly(id: "noaa20", source: "VIIRS_NOAA20_NRT", satellite: "NOAA-20", date, frpMegawatts: 70)]),
            Success(source: "VIIRS_NOAA21_NRT", [Anomaly(id: "noaa21", source: "VIIRS_NOAA21_NRT", satellite: "NOAA-21", date, frpMegawatts: 80)])
        ];

        FirmsHistoryDay day = Assert.Single(store.Publish(date, dayCount: 1, initial).Days.ToArray(), candidate => candidate.Date == date);

        Assert.True(day.IsComplete);
        Assert.Equal(expected: 5, day.AnomalyCount);
        NotificationClusterSummary cluster = Assert.Single(day.Clusters.ToArray());
        Assert.Equal(expected: 5, cluster.DetectionCount);
        Assert.Equal(expected: 300, cluster.TotalFrpMegawatts);
        Assert.Equal(["aqua", "noaa20", "noaa21", "snpp", "terra"], cluster.MemberIds.ToArray().Order(StringComparer.Ordinal), StringComparer.Ordinal);

        FirmsHistory replaced = store.Publish(date, dayCount: 1, [Success(source: "MODIS_NRT", [])]);
        Assert.Same(replaced, store.Current);
        day = Assert.Single(replaced.Days.ToArray(), candidate => candidate.Date == date);
        Assert.Equal(expected: 3, day.AnomalyCount);
        Assert.False(day.Anomalies.Any(anomaly => anomaly.Source.Equals(value: "MODIS_NRT", StringComparison.Ordinal)));

        FirmsHistory stale = store.Publish(date, dayCount: 1, [Failure(source: "VIIRS_SNPP_NRT")]);
        Assert.Same(stale, store.Current);
        day = Assert.Single(stale.Days.ToArray(), candidate => candidate.Date == date);
        Assert.Equal(expected: 3, day.AnomalyCount);
        Assert.False(day.IsComplete);
        Assert.True(day.IsPartiallyStale);
        Assert.True(day.Anomalies.Any(anomaly => anomaly.Id.Equals(value: "snpp", StringComparison.Ordinal)));
    }

    private static Anomaly Anomaly(string id, string source, string satellite, DateOnly date, double? frpMegawatts)
    {
        return new(
            id,
            CountryCode: "UKR",
            source,
            satellite,
            source.Equals(value: "MODIS_NRT", StringComparison.Ordinal) ? "MODIS" : "VIIRS",
            Latitude: 50,
            Longitude: 30,
            new DateTimeOffset(date.ToDateTime(new(hour: 12, minute: 0), DateTimeKind.Unspecified), TimeSpan.Zero),
            DayNight: "D",
            BrightnessKelvin: 330,
            SecondaryBrightnessKelvin: 300,
            frpMegawatts,
            ScanKilometers: 0.4,
            TrackKilometers: 0.4,
            ConfidenceRaw: "n",
            ConfidencePercent: null,
            ConfidenceCategory: "nominal",
            Version: "2.0NRT",
            new Uri($"https://example.test/{id}")
        );
    }

    private static FirmsHistoryStore CreateStore(TimeProvider timeProvider)
    {
        FirmsOptions options = Options();

        return new(options, ApplicationConfiguration.ParseNotificationOptions(name => null), timeProvider);
    }

    private static SegmentRefreshResult Failure(string source)
    {
        return SegmentRefreshResult.Failure(new(CountryCode: "UKR", source), Now, Now, error: "FIRMS is unavailable.");
    }

    private static FirmsOptions Options()
    {
        return new(
            new string(c: 'A', count: 32),
            ["UKR"],
            TimeSpan.FromMinutes(minutes: 5),
            TimeSpan.FromHours(hours: 24),
            TimeSpan.FromSeconds(seconds: 45),
            MaximumConcurrency: 4
        );
    }

    private static SegmentRefreshResult Success(string source, IEnumerable<Anomaly> anomalies)
    {
        return SegmentRefreshResult.Success(new(CountryCode: "UKR", source), Now, Now, [.. anomalies], IngestionModes.Country);
    }
}
