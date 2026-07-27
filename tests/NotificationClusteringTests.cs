using System.Collections.Immutable;
using ThermalWatch.Core;

namespace ThermalWatch.Tests;

public sealed class NotificationClusteringTests
{
    private static readonly DateTimeOffset s_observedAt = new(
        year: 2026,
        month: 7,
        day: 18,
        hour: 12,
        minute: 0,
        second: 0,
        TimeSpan.Zero);

    [Fact]
    public void CreateIncludesEveryRelatedActiveDetection()
    {
        Anomaly earlier = CreateAnomaly(
            id: "earlier",
            s_observedAt,
            latitude: 50,
            longitude: 30);
        Anomaly later = CreateAnomaly(
            id: "later",
            s_observedAt.AddMinutes(minutes: 5),
            latitude: 50.01,
            longitude: 30.01);

        NotificationCluster cluster = Assert.Single(NotificationClustering.Create(
            [earlier, later],
            radiusKilometers: 5,
            timeWindow: TimeSpan.FromMinutes(minutes: 90)));

        Assert.Equal(2, cluster.Members.Length);
        Assert.Contains(cluster.Members, member => member.Id.Equals(earlier.Id, StringComparison.Ordinal));
        Assert.Contains(cluster.Members, member => member.Id.Equals(later.Id, StringComparison.Ordinal));
    }

    [Fact]
    public void CreateAllowsEarlierDetectionToRemainRepresentative()
    {
        Anomaly earlier = CreateAnomaly(
            id: "earlier",
            s_observedAt,
            latitude: 50,
            longitude: 30,
            frpMegawatts: 200);
        Anomaly later = CreateAnomaly(
            id: "later",
            s_observedAt.AddMinutes(minutes: 5),
            latitude: 50.01,
            longitude: 30.01,
            frpMegawatts: 100);

        NotificationCluster cluster = Assert.Single(NotificationClustering.Create(
            [earlier, later],
            radiusKilometers: 5,
            timeWindow: TimeSpan.FromMinutes(minutes: 90)));

        Assert.Equal(earlier.Id, cluster.Representative.Id);
    }

    [Fact]
    public void ClusterTotalFrpSumsEveryAvailableMemberValue()
    {
        Anomaly first = CreateAnomaly(
            id: "first",
            s_observedAt,
            latitude: 50,
            longitude: 30,
            frpMegawatts: 20);
        Anomaly missing = CreateAnomaly(
            id: "missing",
            s_observedAt.AddMinutes(minutes: 1),
            latitude: 50.01,
            longitude: 30.01,
            frpMegawatts: null);
        Anomaly last = CreateAnomaly(
            id: "last",
            s_observedAt.AddMinutes(minutes: 2),
            latitude: 50.02,
            longitude: 30.02,
            frpMegawatts: 30);
        Anomaly nonFinite = CreateAnomaly(
            id: "non-finite",
            s_observedAt.AddMinutes(minutes: 3),
            latitude: 50.03,
            longitude: 30.03,
            frpMegawatts: double.PositiveInfinity);

        NotificationCluster cluster = Assert.Single(NotificationClustering.Create(
            [first, missing, last, nonFinite],
            radiusKilometers: 5,
            timeWindow: TimeSpan.FromMinutes(minutes: 90)));

        Assert.Equal(50, cluster.TotalFrpMegawatts);
    }

    [Fact]
    public void ClusterTotalFrpIsUnavailableWithoutAnAvailableMemberValue()
    {
        NotificationCluster cluster = Assert.Single(NotificationClustering.Create(
            [CreateAnomaly(
                id: "missing",
                s_observedAt,
                latitude: 50,
                longitude: 30,
                frpMegawatts: null)],
            radiusKilometers: 5,
            timeWindow: TimeSpan.FromMinutes(minutes: 90)));

        Assert.Null(cluster.TotalFrpMegawatts);
    }

    [Fact]
    public void CreateReturnsUnrelatedActiveDetectionsAsSeparateClusters()
    {
        Anomaly first = CreateAnomaly(
            id: "first",
            s_observedAt,
            latitude: 40,
            longitude: 20);
        Anomaly second = CreateAnomaly(
            id: "second",
            s_observedAt.AddMinutes(minutes: 5),
            latitude: 50,
            longitude: 30);

        ImmutableArray<NotificationCluster> clusters = NotificationClustering.Create(
            [first, second],
            radiusKilometers: 5,
            timeWindow: TimeSpan.FromMinutes(minutes: 90));

        Assert.Equal(2, clusters.Length);
        Assert.All(clusters, cluster => Assert.Single(cluster.Members));
    }

