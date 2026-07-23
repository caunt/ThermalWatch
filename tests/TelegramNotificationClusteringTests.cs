using System.Collections.Immutable;
using ThermalWatch.Core;
using ThermalWatch.Telegram;

namespace ThermalWatch.Tests;

public sealed class TelegramNotificationClusteringTests
{
    private static readonly DateTimeOffset s_observedAt = new(year: 2026, month: 7, day: 18, hour: 12, minute: 0, second: 0, TimeSpan.Zero);

    [Fact]
    public void CreateIncludesRelatedDetectionSeenInEarlierSnapshot()
    {
        Anomaly earlier = Detection(id: "earlier", s_observedAt, latitude: 50.000, longitude: 30.000);
        Anomaly newlySeen = Detection(id: "new", s_observedAt.AddMinutes(5), latitude: 50.010, longitude: 30.010);

        ImmutableArray<NotificationCluster> clusters = TelegramNotificationClustering.Create(
            [earlier, newlySeen],
            [newlySeen],
            radiusKilometers: 5,
            timeWindow: TimeSpan.FromMinutes(minutes: 90),
            includeActiveContext: true);

        NotificationCluster cluster = Assert.Single(clusters);
        Assert.Equal(2, cluster.Members.Length);
        Assert.Contains(cluster.Members, member => string.Equals(member.Id, earlier.Id, StringComparison.Ordinal));
        Assert.Contains(cluster.Members, member => string.Equals(member.Id, newlySeen.Id, StringComparison.Ordinal));
    }

    [Fact]
    public void CreateAllowsEarlierContextDetectionToRemainRepresentative()
    {
        Anomaly earlier = Detection(id: "earlier", s_observedAt, latitude: 50.000, longitude: 30.000, frpMegawatts: 200);
        Anomaly newlySeen = Detection(id: "new", s_observedAt.AddMinutes(5), latitude: 50.010, longitude: 30.010, frpMegawatts: 100);

        NotificationCluster cluster = Assert.Single(TelegramNotificationClustering.Create(
            [earlier, newlySeen],
            [newlySeen],
            radiusKilometers: 5,
            timeWindow: TimeSpan.FromMinutes(minutes: 90),
            includeActiveContext: true));

        Assert.Equal(earlier.Id, cluster.Representative.Id);
    }

    [Fact]
    public void CreateDoesNotReprocessUnrelatedActiveDetection()
    {
        Anomaly unrelated = Detection(id: "unrelated", s_observedAt, latitude: 40.000, longitude: 20.000);
        Anomaly newlySeen = Detection(id: "new", s_observedAt.AddMinutes(5), latitude: 50.000, longitude: 30.000);

        NotificationCluster cluster = Assert.Single(TelegramNotificationClustering.Create(
            [unrelated, newlySeen],
            [newlySeen],
            radiusKilometers: 5,
            timeWindow: TimeSpan.FromMinutes(minutes: 90),
            includeActiveContext: true));

        Assert.Single(cluster.Members);
        Assert.Equal(newlySeen.Id, cluster.Members[0].Id);
    }

    [Fact]
    public void CreateDoesNotIncludeContextOutsideTimeWindow()
    {
        Anomaly tooOld = Detection(id: "old", s_observedAt, latitude: 50.000, longitude: 30.000);
        Anomaly newlySeen = Detection(id: "new", s_observedAt.AddMinutes(91), latitude: 50.010, longitude: 30.010);

        NotificationCluster cluster = Assert.Single(TelegramNotificationClustering.Create(
            [tooOld, newlySeen],
            [newlySeen],
            radiusKilometers: 5,
            timeWindow: TimeSpan.FromMinutes(minutes: 90),
            includeActiveContext: true));

        Assert.Single(cluster.Members);
        Assert.Equal(newlySeen.Id, cluster.Members[0].Id);
    }

    [Fact]
    public void CreatePreservesNewDetectionsOnlyBehaviorWhenContextIsDisabled()
    {
        Anomaly earlier = Detection(id: "earlier", s_observedAt, latitude: 50.000, longitude: 30.000);
        Anomaly newlySeen = Detection(id: "new", s_observedAt.AddMinutes(5), latitude: 50.010, longitude: 30.010);

        NotificationCluster cluster = Assert.Single(TelegramNotificationClustering.Create(
            [earlier, newlySeen],
            [newlySeen],
            radiusKilometers: 5,
            timeWindow: TimeSpan.FromMinutes(minutes: 90),
            includeActiveContext: false));

        Assert.Single(cluster.Members);
        Assert.Equal(newlySeen.Id, cluster.Members[0].Id);
    }

    [Fact]
    public void CreateReturnsNoCandidatesWithoutNewDetections()
    {
        Anomaly existing = Detection(id: "existing", s_observedAt, latitude: 50.000, longitude: 30.000);

        ImmutableArray<NotificationCluster> clusters = TelegramNotificationClustering.Create(
            [existing],
            [],
            radiusKilometers: 5,
            timeWindow: TimeSpan.FromMinutes(minutes: 90),
            includeActiveContext: true);

        Assert.Empty(clusters);
    }

    private static Anomaly Detection(
        string id,
        DateTimeOffset acquiredAtUtc,
        double latitude,
        double longitude,
        double frpMegawatts = 100) =>
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
