using ThermalWatch.Core;
using ThermalWatch.Telegram;

namespace ThermalWatch.Tests;

public sealed class TelegramAutomaticNotificationStateTests
{
    private const double RadiusKilometers = 5;
    private static readonly DateTimeOffset ObservedAt = new(2026, 7, 23, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TimeWindow = TimeSpan.FromMinutes(90);
    private static readonly TimeSpan Retention = TimeSpan.FromHours(48);
    private static readonly TelegramPreviewSelection PreviewSelection = new(
        new(30, 20, 900, 600),
        0);

    [Fact]
    public void PrepareCandidate_SuppressesDifferentSatelliteInDeliveredEpisode()
    {
        var state = CreateState();
        var delivered = Cluster(Detection(
            "noaa20",
            ObservedAt,
            57.94608,
            60.06142,
            source: "VIIRS_NOAA20_NRT",
            satellite: "N20"));
        var laterSatellite = Cluster(Detection(
            "snpp",
            ObservedAt.AddMinutes(53),
            57.947,
            60.062,
            source: "VIIRS_SNPP_NRT",
            satellite: "N"));

        state.RecordDelivered(delivered, ObservedAt.AddHours(1));

        var preparation = state.PrepareCandidate(laterSatellite, ObservedAt.AddHours(2));

        Assert.True(preparation.ContinuesDeliveredEpisode);
        Assert.Equal(laterSatellite.Id, preparation.Cluster.Id);
        Assert.Equal(0, state.PendingCount);
    }

    [Fact]
    public void PrepareCandidate_ExtendsDeliveredEpisodeTransitively()
    {
        var state = CreateState();
        var first = Cluster(Detection("first", ObservedAt, 0, 0));
        var bridge = Cluster(Detection("bridge", ObservedAt.AddMinutes(60), 0, 0.04));
        var continuation = Cluster(Detection("continuation", ObservedAt.AddMinutes(120), 0, 0.08));
        state.RecordDelivered(first, ObservedAt);

        var bridgePreparation = state.PrepareCandidate(bridge, ObservedAt.AddMinutes(60));
        var continuationPreparation = state.PrepareCandidate(
            continuation,
            ObservedAt.AddMinutes(120));

        Assert.True(bridgePreparation.ContinuesDeliveredEpisode);
        Assert.True(continuationPreparation.ContinuesDeliveredEpisode);
        Assert.False(TelegramNotificationClustering.AreRelated(
            first,
            continuation,
            RadiusKilometers,
            TimeWindow));
    }

    [Fact]
    public void PrepareCandidate_AllowsNewEpisodeOutsideTimeWindow()
    {
        var state = CreateState();
        var delivered = Cluster(Detection("delivered", ObservedAt, 50, 30));
        var later = Cluster(Detection("later", ObservedAt.AddMinutes(91), 50, 30));
        state.RecordDelivered(delivered, ObservedAt);

        var preparation = state.PrepareCandidate(later, ObservedAt.AddMinutes(91));

        Assert.False(preparation.ContinuesDeliveredEpisode);
    }

    [Fact]
    public void PrepareCandidate_AllowsNewEpisodeOutsideRadius()
    {
        var state = CreateState();
        var delivered = Cluster(Detection("delivered", ObservedAt, 0, 0));
        var distant = Cluster(Detection("distant", ObservedAt.AddMinutes(5), 0, 0.05));
        state.RecordDelivered(delivered, ObservedAt);

        var preparation = state.PrepareCandidate(distant, ObservedAt.AddMinutes(5));

        Assert.False(preparation.ContinuesDeliveredEpisode);
    }

    [Fact]
    public void PrepareCandidate_AllowsRelatedDetectionAfterHistoryExpires()
    {
        var state = CreateState();
        var delivered = Cluster(Detection("delivered", ObservedAt, 50, 30));
        var related = Cluster(Detection("related", ObservedAt.AddMinutes(5), 50, 30));
        state.RecordDelivered(delivered, ObservedAt);

        var preparation = state.PrepareCandidate(
            related,
            ObservedAt.Add(Retention).AddMinutes(1));

        Assert.False(preparation.ContinuesDeliveredEpisode);
    }

    [Fact]
    public void PrepareCandidate_CoalescesPendingClusterAndPreservesRetryStart()
    {
        var state = CreateState();
        var firstSeenUtc = ObservedAt.AddMinutes(10);
        var first = Cluster(Detection("first", ObservedAt, 50, 30, frpMegawatts: 100));
        var later = Cluster(Detection(
            "later",
            ObservedAt.AddMinutes(5),
            50.01,
            30.01,
            frpMegawatts: 200));
        state.AddPending(new(first, firstSeenUtc, PreviewSelection, "first summary"));

        var preparation = state.PrepareCandidate(later, firstSeenUtc.AddMinutes(20));

        Assert.False(preparation.ContinuesDeliveredEpisode);
        Assert.Equal(firstSeenUtc, preparation.FirstSeenUtc);
        Assert.Equal(0, state.PendingCount);
        Assert.Equal(2, preparation.Cluster.Members.Length);
        Assert.Equal("later", preparation.Cluster.Representative.Id);
        Assert.Contains(preparation.Cluster.Members, member => member.Id == "first");
        Assert.Contains(preparation.Cluster.Members, member => member.Id == "later");
    }

    [Fact]
    public void TrySuppressPending_RemovesAndExtendsDeliveredEpisode()
    {
        var state = CreateState();
        var delivered = Cluster(Detection("delivered", ObservedAt, 0, 0));
        var pending = Cluster(Detection("pending", ObservedAt.AddMinutes(60), 0, 0.04));
        var continuation = Cluster(Detection(
            "continuation",
            ObservedAt.AddMinutes(120),
            0,
            0.08));
        state.AddPending(new(pending, ObservedAt, PreviewSelection, null));
        state.RecordDelivered(delivered, ObservedAt);

        var suppressedPending = state.TrySuppressPending(0, ObservedAt.AddMinutes(60));
        var preparation = state.PrepareCandidate(
            continuation,
            ObservedAt.AddMinutes(120));

        Assert.True(suppressedPending);
        Assert.Equal(0, state.PendingCount);
        Assert.True(preparation.ContinuesDeliveredEpisode);
    }

    [Fact]
    public void RemovingUndeliveredPendingDoesNotEstablishEpisode()
    {
        var state = CreateState();
        var pending = Cluster(Detection("pending", ObservedAt, 50, 30));
        var continuation = Cluster(Detection(
            "continuation",
            ObservedAt.AddMinutes(5),
            50.01,
            30.01));
        state.AddPending(new(pending, ObservedAt, PreviewSelection, null));

        state.RemovePendingAt(0);
        var preparation = state.PrepareCandidate(
            continuation,
            ObservedAt.AddMinutes(5));

        Assert.False(preparation.ContinuesDeliveredEpisode);
    }

    [Fact]
    public void UndeliveredPendingRemainsEligibleForRetry()
    {
        var state = CreateState();
        var pending = Cluster(Detection("pending", ObservedAt, 50, 30));
        state.AddPending(new(pending, ObservedAt, PreviewSelection, null));

        var suppressed = state.TrySuppressPending(0, ObservedAt.AddMinutes(5));

        Assert.False(suppressed);
        Assert.Equal(1, state.PendingCount);
        Assert.Equal(pending.Id, state.GetPending(0).Cluster.Id);
    }

    private static TelegramAutomaticNotificationState CreateState() =>
        new(RadiusKilometers, TimeWindow, Retention);

    private static NotificationCluster Cluster(params Anomaly[] detections) =>
        Assert.Single(NotificationClustering.Create(
            detections,
            RadiusKilometers,
            TimeWindow));

    private static Anomaly Detection(
        string id,
        DateTimeOffset acquiredAtUtc,
        double latitude,
        double longitude,
        double frpMegawatts = 100,
        string source = "VIIRS_SNPP_NRT",
        string satellite = "N") =>
        new(
            id,
            "RUS",
            source,
            satellite,
            "VIIRS",
            latitude,
            longitude,
            acquiredAtUtc,
            "D",
            350,
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
