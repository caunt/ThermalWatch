using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using ThermalWatch.Core;

namespace ThermalWatch.Tests;

public sealed class NotificationCandidateEngineTests
{
    private static readonly DateTimeOffset s_observedAt = new(
        year: 2026,
        month: 7,
        day: 23,
        hour: 8,
        minute: 0,
        second: 0,
        TimeSpan.Zero);

    [Fact]
    public async Task GetNotificationDiagnosticAsyncBuildsTransitiveClusterAndDoesNotConsumeAutomaticCandidates()
    {
        var handler = new NotFoundHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
        NotificationCandidateEngine engine = CreateEngine(handler, cache);
        AnomalySnapshot snapshot = Snapshot(
            CreateAnomaly(id: "first", longitude: 30, frpMegawatts: 100),
            CreateAnomaly(id: "bridge", longitude: 30.03, frpMegawatts: 200),
            CreateAnomaly(id: "last", longitude: 30.06, frpMegawatts: 150));

        NotificationDiagnostic diagnostic = Assert.IsType<NotificationDiagnostic>(
            await engine.GetNotificationDiagnosticAsync(
                snapshot,
                anomalyId: "first",
                TestContext.Current.CancellationToken));

        Assert.Equal(3, diagnostic.DetectionCount);
        Assert.Equal("bridge", diagnostic.RepresentativeId);
        Assert.Equal(["bridge", "first", "last"], diagnostic.MemberIds.Order(StringComparer.Ordinal));
        Assert.Equal(7, diagnostic.Criteria.Length);
        Assert.True(diagnostic.IsEligible);
        Assert.Equal(0, handler.RequestCount);

        PreparedNotificationCandidate? delivered = null;
        AutomaticNotificationProcessingResult processing = await engine.ProcessAutomaticNotificationsAsync(
            snapshot,
            (candidate, _) =>
            {
                delivered = candidate;
                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken);

        Assert.True(processing.ContinueProcessing);
        Assert.NotNull(delivered);
        Assert.Equal(3, delivered.Cluster.Members.Length);
        Assert.Equal(1, processing.Summary.DeliveredClusterCount);
    }

    [Fact]
    public async Task GetNotificationDiagnosticAsyncReturnsNullForAnomalyOutsideTheSnapshot()
    {
        var handler = new NotFoundHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
        NotificationCandidateEngine engine = CreateEngine(handler, cache);

        NotificationDiagnostic? diagnostic = await engine.GetNotificationDiagnosticAsync(
            Snapshot(CreateAnomaly(id: "present", longitude: 30, frpMegawatts: 100)),
            anomalyId: "missing",
            TestContext.Current.CancellationToken);

        Assert.Null(diagnostic);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetNotificationDiagnosticAsyncLooksUpNearbyFeaturesForSelectedAnomalyInsteadOfRepresentative()
    {
        var gibsHandler = new NotFoundHandler();
        var nearbyHandler = new NearbyHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
        NotificationCandidateEngine engine = CreateEngine(gibsHandler, cache, nearbyHandler);
        AnomalySnapshot snapshot = Snapshot(
            CreateAnomaly(id: "selected", longitude: 30, frpMegawatts: 100),
            CreateAnomaly(id: "representative", longitude: 30.02, frpMegawatts: 200));

        NotificationDiagnostic diagnostic = Assert.IsType<NotificationDiagnostic>(
            await engine.GetNotificationDiagnosticAsync(
                snapshot,
                anomalyId: "selected",
                TestContext.Current.CancellationToken));

        Assert.Equal("representative", diagnostic.RepresentativeId);
        string query = Assert.Single(nearbyHandler.Queries);
        Assert.Contains("around:2000,50.000000,30.000000", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutomaticAndManualCandidatesEnrichOnlyTheirSelectedRepresentatives()
    {
        var automaticGibs = new NotFoundHandler();
        var automaticNearby = new NearbyHandler();
        using var automaticCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
        NotificationCandidateEngine automaticEngine = CreateEngine(
            automaticGibs,
            automaticCache,
            automaticNearby);
        AnomalySnapshot automaticSnapshot = Snapshot(
            CreateAnomaly(id: "lower", longitude: 30, frpMegawatts: 100),
            CreateAnomaly(id: "higher", longitude: 30.02, frpMegawatts: 200));

        await automaticEngine.ProcessAutomaticNotificationsAsync(
            automaticSnapshot,
            (_, _) => Task.FromResult(NotificationDeliveryOutcome.Delivered),
            TestContext.Current.CancellationToken);

        string automaticQuery = Assert.Single(automaticNearby.Queries);
        Assert.Contains("around:2000,50.000000,30.020000", automaticQuery, StringComparison.Ordinal);

        var manualGibs = new NotFoundHandler();
        var manualNearby = new NearbyHandler();
        using var manualCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
        NotificationCandidateEngine manualEngine = CreateEngine(manualGibs, manualCache, manualNearby);
        AnomalySnapshot manualSnapshot = Snapshot(
            CreateAnomaly(id: "low", longitude: 30, frpMegawatts: 100),
            CreateAnomaly(id: "low-context", longitude: 30.01, frpMegawatts: 90),
            CreateAnomaly(id: "highest", longitude: 31, frpMegawatts: 300),
            CreateAnomaly(id: "highest-context", longitude: 31.01, frpMegawatts: 290),
            CreateAnomaly(id: "middle", longitude: 32, frpMegawatts: 200),
            CreateAnomaly(id: "middle-context", longitude: 32.01, frpMegawatts: 190));

        ManualNotificationCandidateSelection manual = await manualEngine.PrepareManualCandidatesAsync(
            manualSnapshot,
            requestedClusterCount: 1,
            TestContext.Current.CancellationToken);

        Assert.Single(manual.SelectedCandidates);
        string manualQuery = Assert.Single(manualNearby.Queries);
        Assert.Contains("around:2000,50.000000,31.000000", manualQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EligibleClusterQueryFiltersOrdersAndDoesNotConsumeAutomaticLifecycle()
    {
        var gibsHandler = new NotFoundHandler();
        var nearbyHandler = new NearbyHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
        NotificationOptions options = DefaultOptions() with { SendExistingOnStartup = false };
        NotificationCandidateEngine engine = CreateEngine(
            gibsHandler,
            cache,
            nearbyHandler,
            options);
        AnomalySnapshot snapshot = Snapshot(
            CreateAnomaly(id: "low", longitude: 30, frpMegawatts: 100),
            CreateAnomaly(id: "low-context", longitude: 30.01, frpMegawatts: 90),
            CreateAnomaly(id: "highest", longitude: 31, frpMegawatts: 300),
            CreateAnomaly(id: "highest-context", longitude: 31.01, frpMegawatts: 290),
            CreateAnomaly(id: "middle", longitude: 32, frpMegawatts: 200),
            CreateAnomaly(id: "middle-context", longitude: 32.01, frpMegawatts: 190),
            CreateAnomaly(id: "filtered-singleton", longitude: 33, frpMegawatts: 500));

        EligibleNotificationClusters result = await engine.GetEligibleNotificationClustersAsync(
            snapshot,
            TestContext.Current.CancellationToken);

        Assert.Equal(snapshot.GeneratedAtUtc, result.SnapshotGeneratedAtUtc);
        Assert.Equal(4, result.EvaluatedClusterCount);
        Assert.Equal(3, result.EligibleClusterCount);
        Assert.Equal(["highest", "middle", "low"], result.Clusters.Select(cluster => cluster.RepresentativeId));
        EligibleNotificationCluster first = result.Clusters[0];
        Assert.Equal("RUS", first.CountryCode);
        Assert.Equal("VIIRS_SNPP_NRT", first.Source);
        Assert.Equal("N", first.Satellite);
        Assert.Equal(50, first.Latitude);
        Assert.Equal(31, first.Longitude);
        Assert.Equal(2, first.DetectionCount);
        Assert.True(first.ClusterDiameterKilometers > 0);
        Assert.Equal(0, gibsHandler.RequestCount);
        Assert.Empty(nearbyHandler.Queries);

        int deliveryCount = 0;
        AutomaticNotificationProcessingResult processing = await engine.ProcessAutomaticNotificationsAsync(
            snapshot,
            (_, _) =>
            {
                deliveryCount++;
                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(4, processing.Summary.EvaluatedClusterCount);
        Assert.Equal(3, processing.Summary.StartupSuppressedIncidentCount);
        Assert.Equal(1, processing.Summary.RejectedClusterCount);
        Assert.Equal(0, gibsHandler.RequestCount);
        Assert.Equal(0, deliveryCount);
    }

    [Fact]
    public async Task EligibleClusterQueryFailsClosedAndReevaluatesRequiredPreview()
    {
        var handler = new RecoveringPreviewHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
        NotificationOptions options = DefaultOptions() with
        {
            Visibility = DefaultOptions().Visibility with { RequirePreview = true }
        };
        NotificationCandidateEngine engine = CreateEngine(handler, cache, options: options);
        AnomalySnapshot snapshot = Snapshot(
            CreateAnomaly(id: "first", longitude: 30, frpMegawatts: 100),
            CreateAnomaly(id: "second", longitude: 30.02, frpMegawatts: 200));

        EligibleNotificationClusters unavailable = await engine.GetEligibleNotificationClustersAsync(
            snapshot,
            TestContext.Current.CancellationToken);
        handler.IsPreviewAvailable = true;
        EligibleNotificationClusters available = await engine.GetEligibleNotificationClustersAsync(
            snapshot,
            TestContext.Current.CancellationToken);

        Assert.Empty(unavailable.Clusters);
        Assert.Single(available.Clusters);
    }

    [Fact]
    public async Task EligibleClusterQueryFailsOpenWhenLandCoverIsUnavailable()
    {
        var handler = new NotFoundHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
        NotificationOptions options = DefaultOptions() with
        {
            LandCover = DefaultOptions().LandCover with { Enabled = true }
        };
        NotificationCandidateEngine engine = CreateEngine(handler, cache, options: options);
        AnomalySnapshot snapshot = Snapshot(
            CreateAnomaly(id: "first", longitude: 30, frpMegawatts: 100),
            CreateAnomaly(id: "second", longitude: 30.02, frpMegawatts: 200));

        EligibleNotificationClusters result = await engine.GetEligibleNotificationClustersAsync(
            snapshot,
            TestContext.Current.CancellationToken);

        Assert.Single(result.Clusters);
        Assert.True(handler.RequestCount > 0);
    }

    [Fact]
    public async Task AutomaticReevaluatesActiveClusterUntilRequiredPreviewIsAvailable()
    {
        var handler = new RecoveringPreviewHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
        NotificationOptions options = DefaultOptions() with
        {
            Visibility = DefaultOptions().Visibility with { RequirePreview = true }
        };
        NotificationCandidateEngine engine = CreateEngine(handler, cache, options: options);
        AnomalySnapshot snapshot = Snapshot(
            CreateAnomaly(id: "first", longitude: 30, frpMegawatts: 100),
            CreateAnomaly(id: "second", longitude: 30.02, frpMegawatts: 200));
        int deliveryCount = 0;

        AutomaticNotificationProcessingResult unavailable = await engine.ProcessAutomaticNotificationsAsync(
            snapshot,
            (_, _) =>
            {
                deliveryCount++;
                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken);
        handler.IsPreviewAvailable = true;
        AutomaticNotificationProcessingResult available = await engine.ProcessAutomaticNotificationsAsync(
            snapshot,
            (_, _) =>
            {
                deliveryCount++;
                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken);
        AutomaticNotificationProcessingResult delivered = await engine.ProcessAutomaticNotificationsAsync(
            snapshot,
            (_, _) =>
            {
                deliveryCount++;
                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, unavailable.Summary.DeliveredClusterCount);
        Assert.Equal(1, unavailable.Summary.RejectionCount(NotificationRejectionReason.PreviewUnavailable));
        Assert.Equal(1, available.Summary.DeliveredClusterCount);
        Assert.Equal(1, delivered.Summary.DuplicateEpisodeCount);
        Assert.Equal(1, deliveryCount);
    }

    [Fact]
    public async Task AutomaticReevaluatesPreviewBlockedStartupIncidentUntilItIsEligible()
    {
        var handler = new RecoveringPreviewHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
        NotificationOptions options = DefaultOptions() with
        {
            SendExistingOnStartup = false,
            Visibility = DefaultOptions().Visibility with { RequirePreview = true }
        };
        NotificationCandidateEngine engine = CreateEngine(handler, cache, options: options);
        AnomalySnapshot snapshot = Snapshot(
            CreateAnomaly(id: "first", longitude: 30, frpMegawatts: 100),
            CreateAnomaly(id: "second", longitude: 30.02, frpMegawatts: 200));
        int deliveryCount = 0;

        AutomaticNotificationProcessingResult unavailable = await engine.ProcessAutomaticNotificationsAsync(
            snapshot,
            (_, _) =>
            {
                deliveryCount++;
                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken);
        handler.IsPreviewAvailable = true;
        AutomaticNotificationProcessingResult available = await engine.ProcessAutomaticNotificationsAsync(
            snapshot,
            (_, _) =>
            {
                deliveryCount++;
                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken);
        AutomaticNotificationProcessingResult delivered = await engine.ProcessAutomaticNotificationsAsync(
            snapshot,
            (_, _) =>
            {
                deliveryCount++;
                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, unavailable.Summary.StartupSuppressedIncidentCount);
        Assert.Equal(1, unavailable.Summary.RejectionCount(NotificationRejectionReason.PreviewUnavailable));
        Assert.Equal(1, available.Summary.DeliveredClusterCount);
        Assert.Equal(1, delivered.Summary.DuplicateEpisodeCount);
        Assert.Equal(1, deliveryCount);
    }

    [Fact]
    public async Task AutomaticSendsTextImmediatelyWhenPreviewIsOptional()
    {
        var handler = new NotFoundHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
        NotificationCandidateEngine engine = CreateEngine(handler, cache);
        AnomalySnapshot snapshot = Snapshot(
            CreateAnomaly(id: "first", longitude: 30, frpMegawatts: 100),
            CreateAnomaly(id: "second", longitude: 30.02, frpMegawatts: 200));
        PreparedNotificationCandidate? delivered = null;

        AutomaticNotificationProcessingResult result = await engine.ProcessAutomaticNotificationsAsync(
            snapshot,
            (candidate, _) =>
            {
                delivered = candidate;
                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken);

        Assert.False(Assert.IsType<PreparedNotificationCandidate>(delivered).Preview.IsAvailable);
        Assert.Equal(1, result.Summary.DeliveredClusterCount);
        Assert.Equal(0, result.Summary.RejectedClusterCount);
    }

    [Fact]
    public async Task AutomaticRetriesTransientDeliveryThroughNextSnapshotEvaluation()
    {
        var handler = new NotFoundHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
        NotificationCandidateEngine engine = CreateEngine(handler, cache);
        AnomalySnapshot snapshot = Snapshot(
            CreateAnomaly(id: "first", longitude: 30, frpMegawatts: 100),
            CreateAnomaly(id: "second", longitude: 30.02, frpMegawatts: 200));
        int deliveryCount = 0;

        AutomaticNotificationProcessingResult failed = await engine.ProcessAutomaticNotificationsAsync(
            snapshot,
            (_, _) =>
            {
                deliveryCount++;
                return Task.FromResult(NotificationDeliveryOutcome.RetryLater);
            },
            TestContext.Current.CancellationToken);
        AutomaticNotificationProcessingResult retried = await engine.ProcessAutomaticNotificationsAsync(
            snapshot,
            (_, _) =>
            {
                deliveryCount++;
                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken);
        AutomaticNotificationProcessingResult delivered = await engine.ProcessAutomaticNotificationsAsync(
            snapshot,
            (_, _) =>
            {
                deliveryCount++;
                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken);

        Assert.True(failed.ContinueProcessing);
        Assert.Equal(1, failed.Summary.SendFailureCount);
        Assert.Equal(1, retried.Summary.DeliveredClusterCount);
        Assert.Equal(1, delivered.Summary.DuplicateEpisodeCount);
        Assert.Equal(2, deliveryCount);
    }

    [Fact]
    public async Task AutomaticSuppressesEligibleStartupIncidentAndRelatedExtensions()
    {
        var handler = new NotFoundHandler();
        var nearbyHandler = new NearbyHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
        NotificationOptions options = DefaultOptions() with { SendExistingOnStartup = false };
        NotificationCandidateEngine engine = CreateEngine(
            handler,
            cache,
            nearbyHandler,
            options);
        Anomaly first = CreateAnomaly(id: "first", longitude: 30, frpMegawatts: 100);
        Anomaly second = CreateAnomaly(id: "second", longitude: 30.02, frpMegawatts: 200);
        AnomalySnapshot startupSnapshot = Snapshot(first, second);
        int deliveryCount = 0;

        AutomaticNotificationProcessingResult startup = await engine.ProcessAutomaticNotificationsAsync(
            startupSnapshot,
            (_, _) =>
            {
                deliveryCount++;
                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken);
        AutomaticNotificationProcessingResult unchanged = await engine.ProcessAutomaticNotificationsAsync(
            startupSnapshot,
            (_, _) =>
            {
                deliveryCount++;
                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken);
        AutomaticNotificationProcessingResult extended = await engine.ProcessAutomaticNotificationsAsync(
            Snapshot(
                first,
                second,
                CreateAnomaly(id: "later", longitude: 30.01, frpMegawatts: 150)),
            (_, _) =>
            {
                deliveryCount++;
                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, startup.Summary.EvaluatedClusterCount);
        Assert.Equal(1, startup.Summary.StartupSuppressedIncidentCount);
        Assert.Equal(1, unchanged.Summary.StartupSuppressedIncidentCount);
        Assert.Equal(1, extended.Summary.StartupSuppressedIncidentCount);
        Assert.Equal(0, extended.Summary.DeliveredClusterCount);
        Assert.Equal(0, deliveryCount);
        Assert.Equal(0, handler.RequestCount);
        Assert.Empty(nearbyHandler.Queries);
    }

    [Fact]
    public async Task GetNotificationDiagnosticAsyncExplainsLaterSnapshotReevaluationForUnavailableRequiredPreview()
    {
        var handler = new NotFoundHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
        NotificationOptions options = DefaultOptions() with
        {
            Visibility = DefaultOptions().Visibility with { RequirePreview = true }
        };
        NotificationCandidateEngine engine = CreateEngine(handler, cache, options: options);
        AnomalySnapshot snapshot = Snapshot(
            CreateAnomaly(id: "first", longitude: 30, frpMegawatts: 100),
            CreateAnomaly(id: "second", longitude: 30.02, frpMegawatts: 200));

        NotificationDiagnostic diagnostic = Assert.IsType<NotificationDiagnostic>(
            await engine.GetNotificationDiagnosticAsync(
                snapshot,
                anomalyId: "first",
                TestContext.Current.CancellationToken));
        NotificationCriterionResult preview = Assert.Single(
            diagnostic.Criteria,
            criterion => criterion.Code.Equals(value: "exact-preview", StringComparison.Ordinal));

        Assert.False(diagnostic.IsEligible);
        Assert.Equal(NotificationCriterionOutcomes.Unavailable, preview.Outcome);
        Assert.Equal(
            "No exact-date preview is currently available; automatic processing reevaluates the active cluster after later snapshot publications.",
            preview.Explanation);
    }

    private static NotificationCandidateEngine CreateEngine(
        HttpMessageHandler handler,
        IMemoryCache cache,
        HttpMessageHandler? nearbyHandler = null,
        NotificationOptions? options = null)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new(uriString: "https://gibs.example.test/")
        };
        var gibsClient = new GibsClient(httpClient, cache, NullLogger<GibsClient>.Instance);
        var nearbyClient = new NearbyFeatureClient(
            new HttpClient(nearbyHandler ?? new NotFoundHandler())
            {
                BaseAddress = new(uriString: "https://overpass.example.test/api/")
            },
            cache,
            NullLogger<NearbyFeatureClient>.Instance);
        return new(
            options ?? DefaultOptions(),
            gibsClient,
            nearbyClient,
            new FixedTimeProvider(s_observedAt.AddHours(1)));
    }

    private static NotificationOptions DefaultOptions() =>
        new(
            SendExistingOnStartup: true,
            ClusterRadiusKilometers: 5,
            ClusterTimeWindow: TimeSpan.FromMinutes(minutes: 90),
            EpisodeRetention: TimeSpan.FromHours(hours: 48),
            new(
                new(WidthKilometers: 30, HeightKilometers: 20),
                new(WidthKilometers: 45, HeightKilometers: 30),
                PixelWidth: 900,
                PixelHeight: 600,
                LargeClusterMinimumDetections: 8,
                LargeClusterMinimumFrpMegawatts: 500,
                LargeClusterMinimumDiameterKilometers: 8),
            new(
                Enabled: false,
                VegetationPercentThreshold: 50,
                BuiltUpProximityKilometers: 2,
                VegetationMaximumFrpMegawatts: 300,
                KeepHighFrpVegetation: false,
                KeepMultiSatelliteVegetation: false),
            new(
                Enabled: true,
                MinimumFrpMegawatts: 50,
                MinimumThermalContrastKelvin: 20,
                MinimumClusterDetections: 2,
                MinimumModisConfidencePercent: 60,
                MinimumViirsConfidence: NotificationViirsConfidenceLevel.Nominal,
                RequireDaytime: true,
                RequirePreview: false));

    private static AnomalySnapshot Snapshot(params Anomaly[] anomalies) =>
        new(
            s_observedAt.AddHours(1),
            ActiveWindowHours: 24,
            IsReady: true,
            IsPartiallyStale: false,
            ConfiguredCountryCodes: ["RUS"],
            Segments: [],
            AnomalyCount: anomalies.Length,
            Anomalies: [.. anomalies]);

    private static Anomaly CreateAnomaly(string id, double longitude, double frpMegawatts) =>
        new(
            id,
            CountryCode: "RUS",
            Source: "VIIRS_SNPP_NRT",
            Satellite: "N",
            Instrument: "VIIRS",
            Latitude: 50,
            longitude,
            s_observedAt,
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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class NearbyHandler : HttpMessageHandler
    {
        public List<string> Queries { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            Assert.StartsWith("data=", body, StringComparison.Ordinal);
            Queries.Add(Uri.UnescapeDataString(body[5..].Replace(oldChar: '+', newChar: ' ')));
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    content: "{\"elements\":[]}",
                    Encoding.UTF8,
                    mediaType: "application/json")
            };
        }
    }

    private sealed class RecoveringPreviewHandler : HttpMessageHandler
    {
        public bool IsPreviewAvailable { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HttpContent content;
            if (request.RequestUri!.AbsolutePath.EndsWith(value: ".xml", StringComparison.Ordinal))
            {
                content = new StringContent(
                    content: "<Domains><Domain>2026-07-23</Domain></Domains>",
                    Encoding.UTF8,
                    mediaType: "application/xml");
            }
            else
            {
                string layers = ReadQueryValue(request.RequestUri, name: "LAYERS");
                bool isComposite = layers.Contains(',', StringComparison.Ordinal);
                byte[] bytes = isComposite
                    ? PngTestData.CreateSolidRgba(
                        width: 900,
                        height: 600,
                        red: 30,
                        green: 80,
                        blue: 40,
                        alpha: 255)
                    : PngTestData.CreateSolidRgba(
                        width: 64,
                        height: 64,
                        red: IsPreviewAvailable ? (byte)30 : (byte)0,
                        green: IsPreviewAvailable ? (byte)80 : (byte)0,
                        blue: IsPreviewAvailable ? (byte)40 : (byte)0,
                        alpha: 255);
                content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue(mediaType: "image/png");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request
            });
        }

        private static string ReadQueryValue(Uri uri, string name)
        {
            foreach (string item in uri.Query.TrimStart('?').Split('&'))
            {
                string[] pair = item.Split('=', count: 2);
                if (pair.Length == 2 && pair[0].Equals(name, StringComparison.Ordinal))
                    return Uri.UnescapeDataString(pair[1]);
            }

            throw new InvalidOperationException(message: $"Missing {name} query value.");
        }
    }
}