    [Fact]
    public void CreateKeepsNearbyDetectionsOutsideTimeWindowSeparate()
    {
        Anomaly first = CreateAnomaly(
            id: "first",
            s_observedAt,
            latitude: 50,
            longitude: 30);
        Anomaly second = CreateAnomaly(
            id: "second",
            s_observedAt.AddMinutes(minutes: 91),
            latitude: 50.01,
            longitude: 30.01);

        ImmutableArray<NotificationCluster> clusters = NotificationClustering.Create(
            [first, second],
            radiusKilometers: 5,
            timeWindow: TimeSpan.FromMinutes(minutes: 90));

        Assert.Equal(2, clusters.Length);
    }

    [Fact]
    public void CreateUsesTransitiveLinkage()
    {
        Anomaly first = CreateAnomaly(
            id: "first",
            s_observedAt,
            latitude: 0,
            longitude: 0);
        Anomaly bridge = CreateAnomaly(
            id: "bridge",
            s_observedAt.AddMinutes(minutes: 5),
            latitude: 0,
            longitude: 0.04);
        Anomaly last = CreateAnomaly(
            id: "last",
            s_observedAt.AddMinutes(minutes: 10),
            latitude: 0,
            longitude: 0.08);

        NotificationCluster cluster = Assert.Single(NotificationClustering.Create(
            [first, bridge, last],
            radiusKilometers: 5,
            timeWindow: TimeSpan.FromMinutes(minutes: 90)));

        Assert.Equal(3, cluster.Members.Length);
        Assert.False(NotificationClustering.AreRelated(
            first,
            last,
            radiusKilometers: 5,
            timeWindow: TimeSpan.FromMinutes(minutes: 90)));
    }

    [Fact]
    public void CreatePreservesTransitiveLinkageForUnorderedInputAcrossTimeWindowBoundary()
    {
        Anomaly first = CreateAnomaly(
            id: "first",
            s_observedAt,
            latitude: 50,
            longitude: 30);
        Anomaly bridge = CreateAnomaly(
            id: "bridge",
            s_observedAt.AddMinutes(minutes: 60),
            latitude: 50,
            longitude: 30);
        Anomaly last = CreateAnomaly(
            id: "last",
            s_observedAt.AddMinutes(minutes: 120),
            latitude: 50,
            longitude: 30);

        NotificationCluster cluster = Assert.Single(NotificationClustering.Create(
            [last, first, bridge],
            radiusKilometers: 5,
            timeWindow: TimeSpan.FromMinutes(minutes: 90)));

        Assert.Equal(3, cluster.Members.Length);
        Assert.False(NotificationClustering.AreRelated(
            first,
            last,
            radiusKilometers: 5,
            timeWindow: TimeSpan.FromMinutes(minutes: 90)));
    }

    [Fact]
    public void CreateLinksNearbyDetectionsAcrossAntimeridian()
    {
        Anomaly west = CreateAnomaly(
            id: "west",
            s_observedAt,
            latitude: 70,
            longitude: -179.99);
        Anomaly east = CreateAnomaly(
            id: "east",
            s_observedAt,
            latitude: 70,
            longitude: 179.99);

        NotificationCluster cluster = Assert.Single(NotificationClustering.Create(
            [west, east],
            radiusKilometers: 5,
            timeWindow: TimeSpan.FromMinutes(minutes: 90)));

        Assert.Equal(2, cluster.Members.Length);
    }

    [Fact(Timeout = 10_000)]
    public void CreateScalesToLargeSpatiallyDistributedSnapshot()
    {
        Anomaly[] anomalies =
        [
            .. Enumerable.Range(start: 0, count: 17_000).Select(index => CreateAnomaly(
                id: $"anomaly-{index}",
                s_observedAt,
                latitude: -80 + ((index / 100) * 0.8),
                longitude: -170 + ((index % 100) * 3.4)))
        ];

        ImmutableArray<NotificationCluster> clusters = NotificationClustering.Create(
            anomalies,
            radiusKilometers: 5,
            timeWindow: TimeSpan.FromMinutes(minutes: 90));

        Assert.Equal(anomalies.Length, clusters.Length);
    }

    [Fact]
    public void CreateReturnsNoClustersWithoutActiveDetections()
    {
        ImmutableArray<NotificationCluster> clusters = NotificationClustering.Create(
            [],
            radiusKilometers: 5,
            timeWindow: TimeSpan.FromMinutes(minutes: 90));

        Assert.Empty(clusters);
    }

    private static Anomaly CreateAnomaly(
        string id,
        DateTimeOffset acquiredAtUtc,
        double latitude,
        double longitude,
        double? frpMegawatts = 100) =>
        new(
            id,
            CountryCode: "UKR",
            Source: "VIIRS_SNPP_NRT",
            Satellite: "N",
            Instrument: "VIIRS",
            latitude,
            longitude,
            acquiredAtUtc,
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
            GoogleMapsUrl: $"https://example.test/{id}");
}
