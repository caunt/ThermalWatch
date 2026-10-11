using System.Collections.Immutable;

using ThermalWatch.Core;

namespace ThermalWatch.Tests.Core.Notifications;

/// <summary>Verifies spatial and temporal connected components and cluster measurements.</summary>
public sealed class NotificationClusteringTests
{
    private static readonly DateTimeOffset ObservedAt = new(year: 2026, month: 7, day: 18, hour: 12, minute: 0, second: 0, TimeSpan.Zero);

    /// <summary>Reports unavailable total FRP when every member lacks a value.</summary>
    [Fact]
    public void ClusterTotalFrpIsUnavailableWithoutAnAvailableMemberValue()
    {
        NotificationCluster cluster = Assert.Single(
            NotificationClustering.Create<Anomaly[]>(
                [CreateAnomaly(id: "missing", ObservedAt, latitude: 50, longitude: 30, frpMegawatts: null)],
                radiusKilometers: 5,
                TimeSpan.FromMinutes(minutes: 90)
            ).ToArray()
        );

        Assert.False(cluster.TotalFrpMegawatts.HasValue);
    }

    /// <summary>Sums finite FRP values while ignoring missing and nonfinite measurements.</summary>
    [Fact]
    public void ClusterTotalFrpSumsEveryAvailableMemberValue()
    {
        Anomaly first = CreateAnomaly(id: "first", ObservedAt, latitude: 50, longitude: 30, frpMegawatts: 20);

        Anomaly missing = CreateAnomaly(id: "missing", ObservedAt.AddMinutes(minutes: 1), latitude: 50.01, longitude: 30.01, frpMegawatts: null);

        Anomaly last = CreateAnomaly(id: "last", ObservedAt.AddMinutes(minutes: 2), latitude: 50.02, longitude: 30.02, frpMegawatts: 30);

        Anomaly nonFinite = CreateAnomaly(id: "non-finite", ObservedAt.AddMinutes(minutes: 3), latitude: 50.03, longitude: 30.03, double.PositiveInfinity);

        NotificationCluster cluster = Assert.Single(
            NotificationClustering.Create(new Anomaly[] { first, missing, last, nonFinite }, radiusKilometers: 5, TimeSpan.FromMinutes(minutes: 90)).ToArray()
        );

        Assert.Equal(expected: 50, cluster.TotalFrpMegawatts);
    }

    /// <summary>Keeps the strongest member as representative regardless of its age.</summary>
    [Fact]
    public void CreateAllowsEarlierDetectionToRemainRepresentative()
    {
        Anomaly earlier = CreateAnomaly(id: "earlier", ObservedAt, latitude: 50, longitude: 30, frpMegawatts: 200);

        Anomaly later = CreateAnomaly(id: "later", ObservedAt.AddMinutes(minutes: 5), latitude: 50.01, longitude: 30.01, frpMegawatts: 100);

        NotificationCluster cluster = Assert.Single(
            NotificationClustering.Create(new Anomaly[] { earlier, later }, radiusKilometers: 5, TimeSpan.FromMinutes(minutes: 90)).ToArray()
        );

        Assert.Equal(earlier.Id, cluster.Representative.Id);
    }

    /// <summary>Retains every related active member in the connected component.</summary>
    [Fact]
    public void CreateIncludesEveryRelatedActiveDetection()
    {
        Anomaly earlier = CreateAnomaly(id: "earlier", ObservedAt, latitude: 50, longitude: 30);

        Anomaly later = CreateAnomaly(id: "later", ObservedAt.AddMinutes(minutes: 5), latitude: 50.01, longitude: 30.01);

        NotificationCluster cluster = Assert.Single(
            NotificationClustering.Create(new Anomaly[] { earlier, later }, radiusKilometers: 5, TimeSpan.FromMinutes(minutes: 90)).ToArray()
        );

        Assert.Equal(expected: 2, cluster.Members.Length);
        Assert.True(cluster.Members.Any(member => member.Id.Equals(earlier.Id, StringComparison.Ordinal)));
        Assert.True(cluster.Members.Any(member => member.Id.Equals(later.Id, StringComparison.Ordinal)));
    }

    /// <summary>Separates nearby members whose timestamps exceed the allowed interval.</summary>
    [Fact]
    public void CreateKeepsNearbyDetectionsOutsideTimeWindowSeparate()
    {
        Anomaly first = CreateAnomaly(id: "first", ObservedAt, latitude: 50, longitude: 30);

        Anomaly second = CreateAnomaly(id: "second", ObservedAt.AddMinutes(minutes: 91), latitude: 50.01, longitude: 30.01);

        ImmutableArray<NotificationCluster> clusters = NotificationClustering.Create(new Anomaly[] { first, second }, radiusKilometers: 5, TimeSpan.FromMinutes(minutes: 90));

        Assert.Equal(expected: 2, clusters.Length);
    }

