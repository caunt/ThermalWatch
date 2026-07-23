using ThermalWatch.Core;

namespace ThermalWatch.Tests;

public sealed class NotificationAutomaticStateTests
{
    private const double RadiusKilometers = 5;
    private static readonly DateTimeOffset s_observedAt = new(year: 2026, month: 7, day: 23, hour: 8, minute: 0, second: 0, TimeSpan.Zero);
    private static readonly TimeSpan s_timeWindow = TimeSpan.FromMinutes(minutes: 90);
    private static readonly TimeSpan s_retention = TimeSpan.FromHours(hours: 48);
    private static readonly NotificationPreviewSelection s_previewSelection = new(
        new(WidthKilometers: 30, HeightKilometers: 20, PixelWidth: 900, PixelHeight: 600),
        ClusterDiameterKilometers: 0,
        IsLargePreview: false);

    [Fact]
    public void PrepareCandidateSuppressesDifferentSatelliteInDeliveredEpisode()
    {
        NotificationAutomaticState state = CreateState();
        NotificationCluster delivered = Cluster(Detection(
            id: "noaa20",
            s_observedAt,
            latitude: 57.94608,
            longitude: 60.06142,
            source: "VIIRS_NOAA20_NRT",
            satellite: "N20"));
        NotificationCluster laterSatellite = Cluster(Detection(
            id: "snpp",
            s_observedAt.AddMinutes(53),
            latitude: 57.947,
            longitude: 60.062,
            source: "VIIRS_SNPP_NRT",
            satellite: "N"));

        state.RecordDelivered(delivered, s_observedAt.AddHours(1));

        NotificationCandidatePreparation preparation = state.PrepareCandidate(laterSatellite, s_observedAt.AddHours(2));

        Assert.True(preparation.ContinuesDeliveredEpisode);
        Assert.Equal(laterSatellite.Id, preparation.Cluster.Id);
        Assert.Equal(0, state.PendingCount);
    }

    [Fact]
    public void PrepareCandidateExtendsDeliveredEpisodeTransitively()
    {
        NotificationAutomaticState state = CreateState();
        NotificationCluster first = Cluster(Detection(id: "first", s_observedAt, latitude: 0, longitude: 0));
        NotificationCluster bridge = Cluster(Detection(id: "bridge", s_observedAt.AddMinutes(60), latitude: 0, longitude: 0.04));
        NotificationCluster continuation = Cluster(Detection(id: "continuation", s_observedAt.AddMinutes(120), latitude: 0, longitude: 0.08));
        state.RecordDelivered(first, s_observedAt);

        NotificationCandidatePreparation bridgePreparation = state.PrepareCandidate(bridge, s_observedAt.AddMinutes(60));
        NotificationCandidatePreparation continuationPreparation = state.PrepareCandidate(
            continuation,
            s_observedAt.AddMinutes(120));

        Assert.True(bridgePreparation.ContinuesDeliveredEpisode);
        Assert.True(continuationPreparation.ContinuesDeliveredEpisode);
        Assert.False(NotificationClustering.AreRelated(
            first,
            continuation,
            RadiusKilometers,
            s_timeWindow));
    }

    [Fact]
    public void PrepareCandidateAllowsNewEpisodeOutsideTimeWindow()
    {
        NotificationAutomaticState state = CreateState();
        NotificationCluster delivered = Cluster(Detection(id: "delivered", s_observedAt, latitude: 50, longitude: 30));
        NotificationCluster later = Cluster(Detection(id: "later", s_observedAt.AddMinutes(91), latitude: 50, longitude: 30));
        state.RecordDelivered(delivered, s_observedAt);

        NotificationCandidatePreparation preparation = state.PrepareCandidate(later, s_observedAt.AddMinutes(91));

        Assert.False(preparation.ContinuesDeliveredEpisode);
    }

    [Fact]
    public void PrepareCandidateAllowsNewEpisodeOutsideRadius()
    {
        NotificationAutomaticState state = CreateState();
        NotificationCluster delivered = Cluster(Detection(id: "delivered", s_observedAt, latitude: 0, longitude: 0));
        NotificationCluster distant = Cluster(Detection(id: "distant", s_observedAt.AddMinutes(5), latitude: 0, longitude: 0.05));
        state.RecordDelivered(delivered, s_observedAt);

        NotificationCandidatePreparation preparation = state.PrepareCandidate(distant, s_observedAt.AddMinutes(5));

        Assert.False(preparation.ContinuesDeliveredEpisode);
    }

