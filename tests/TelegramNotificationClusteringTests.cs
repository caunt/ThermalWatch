using ThermalWatch.Core;
using ThermalWatch.Telegram;

namespace ThermalWatch.Tests;

public sealed class TelegramNotificationClusteringTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_IncludesRelatedDetectionSeenInEarlierSnapshot()
    {
        var earlier = Detection("earlier", ObservedAt, 50.000, 30.000);
        var newlySeen = Detection("new", ObservedAt.AddMinutes(5), 50.010, 30.010);

        var clusters = TelegramNotificationClustering.Create(
            [earlier, newlySeen],
            [newlySeen],
            radiusKilometers: 5,
            timeWindow: TimeSpan.FromMinutes(90),
            includeActiveContext: true);

        var cluster = Assert.Single(clusters);
        Assert.Equal(2, cluster.Members.Length);
        Assert.Contains(cluster.Members, member => member.Id == earlier.Id);
        Assert.Contains(cluster.Members, member => member.Id == newlySeen.Id);
    }

    [Fact]
    public void Create_AllowsEarlierContextDetectionToRemainRepresentative()
    {
        var earlier = Detection("earlier", ObservedAt, 50.000, 30.000, frpMegawatts: 200);
        var newlySeen = Detection("new", ObservedAt.AddMinutes(5), 50.010, 30.010, frpMegawatts: 100);

        var cluster = Assert.Single(TelegramNotificationClustering.Create(
            [earlier, newlySeen],
            [newlySeen],
            radiusKilometers: 5,
            timeWindow: TimeSpan.FromMinutes(90),
            includeActiveContext: true));

        Assert.Equal(earlier.Id, cluster.Representative.Id);
    }

    [Fact]
    public void Create_DoesNotReprocessUnrelatedActiveDetection()
    {
        var unrelated = Detection("unrelated", ObservedAt, 40.000, 20.000);
        var newlySeen = Detection("new", ObservedAt.AddMinutes(5), 50.000, 30.000);

        var cluster = Assert.Single(TelegramNotificationClustering.Create(
            [unrelated, newlySeen],
            [newlySeen],
            radiusKilometers: 5,
            timeWindow: TimeSpan.FromMinutes(90),
            includeActiveContext: true));

        Assert.Single(cluster.Members);
        Assert.Equal(newlySeen.Id, cluster.Members[0].Id);
    }

    [Fact]
    public void Create_DoesNotIncludeContextOutsideTimeWindow()
    {
        var tooOld = Detection("old", ObservedAt, 50.000, 30.000);
        var newlySeen = Detection("new", ObservedAt.AddMinutes(91), 50.010, 30.010);

        var cluster = Assert.Single(TelegramNotificationClustering.Create(
            [tooOld, newlySeen],
            [newlySeen],
            radiusKilometers: 5,
            timeWindow: TimeSpan.FromMinutes(90),
            includeActiveContext: true));

        Assert.Single(cluster.Members);
        Assert.Equal(newlySeen.Id, cluster.Members[0].Id);
    }

    [Fact]
    public void Create_PreservesNewDetectionsOnlyBehaviorWhenContextIsDisabled()
    {
        var earlier = Detection("earlier", ObservedAt, 50.000, 30.000);
        var newlySeen = Detection("new", ObservedAt.AddMinutes(5), 50.010, 30.010);

        var cluster = Assert.Single(TelegramNotificationClustering.Create(
            [earlier, newlySeen],
            [newlySeen],
            radiusKilometers: 5,
            timeWindow: TimeSpan.FromMinutes(90),
            includeActiveContext: false));

        Assert.Single(cluster.Members);
        Assert.Equal(newlySeen.Id, cluster.Members[0].Id);
    }

    [Fact]
    public void Create_ReturnsNoCandidatesWithoutNewDetections()
    {
        var existing = Detection("existing", ObservedAt, 50.000, 30.000);

        var clusters = TelegramNotificationClustering.Create(
            [existing],
            [],
            radiusKilometers: 5,
            timeWindow: TimeSpan.FromMinutes(90),
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
            "UKR",
            "VIIRS_SNPP_NRT",
            "N",
            "VIIRS",
            latitude,
            longitude,
            acquiredAtUtc,
            "D",
            330,
            300,
            frpMegawatts,
            0.4,
            0.4,
            "n",
            null,
            "nominal",
            "2.0NRT",
            $"https://example.test/{id}");
}
