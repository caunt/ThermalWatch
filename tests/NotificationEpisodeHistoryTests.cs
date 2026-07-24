using ThermalWatch.Core;

namespace ThermalWatch.Tests;

public sealed class NotificationEpisodeHistoryTests
{
    private const double RadiusKilometers = 5;
    private static readonly DateTimeOffset s_observedAt = new(
        year: 2026,
        month: 7,
        day: 23,
        hour: 8,
        minute: 0,
        second: 0,
        TimeSpan.Zero);
    private static readonly TimeSpan s_timeWindow = TimeSpan.FromMinutes(minutes: 90);
    private static readonly TimeSpan s_retention = TimeSpan.FromHours(hours: 48);

    [Fact]
    public void SuppressesDifferentSatelliteInTrackedEpisode()
    {
        NotificationEpisodeHistory history = CreateHistory();
        NotificationCluster delivered = Cluster(CreateAnomaly(
            id: "noaa20",
            s_observedAt,
            latitude: 57.94608,
            longitude: 60.06142,
            source: "VIIRS_NOAA20_NRT",
            satellite: "N20"));
        NotificationCluster laterSatellite = Cluster(CreateAnomaly(
            id: "snpp",
            s_observedAt.AddMinutes(minutes: 53),
            latitude: 57.947,
            longitude: 60.062,
            source: "VIIRS_SNPP_NRT",
            satellite: "N"));
        history.RecordIncident(delivered, s_observedAt.AddHours(hours: 1));

        bool suppressed = history.TrySuppressAndExtend(
            laterSatellite,
            s_observedAt.AddHours(hours: 2));

        Assert.True(suppressed);
    }

    [Fact]
    public void ExtendsTrackedEpisodeTransitively()
    {
        NotificationEpisodeHistory history = CreateHistory();
        NotificationCluster first = Cluster(CreateAnomaly(
            id: "first",
            s_observedAt,
            latitude: 0,
            longitude: 0));
        NotificationCluster bridge = Cluster(CreateAnomaly(
            id: "bridge",
            s_observedAt.AddMinutes(minutes: 60),
            latitude: 0,
            longitude: 0.04));
        NotificationCluster continuation = Cluster(CreateAnomaly(
            id: "continuation",
            s_observedAt.AddMinutes(minutes: 120),
            latitude: 0,
            longitude: 0.08));
        history.RecordIncident(first, s_observedAt);

        bool bridgeSuppressed = history.TrySuppressAndExtend(
            bridge,
            s_observedAt.AddMinutes(minutes: 60));
        bool continuationSuppressed = history.TrySuppressAndExtend(
            continuation,
            s_observedAt.AddMinutes(minutes: 120));

        Assert.True(bridgeSuppressed);
        Assert.True(continuationSuppressed);
        Assert.False(NotificationClustering.AreRelated(
            first.Representative,
            continuation.Representative,
            RadiusKilometers,
            s_timeWindow));
    }

    [Fact]
    public void AllowsNewEpisodeOutsideTimeWindow()
    {
        NotificationEpisodeHistory history = CreateHistory();
        NotificationCluster delivered = Cluster(CreateAnomaly(
            id: "delivered",
            s_observedAt,
            latitude: 50,
            longitude: 30));
        NotificationCluster later = Cluster(CreateAnomaly(
            id: "later",
            s_observedAt.AddMinutes(minutes: 91),
            latitude: 50,
            longitude: 30));
        history.RecordIncident(delivered, s_observedAt);

        bool suppressed = history.TrySuppressAndExtend(
            later,
            s_observedAt.AddMinutes(minutes: 91));

        Assert.False(suppressed);
    }

    [Fact]
    public void AllowsNewEpisodeOutsideRadius()
    {
        NotificationEpisodeHistory history = CreateHistory();
        NotificationCluster delivered = Cluster(CreateAnomaly(
            id: "delivered",
            s_observedAt,
            latitude: 0,
            longitude: 0));
        NotificationCluster distant = Cluster(CreateAnomaly(
            id: "distant",
            s_observedAt.AddMinutes(minutes: 5),
            latitude: 0,
            longitude: 0.05));
        history.RecordIncident(delivered, s_observedAt);

        bool suppressed = history.TrySuppressAndExtend(
            distant,
            s_observedAt.AddMinutes(minutes: 5));

        Assert.False(suppressed);
    }

    [Fact]
    public void AllowsRelatedDetectionAfterHistoryExpires()
    {
        NotificationEpisodeHistory history = CreateHistory();
        NotificationCluster delivered = Cluster(CreateAnomaly(
            id: "delivered",
            s_observedAt,
            latitude: 50,
            longitude: 30));
        NotificationCluster related = Cluster(CreateAnomaly(
            id: "related",
            s_observedAt.AddMinutes(minutes: 5),
            latitude: 50,
            longitude: 30));
        history.RecordIncident(delivered, s_observedAt);

        bool suppressed = history.TrySuppressAndExtend(
            related,
            s_observedAt.Add(s_retention).AddMinutes(minutes: 1));

        Assert.False(suppressed);
    }

    [Fact]
    public void DoesNotSuppressUndeliveredCluster()
    {
        NotificationEpisodeHistory history = CreateHistory();
        NotificationCluster cluster = Cluster(CreateAnomaly(
            id: "undelivered",
            s_observedAt,
            latitude: 50,
            longitude: 30));

        bool suppressed = history.TrySuppressAndExtend(
            cluster,
            s_observedAt.AddMinutes(minutes: 5));

        Assert.False(suppressed);
    }

    private static NotificationEpisodeHistory CreateHistory() =>
        new(RadiusKilometers, s_timeWindow, s_retention);

    private static NotificationCluster Cluster(params Anomaly[] anomalies) =>
        Assert.Single(NotificationClustering.Create(
            anomalies,
            RadiusKilometers,
            s_timeWindow));

    private static Anomaly CreateAnomaly(
        string id,
        DateTimeOffset acquiredAtUtc,
        double latitude,
        double longitude,
        double frpMegawatts = 100,
        string source = "VIIRS_SNPP_NRT",
        string satellite = "N") =>
        new(
            id,
            CountryCode: "RUS",
            source,
            satellite,
            Instrument: "VIIRS",
            latitude,
            longitude,
            acquiredAtUtc,
            DayNight: "D",
            BrightnessKelvin: 350,
            SecondaryBrightnessKelvin: 300,
            frpMegawatts,
            ScanKilometers: 0.4,
            TrackKilometers: 0.4,
            ConfidenceRaw: "n",
            ConfidencePercent: null,
            ConfidenceCategory: "nominal",
            Version: "2.0NRT",
            GoogleMapsUrl: $"https://example.test/{id}");
}