    [Fact]
    public void PrepareCandidateAllowsRelatedDetectionAfterHistoryExpires()
    {
        NotificationAutomaticState state = CreateState();
        NotificationCluster delivered = Cluster(Detection(id: "delivered", s_observedAt, latitude: 50, longitude: 30));
        NotificationCluster related = Cluster(Detection(id: "related", s_observedAt.AddMinutes(5), latitude: 50, longitude: 30));
        state.RecordDelivered(delivered, s_observedAt);

        NotificationCandidatePreparation preparation = state.PrepareCandidate(
            related,
            s_observedAt.Add(s_retention).AddMinutes(1));

        Assert.False(preparation.ContinuesDeliveredEpisode);
    }

    [Fact]
    public void PrepareCandidateCoalescesPendingClusterAndPreservesRetryStart()
    {
        NotificationAutomaticState state = CreateState();
        DateTimeOffset firstSeenUtc = s_observedAt.AddMinutes(10);
        NotificationCluster first = Cluster(Detection(id: "first", s_observedAt, latitude: 50, longitude: 30, frpMegawatts: 100));
        NotificationCluster later = Cluster(Detection(
            id: "later",
            s_observedAt.AddMinutes(5),
            latitude: 50.01,
            longitude: 30.01,
            frpMegawatts: 200));
        state.AddPending(new(first, firstSeenUtc, s_previewSelection, "first summary"));

        NotificationCandidatePreparation preparation = state.PrepareCandidate(later, firstSeenUtc.AddMinutes(20));

        Assert.False(preparation.ContinuesDeliveredEpisode);
        Assert.Equal(firstSeenUtc, preparation.FirstSeenUtc);
        Assert.Equal(0, state.PendingCount);
        Assert.Equal(2, preparation.Cluster.Members.Length);
        Assert.Equal("later", preparation.Cluster.Representative.Id);
        Assert.Contains(preparation.Cluster.Members, member => "first".Equals(member.Id, StringComparison.Ordinal));
        Assert.Contains(preparation.Cluster.Members, member => "later".Equals(member.Id, StringComparison.Ordinal));
    }

    [Fact]
    public void TrySuppressPendingRemovesAndExtendsDeliveredEpisode()
    {
        NotificationAutomaticState state = CreateState();
        NotificationCluster delivered = Cluster(Detection(id: "delivered", s_observedAt, latitude: 0, longitude: 0));
        NotificationCluster pending = Cluster(Detection(id: "pending", s_observedAt.AddMinutes(60), latitude: 0, longitude: 0.04));
        NotificationCluster continuation = Cluster(Detection(
            id: "continuation",
            s_observedAt.AddMinutes(120),
            latitude: 0,
            longitude: 0.08));
        state.AddPending(new(pending, s_observedAt, s_previewSelection, null));
        state.RecordDelivered(delivered, s_observedAt);

        bool suppressedPending = state.TrySuppressPending(index: 0, s_observedAt.AddMinutes(60));
        NotificationCandidatePreparation preparation = state.PrepareCandidate(
            continuation,
            s_observedAt.AddMinutes(120));

        Assert.True(suppressedPending);
        Assert.Equal(0, state.PendingCount);
        Assert.True(preparation.ContinuesDeliveredEpisode);
    }

    [Fact]
    public void RemovingUndeliveredPendingDoesNotEstablishEpisode()
    {
        NotificationAutomaticState state = CreateState();
        NotificationCluster pending = Cluster(Detection(id: "pending", s_observedAt, latitude: 50, longitude: 30));
        NotificationCluster continuation = Cluster(Detection(
            id: "continuation",
            s_observedAt.AddMinutes(5),
            latitude: 50.01,
            longitude: 30.01));
        state.AddPending(new(pending, s_observedAt, s_previewSelection, null));

        state.RemovePendingAt(0);
        NotificationCandidatePreparation preparation = state.PrepareCandidate(
            continuation,
            s_observedAt.AddMinutes(5));

        Assert.False(preparation.ContinuesDeliveredEpisode);
    }

    [Fact]
    public void UndeliveredPendingRemainsEligibleForRetry()
    {
        NotificationAutomaticState state = CreateState();
        NotificationCluster pending = Cluster(Detection(id: "pending", s_observedAt, latitude: 50, longitude: 30));
        state.AddPending(new(pending, s_observedAt, s_previewSelection, null));

        bool suppressed = state.TrySuppressPending(index: 0, s_observedAt.AddMinutes(5));

        Assert.False(suppressed);
        Assert.Equal(1, state.PendingCount);
        Assert.Equal(pending.Id, state.GetPending(index: 0).Cluster.Id);
    }

    private static NotificationAutomaticState CreateState() =>
        new(RadiusKilometers, s_timeWindow, s_retention);

    private static NotificationCluster Cluster(params Anomaly[] detections) =>
        Assert.Single(NotificationClustering.Create(
            detections,
            RadiusKilometers,
            s_timeWindow));

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
