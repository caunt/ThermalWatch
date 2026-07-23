using System.Collections.Immutable;
using System.Net;
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

    private static NotificationCandidateEngine CreateEngine(
        HttpMessageHandler handler,
        IMemoryCache cache)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new(uriString: "https://gibs.example.test/")
        };
        var gibsClient = new GibsClient(httpClient, cache, NullLogger<GibsClient>.Instance);
        return new(DefaultOptions(), gibsClient, new FixedTimeProvider(s_observedAt.AddHours(1)));
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
}
