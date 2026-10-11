using ThermalWatch.Core;

namespace ThermalWatch.Tests.Core.Notifications;

/// <summary>Verifies incident suppression, continuation, and expiration.</summary>
public sealed class NotificationEpisodeHistoryTests
{
    private const double RadiusKilometers = 5;
    private static readonly DateTimeOffset ObservedAt = new(year: 2026, month: 7, day: 23, hour: 8, minute: 0, second: 0, TimeSpan.Zero);
    private static readonly TimeSpan TimeWindow = TimeSpan.FromMinutes(minutes: 90);
    private static readonly TimeSpan Retention = TimeSpan.FromHours(hours: 48);

    /// <summary>Allows an observation outside every tracked incident's radius.</summary>
    [Fact]
    public void AllowsNewEpisodeOutsideRadius()
    {
        NotificationEpisodeHistory history = CreateHistory();

        NotificationCluster delivered = Cluster(CreateAnomaly(id: "delivered", ObservedAt, latitude: 0, longitude: 0));

        NotificationCluster distant = Cluster(CreateAnomaly(id: "distant", ObservedAt.AddMinutes(minutes: 5), latitude: 0, longitude: 0.05));

        history.RecordIncident(delivered, ObservedAt);

        bool suppressed = history.TrySuppressAndExtend(distant, ObservedAt.AddMinutes(minutes: 5));

        Assert.False(suppressed);
    }

    /// <summary>Allows an observation outside the tracked incident's time window.</summary>
    [Fact]
    public void AllowsNewEpisodeOutsideTimeWindow()
    {
        NotificationEpisodeHistory history = CreateHistory();

        NotificationCluster delivered = Cluster(CreateAnomaly(id: "delivered", ObservedAt, latitude: 50, longitude: 30));

        NotificationCluster later = Cluster(CreateAnomaly(id: "later", ObservedAt.AddMinutes(minutes: 91), latitude: 50, longitude: 30));

        history.RecordIncident(delivered, ObservedAt);

        bool suppressed = history.TrySuppressAndExtend(later, ObservedAt.AddMinutes(minutes: 91));

        Assert.False(suppressed);
    }

    /// <summary>Allows related observations after retained incident history expires.</summary>
    [Fact]
    public void AllowsRelatedDetectionAfterHistoryExpires()
    {
        NotificationEpisodeHistory history = CreateHistory();

        NotificationCluster delivered = Cluster(CreateAnomaly(id: "delivered", ObservedAt, latitude: 50, longitude: 30));

        NotificationCluster related = Cluster(CreateAnomaly(id: "related", ObservedAt.AddMinutes(minutes: 5), latitude: 50, longitude: 30));

        history.RecordIncident(delivered, ObservedAt);

        bool suppressed = history.TrySuppressAndExtend(related, ObservedAt.Add(Retention).AddMinutes(minutes: 1));

        Assert.False(suppressed);
    }

    /// <summary>Does not suppress a cluster that has never been tracked.</summary>
    [Fact]
    public void DoesNotSuppressUndeliveredCluster()
    {
        NotificationEpisodeHistory history = CreateHistory();

        NotificationCluster cluster = Cluster(CreateAnomaly(id: "undelivered", ObservedAt, latitude: 50, longitude: 30));

        bool suppressed = history.TrySuppressAndExtend(cluster, ObservedAt.AddMinutes(minutes: 5));

        Assert.False(suppressed);
    }

    /// <summary>Extends suppression through spatial and temporal bridges.</summary>
    [Fact]
    public void ExtendsTrackedEpisodeTransitively()
    {
        NotificationEpisodeHistory history = CreateHistory();

        NotificationCluster first = Cluster(CreateAnomaly(id: "first", ObservedAt, latitude: 0, longitude: 0));

        NotificationCluster bridge = Cluster(CreateAnomaly(id: "bridge", ObservedAt.AddMinutes(minutes: 60), latitude: 0, longitude: 0.04));

        NotificationCluster continuation = Cluster(CreateAnomaly(id: "continuation", ObservedAt.AddMinutes(minutes: 120), latitude: 0, longitude: 0.08));

        history.RecordIncident(first, ObservedAt);

        bool bridgeSuppressed = history.TrySuppressAndExtend(bridge, ObservedAt.AddMinutes(minutes: 60));

        bool continuationSuppressed = history.TrySuppressAndExtend(continuation, ObservedAt.AddMinutes(minutes: 120));

        Assert.True(bridgeSuppressed);
        Assert.True(continuationSuppressed);
        Assert.True(continuation.Representative.AcquiredAtUtc - first.Representative.AcquiredAtUtc > TimeWindow);
    }

    /// <summary>Suppresses another satellite's observation of a tracked incident.</summary>
    [Fact]
    public void SuppressesDifferentSatelliteInTrackedEpisode()
    {
        NotificationEpisodeHistory history = CreateHistory();

        NotificationCluster delivered = Cluster(CreateAnomaly(id: "noaa20", ObservedAt, latitude: 57.94608, longitude: 60.06142, source: "VIIRS_NOAA20_NRT", satellite: "N20"));

        NotificationCluster laterSatellite = Cluster(
            CreateAnomaly(id: "snpp", ObservedAt.AddMinutes(minutes: 53), latitude: 57.947, longitude: 60.062, source: "VIIRS_SNPP_NRT", satellite: "N")
        );

        history.RecordIncident(delivered, ObservedAt.AddHours(hours: 1));

        bool suppressed = history.TrySuppressAndExtend(laterSatellite, ObservedAt.AddHours(hours: 2));

        Assert.True(suppressed);
    }

    private static NotificationCluster Cluster(params Anomaly[] anomalies)
    {
        return Assert.Single(NotificationClustering.Create(anomalies, RadiusKilometers, TimeWindow).ToArray());
    }

    private static Anomaly CreateAnomaly(
        string id,
        DateTimeOffset acquiredAtUtc,
        double latitude,
        double longitude,
        double frpMegawatts = 100,
        string source = "VIIRS_SNPP_NRT",
        string satellite = "N"
    )
    {
        return new(
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
            new Uri($"https://example.test/{id}")
        );
    }

    private static NotificationEpisodeHistory CreateHistory()
    {
        return new(RadiusKilometers, TimeWindow, Retention);
    }
}
