using Microsoft.Extensions.Time.Testing;
using ThermalWatch.Api;
using ThermalWatch.Core;

namespace ThermalWatch.Tests;

public sealed class FirmsHistoryStoreTests
{
    private static readonly DateTimeOffset s_now = new(
        year: 2026,
        month: 7,
        day: 27,
        hour: 12,
        minute: 0,
        second: 0,
        TimeSpan.Zero);

    [Fact]
    public void CurrentStartsWithThirtyCompletedDatesAndLiveTodayIncomplete()
    {
        FirmsHistoryStore store = CreateStore(new FakeTimeProvider(s_now));

        FirmsHistory history = store.Current;

        Assert.Equal(FirmsHistoryStore.RetainedDayCount, history.Days.Length);
        Assert.Equal(new DateOnly(year: 2026, month: 6, day: 27), history.RetainedFromDate);
        Assert.Equal(new DateOnly(year: 2026, month: 7, day: 27), history.RetainedThroughDate);
        Assert.False(history.IsReady);
        Assert.True(history.IsPartiallyStale);
        Assert.All(history.Days, day => Assert.False(day.IsComplete));
    }

    [Fact]
    public void PublishBuildsRawMultiSatelliteClusterAndReplacesCompleteSlices()
    {
        var timeProvider = new FakeTimeProvider(s_now);
        FirmsHistoryStore store = CreateStore(timeProvider);
        var date = new DateOnly(year: 2026, month: 7, day: 26);
        SegmentRefreshResult[] initial =
        [
            Success(source: "MODIS_NRT", anomalies: [
                Anomaly(id: "terra", source: "MODIS_NRT", satellite: "Terra", date: date, frp: 40),
                Anomaly(id: "aqua", source: "MODIS_NRT", satellite: "Aqua", date: date, frp: 50)
            ]),
            Success(source: "VIIRS_SNPP_NRT", anomalies: [Anomaly(id: "snpp", source: "VIIRS_SNPP_NRT", satellite: "Suomi-NPP", date: date, frp: 60)]),
            Success(source: "VIIRS_NOAA20_NRT", anomalies: [Anomaly(id: "noaa20", source: "VIIRS_NOAA20_NRT", satellite: "NOAA-20", date: date, frp: 70)]),
            Success(source: "VIIRS_NOAA21_NRT", anomalies: [Anomaly(id: "noaa21", source: "VIIRS_NOAA21_NRT", satellite: "NOAA-21", date: date, frp: 80)])
        ];

        FirmsHistoryDay day = Assert.Single(
            store.Publish(startDate: date, dayCount: 1, results: initial).Days,
            candidate => candidate.Date == date);

        Assert.True(day.IsComplete);
        Assert.Equal(5, day.AnomalyCount);
        NotificationClusterSummary cluster = Assert.Single(day.Clusters);
        Assert.Equal(5, cluster.DetectionCount);
        Assert.Equal(300, cluster.TotalFrpMegawatts);
        Assert.Equal(["aqua", "noaa20", "noaa21", "snpp", "terra"], cluster.MemberIds.Order(StringComparer.Ordinal), StringComparer.Ordinal);

        store.Publish(
            startDate: date,
            dayCount: 1,
            results: [Success(source: "MODIS_NRT", anomalies: [])]);
        day = Assert.Single(store.Current.Days, candidate => candidate.Date == date);
        Assert.Equal(3, day.AnomalyCount);
        Assert.DoesNotContain(day.Anomalies, anomaly => anomaly.Source.Equals(value: "MODIS_NRT", StringComparison.Ordinal));

        store.Publish(
            startDate: date,
            dayCount: 1,
            results: [Failure(source: "VIIRS_SNPP_NRT")]);
        day = Assert.Single(store.Current.Days, candidate => candidate.Date == date);
        Assert.Equal(3, day.AnomalyCount);
        Assert.False(day.IsComplete);
        Assert.True(day.IsPartiallyStale);
        Assert.Contains(day.Anomalies, anomaly => anomaly.Id.Equals(value: "snpp", StringComparison.Ordinal));
    }

    [Fact]
    public void CompletePriorSlicesMakeHistoryReadyAndUtcRolloverRotatesRetention()
    {
        var timeProvider = new FakeTimeProvider(s_now);
        FirmsHistoryStore store = CreateStore(timeProvider);
        DateOnly startDate = DateOnly.FromDateTime(s_now.UtcDateTime).AddDays(-FirmsHistoryStore.CompletedDayCount);
        SegmentRefreshResult[] results = [.. FirmsSources.All.Select(source => Success(source, []))];

        FirmsHistory history = store.Publish(
            startDate,
            FirmsHistoryStore.CompletedDayCount,
            results);

        Assert.True(history.IsReady);
        Assert.False(history.Days[^1].IsComplete);

        timeProvider.Advance(TimeSpan.FromDays(days: 1));
        history = store.Current;

        Assert.Equal(startDate.AddDays(value: 1), history.RetainedFromDate);
        Assert.Equal(new DateOnly(year: 2026, month: 7, day: 28), history.RetainedThroughDate);
        Assert.False(history.IsReady);
        Assert.DoesNotContain(history.Days, day => day.Date == startDate);
    }

    private static FirmsHistoryStore CreateStore(TimeProvider timeProvider)
    {
        FirmsOptions options = Options();
        return new(
            options,
            ApplicationConfiguration.ParseNotificationOptions(_ => null),
            timeProvider);
    }

    private static FirmsOptions Options() =>
        new(
            MapKey: new string('A', count: 32),
            CountryCodes: ["UKR"],
            PollInterval: TimeSpan.FromMinutes(minutes: 5),
            ActiveWindow: TimeSpan.FromHours(hours: 24),
            RequestTimeout: TimeSpan.FromSeconds(seconds: 45),
            MaxConcurrency: 4);

    private static SegmentRefreshResult Success(string source, IEnumerable<Anomaly> anomalies) =>
        SegmentRefreshResult.Success(
            new(CountryCode: "UKR", source),
            s_now,
            s_now,
            [.. anomalies],
            IngestionModes.Country);

    private static SegmentRefreshResult Failure(string source) =>
        SegmentRefreshResult.Failure(
            new(CountryCode: "UKR", source),
            s_now,
            s_now,
            error: "FIRMS is unavailable.");

    private static Anomaly Anomaly(
        string id,
        string source,
        string satellite,
        DateOnly date,
        double? frp) =>
        new(
            id,
            CountryCode: "UKR",
            source,
            satellite,
            Instrument: source.Equals(value: "MODIS_NRT", StringComparison.Ordinal) ? "MODIS" : "VIIRS",
            Latitude: 50,
            Longitude: 30,
            new DateTimeOffset(
                date.ToDateTime(new(hour: 12, minute: 0), DateTimeKind.Unspecified),
                TimeSpan.Zero),
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
