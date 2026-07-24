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
    public async Task DiagnoseAsyncBuildsTransitiveClusterAndDoesNotConsumeAutomaticCandidates()
    {
        var handler = new NotFoundHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
        NotificationCandidateEngine engine = CreateEngine(handler, cache);
        AnomalySnapshot snapshot = Snapshot(
            Detection(id: "first", longitude: 30, frpMegawatts: 100),
            Detection(id: "bridge", longitude: 30.03, frpMegawatts: 200),
            Detection(id: "last", longitude: 30.06, frpMegawatts: 150));

        NotificationDiagnostic diagnostic = Assert.IsType<NotificationDiagnostic>(
            await engine.DiagnoseAsync(
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
        NotificationAutomaticProcessingResult processing = await engine.ProcessAutomaticAsync(
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
        Assert.Equal(1, processing.Summary.AcceptedClusterCount);
    }

    [Fact]
    public async Task DiagnoseAsyncReturnsNullForAnomalyOutsideTheSnapshot()
    {
        var handler = new NotFoundHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
        NotificationCandidateEngine engine = CreateEngine(handler, cache);

        NotificationDiagnostic? diagnostic = await engine.DiagnoseAsync(
            Snapshot(Detection(id: "present", longitude: 30, frpMegawatts: 100)),
            anomalyId: "missing",
            TestContext.Current.CancellationToken);

        Assert.Null(diagnostic);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task DiagnoseAsyncLooksUpNearbyFeaturesForSelectedAnomalyInsteadOfRepresentative()
    {
        var gibsHandler = new NotFoundHandler();
        var nearbyHandler = new NearbyHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
        NotificationCandidateEngine engine = CreateEngine(gibsHandler, cache, nearbyHandler);
        AnomalySnapshot snapshot = Snapshot(
            Detection(id: "selected", longitude: 30, frpMegawatts: 100),
            Detection(id: "representative", longitude: 30.02, frpMegawatts: 200));

        NotificationDiagnostic diagnostic = Assert.IsType<NotificationDiagnostic>(
            await engine.DiagnoseAsync(
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
            Detection(id: "lower", longitude: 30, frpMegawatts: 100),
            Detection(id: "higher", longitude: 30.02, frpMegawatts: 200));

        await automaticEngine.ProcessAutomaticAsync(
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
            Detection(id: "low", longitude: 30, frpMegawatts: 100),
            Detection(id: "low-context", longitude: 30.01, frpMegawatts: 90),
            Detection(id: "highest", longitude: 31, frpMegawatts: 300),
            Detection(id: "highest-context", longitude: 31.01, frpMegawatts: 290),
            Detection(id: "middle", longitude: 32, frpMegawatts: 200),
            Detection(id: "middle-context", longitude: 32.01, frpMegawatts: 190));

        ManualNotificationCandidates manual = await manualEngine.PrepareManualAsync(
            manualSnapshot,
            requestedCount: 1,
            TestContext.Current.CancellationToken);

        Assert.Single(manual.Selected);
        string manualQuery = Assert.Single(manualNearby.Queries);
        Assert.Contains("around:2000,50.000000,31.000000", manualQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EligibleClusterQueryFiltersOrdersAndDoesNotConsumeAutomaticLifecycle()
    {
        var gibsHandler = new NotFoundHandler();
        var nearbyHandler = new NearbyHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
        NotificationOptions options = DefaultOptions() with { NotifyExistingOnStartup = false };
        NotificationCandidateEngine engine = CreateEngine(
            gibsHandler,
            cache,
            nearbyHandler,
            options);
        AnomalySnapshot snapshot = Snapshot(
            Detection(id: "low", longitude: 30, frpMegawatts: 100),
            Detection(id: "low-context", longitude: 30.01, frpMegawatts: 90),
            Detection(id: "highest", longitude: 31, frpMegawatts: 300),
            Detection(id: "highest-context", longitude: 31.01, frpMegawatts: 290),
            Detection(id: "middle", longitude: 32, frpMegawatts: 200),
            Detection(id: "middle-context", longitude: 32.01, frpMegawatts: 190),
            Detection(id: "filtered-singleton", longitude: 33, frpMegawatts: 500));

        EligibleNotificationClusters result = await engine.GetEligibleClustersAsync(
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
        NotificationAutomaticProcessingResult processing = await engine.ProcessAutomaticAsync(
            snapshot,
            (_, _) =>
            {
                deliveryCount++;
                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(snapshot.Items.Length, processing.Summary.StartupBaselineDetectionCount);
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
            Detection(id: "first", longitude: 30, frpMegawatts: 100),
            Detection(id: "second", longitude: 30.02, frpMegawatts: 200));

        EligibleNotificationClusters unavailable = await engine.GetEligibleClustersAsync(
            snapshot,
            TestContext.Current.CancellationToken);
        handler.IsPreviewAvailable = true;
        EligibleNotificationClusters available = await engine.GetEligibleClustersAsync(
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
            Detection(id: "first", longitude: 30, frpMegawatts: 100),
            Detection(id: "second", longitude: 30.02, frpMegawatts: 200));

        EligibleNotificationClusters result = await engine.GetEligibleClustersAsync(
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
            Detection(id: "first", longitude: 30, frpMegawatts: 100),
            Detection(id: "second", longitude: 30.02, frpMegawatts: 200));
        int deliveryCount = 0;

        NotificationAutomaticProcessingResult unavailable = await engine.ProcessAutomaticAsync(
            snapshot,
            (_, _) =>
            {
                deliveryCount++;
                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken);
        handler.IsPreviewAvailable = true;
        NotificationAutomaticProcessingResult available = await engine.ProcessAutomaticAsync(
            snapshot,
            (_, _) =>
            {
                deliveryCount++;
                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken);
        NotificationAutomaticProcessingResult delivered = await engine.ProcessAutomaticAsync(
            snapshot,
            (_, _) =>
            {
                deliveryCount++;
                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, unavailable.Summary.AcceptedClusterCount);
        Assert.Equal(1, unavailable.Summary.RejectionCount(NotificationRejectionReason.PreviewUnavailable));
        Assert.Equal(1, available.Summary.AcceptedClusterCount);
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
            Detection(id: "first", longitude: 30, frpMegawatts: 100),
            Detection(id: "second", longitude: 30.02, frpMegawatts: 200));
        PreparedNotificationCandidate? delivered = null;

        NotificationAutomaticProcessingResult result = await engine.ProcessAutomaticAsync(
            snapshot,
            (candidate, _) =>
            {
                delivered = candidate;
                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken);

        Assert.False(Assert.IsType<PreparedNotificationCandidate>(delivered).Preview.IsAvailable);
        Assert.Equal(1, result.Summary.AcceptedClusterCount);
        Assert.Equal(0, result.Summary.RejectedClusterCount);
    }

    [Fact]
    public async Task AutomaticRetriesTransientDeliveryThroughNextSnapshotEvaluation()
    {
        var handler = new NotFoundHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
        NotificationCandidateEngine engine = CreateEngine(handler, cache);
        AnomalySnapshot snapshot = Snapshot(
            Detection(id: "first", longitude: 30, frpMegawatts: 100),
            Detection(id: "second", longitude: 30.02, frpMegawatts: 200));
        int deliveryCount = 0;

        NotificationAutomaticProcessingResult failed = await engine.ProcessAutomaticAsync(
            snapshot,
            (_, _) =>
            {
                deliveryCount++;
                return Task.FromResult(NotificationDeliveryOutcome.RetryLater);
            },
            TestContext.Current.CancellationToken);
        NotificationAutomaticProcessingResult retried = await engine.ProcessAutomaticAsync(
            snapshot,
            (_, _) =>
            {
                deliveryCount++;
                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken);
        NotificationAutomaticProcessingResult delivered = await engine.ProcessAutomaticAsync(
            snapshot,
            (_, _) =>
            {
                deliveryCount++;
                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken);

        Assert.True(failed.ContinueProcessing);
        Assert.Equal(1, failed.Summary.SendFailureCount);
        Assert.Equal(1, retried.Summary.AcceptedClusterCount);
        Assert.Equal(1, delivered.Summary.DuplicateEpisodeCount);
        Assert.Equal(2, deliveryCount);
    }

    [Fact]
    public async Task AutomaticPreservesStartupBaselineUntilClusterGainsDetection()
    {
        var handler = new NotFoundHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
        NotificationOptions options = DefaultOptions() with { NotifyExistingOnStartup = false };
        NotificationCandidateEngine engine = CreateEngine(handler, cache, options: options);
        Anomaly first = Detection(id: "first", longitude: 30, frpMegawatts: 100);
        Anomaly second = Detection(id: "second", longitude: 30.02, frpMegawatts: 200);
        AnomalySnapshot baseline = Snapshot(first, second);
        int deliveryCount = 0;

        NotificationAutomaticProcessingResult primed = await engine.ProcessAutomaticAsync(
            baseline,
            (_, _) =>
            {
                deliveryCount++;
                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken);
        NotificationAutomaticProcessingResult unchanged = await engine.ProcessAutomaticAsync(
            baseline,
            (_, _) =>
            {
                deliveryCount++;
                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken);
        NotificationAutomaticProcessingResult extended = await engine.ProcessAutomaticAsync(
            Snapshot(
                first,
                second,
                Detection(id: "later", longitude: 30.01, frpMegawatts: 150)),
            (_, _) =>
            {
                deliveryCount++;
                return Task.FromResult(NotificationDeliveryOutcome.Delivered);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, primed.Summary.StartupBaselineDetectionCount);
        Assert.Equal(1, unchanged.Summary.StartupSuppressedClusterCount);
        Assert.Equal(1, extended.Summary.AcceptedClusterCount);
        Assert.Equal(1, deliveryCount);
    }

    [Fact]
    public async Task DiagnoseExplainsLaterSnapshotReevaluationForUnavailableRequiredPreview()
    {
        var handler = new NotFoundHandler();
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
        NotificationOptions options = DefaultOptions() with
        {
            Visibility = DefaultOptions().Visibility with { RequirePreview = true }
        };
        NotificationCandidateEngine engine = CreateEngine(handler, cache, options: options);
        AnomalySnapshot snapshot = Snapshot(
            Detection(id: "first", longitude: 30, frpMegawatts: 100),
            Detection(id: "second", longitude: 30.02, frpMegawatts: 200));

        NotificationDiagnostic diagnostic = Assert.IsType<NotificationDiagnostic>(
            await engine.DiagnoseAsync(
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
            NotifyExistingOnStartup: true,
            ClusterRadiusKilometers: 5,
            ClusterTimeWindow: TimeSpan.FromMinutes(minutes: 90),
            DeliveredRetention: TimeSpan.FromHours(hours: 48),
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

    private static AnomalySnapshot Snapshot(params Anomaly[] detections) =>
        new(
            s_observedAt.AddHours(1),
            ActiveWindowHours: 24,
            IsReady: true,
            IsPartiallyStale: false,
            ConfiguredCountries: ["RUS"],
            Sources: [],
            Count: detections.Length,
            Items: [.. detections]);

    private static Anomaly Detection(string id, double longitude, double frpMegawatts) =>
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
