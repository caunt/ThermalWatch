using System.Collections.Immutable;
using System.Net;
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

    private static NotificationCandidateEngine CreateEngine(
        HttpMessageHandler handler,
        IMemoryCache cache,
        HttpMessageHandler? nearbyHandler = null)
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
            DefaultOptions(),
            gibsClient,
            nearbyClient,
            new FixedTimeProvider(s_observedAt.AddHours(1)));
    }

    private static NotificationOptions DefaultOptions() =>
        new(
            NotifyExistingOnStartup: true,
            ClusterRadiusKilometers: 5,
            ClusterTimeWindow: TimeSpan.FromMinutes(minutes: 90),
            SeenRetention: TimeSpan.FromHours(hours: 48),
            PreviewRetryWindow: TimeSpan.Zero,
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
}