    /// <summary>Connects adjacent observations across the longitude discontinuity.</summary>
    [Fact]
    public void CreateLinksNearbyDetectionsAcrossAntimeridian()
    {
        Anomaly west = CreateAnomaly(id: "west", ObservedAt, latitude: 70, longitude: -179.99);

        Anomaly east = CreateAnomaly(id: "east", ObservedAt, latitude: 70, longitude: 179.99);

        NotificationCluster cluster = Assert.Single(NotificationClustering.Create(new Anomaly[] { west, east }, radiusKilometers: 5, TimeSpan.FromMinutes(minutes: 90)).ToArray());

        Assert.Equal(expected: 2, cluster.Members.Length);
    }

    /// <summary>Preserves transitive connections when unordered endpoints exceed the time window.</summary>
    [Fact]
    public void CreatePreservesTransitiveLinkageForUnorderedInputAcrossTimeWindowBoundary()
    {
        Anomaly first = CreateAnomaly(id: "first", ObservedAt, latitude: 50, longitude: 30);

        Anomaly bridge = CreateAnomaly(id: "bridge", ObservedAt.AddMinutes(minutes: 60), latitude: 50, longitude: 30);

        Anomaly last = CreateAnomaly(id: "last", ObservedAt.AddMinutes(minutes: 120), latitude: 50, longitude: 30);

        NotificationCluster cluster = Assert.Single(
            NotificationClustering.Create(new Anomaly[] { last, first, bridge }, radiusKilometers: 5, TimeSpan.FromMinutes(minutes: 90)).ToArray()
        );

        Assert.Equal(expected: 3, cluster.Members.Length);
        Assert.True(last.AcquiredAtUtc - first.AcquiredAtUtc > TimeSpan.FromMinutes(minutes: 90));
    }

    /// <summary>Returns an empty result for an empty snapshot.</summary>
    [Fact]
    public void CreateReturnsNoClustersWithoutActiveDetections()
    {
        ImmutableArray<NotificationCluster> clusters = NotificationClustering.Create<Anomaly[]>([], radiusKilometers: 5, TimeSpan.FromMinutes(minutes: 90));

        Assert.True(clusters.IsEmpty);
    }

    /// <summary>Produces separate singleton clusters for distant observations.</summary>
    [Fact]
    public void CreateReturnsUnrelatedActiveDetectionsAsSeparateClusters()
    {
        Anomaly first = CreateAnomaly(id: "first", ObservedAt, latitude: 40, longitude: 20);

        Anomaly second = CreateAnomaly(id: "second", ObservedAt.AddMinutes(minutes: 5), latitude: 50, longitude: 30);

        ImmutableArray<NotificationCluster> clusters = NotificationClustering.Create(new Anomaly[] { first, second }, radiusKilometers: 5, TimeSpan.FromMinutes(minutes: 90));

        Assert.Equal(expected: 2, clusters.Length);

        foreach (NotificationCluster cluster in clusters)
            Assert.True(cluster.Members.Length == 1);
    }

    /// <summary>Processes a large distributed snapshot within a bounded test lifetime.</summary>
    [Fact(Timeout = 10_000)]
    public void CreateScalesToLargeSpatiallyDistributedSnapshot()
    {
        Anomaly[] anomalies =
        [
            .. Enumerable.Range(start: 0, count: 17_000).Select(
                index =>
            {
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();

                return CreateAnomaly($"anomaly-{index}", ObservedAt, -80 + (index / 100 * 0.8), -170 + (index % 100 * 3.4));
            }
            )
        ];

        ImmutableArray<NotificationCluster> clusters = NotificationClustering.Create(anomalies, radiusKilometers: 5, TimeSpan.FromMinutes(minutes: 90));

        Assert.Equal(anomalies.Length, clusters.Length);
    }

    /// <summary>Joins distant endpoints through an intervening observation.</summary>
    [Fact]
    public void CreateUsesTransitiveLinkage()
    {
        Anomaly first = CreateAnomaly(id: "first", ObservedAt, latitude: 0, longitude: 0);

        Anomaly bridge = CreateAnomaly(id: "bridge", ObservedAt.AddMinutes(minutes: 5), latitude: 0, longitude: 0.04);

        Anomaly last = CreateAnomaly(id: "last", ObservedAt.AddMinutes(minutes: 10), latitude: 0, longitude: 0.08);

        NotificationCluster cluster = Assert.Single(
            NotificationClustering.Create(new Anomaly[] { first, bridge, last }, radiusKilometers: 5, TimeSpan.FromMinutes(minutes: 90)).ToArray()
        );

        Assert.Equal(expected: 3, cluster.Members.Length);
        Assert.True(Geography.HaversineKilometers(first, last) > 5);
    }

    private static Anomaly CreateAnomaly(string id, DateTimeOffset acquiredAtUtc, double latitude, double longitude, double? frpMegawatts = 100)
    {
        return new(
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
            new Uri($"https://example.test/{id}")
        );
    }
}
