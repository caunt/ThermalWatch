using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ThermalWatch.Core;
using ThermalWatch.Viewer;

namespace ThermalWatch.Tests;

public sealed class ViewerNotificationDiagnosticEndpointTests
{
    private static readonly DateTimeOffset s_now = new(
        year: 2026,
        month: 7,
        day: 23,
        hour: 9,
        minute: 0,
        second: 0,
        TimeSpan.Zero);

    [Fact]
    public async Task EndpointReturnsActiveClusterAndEveryCriterion()
    {
        await using WebApplication app = await CreateAppAsync();
        using HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client.GetAsync(
            requestUri: "/api/viewer/notification-diagnostics/first",
            TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("first", body.RootElement.GetProperty(propertyName: "selectedAnomalyId").GetString());
        Assert.Equal(2, body.RootElement.GetProperty(propertyName: "detectionCount").GetInt32());
        Assert.Equal(7, body.RootElement.GetProperty(propertyName: "criteria").GetArrayLength());
        Assert.True(body.RootElement.GetProperty(propertyName: "isEligible").GetBoolean());
        JsonElement nearbyFeature = Assert.Single(
            body.RootElement.GetProperty(propertyName: "nearbyFeatures").EnumerateArray());
        Assert.Equal("Nearby workshop", nearbyFeature.GetProperty(propertyName: "name").GetString());
        Assert.Equal("https://www.openstreetmap.org/node/123", nearbyFeature
            .GetProperty(propertyName: "openStreetMapUrl")
            .GetString());
        Assert.Equal(
            ["first", "second"],
            body.RootElement.GetProperty(propertyName: "memberIds")
                .EnumerateArray()
                .Select(item => item.GetString())
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task EndpointReturnsNotFoundForAnomalyOutsideCurrentSnapshot()
    {
        await using WebApplication app = await CreateAppAsync();
        using HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client.GetAsync(
            requestUri: "/api/viewer/notification-diagnostics/missing",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EligibleClusterEndpointReturnsRepresentativeSummary()
    {
        await using WebApplication app = await CreateAppAsync();
        using HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client.GetAsync(
            requestUri: "/api/viewer/eligible-notification-clusters",
            TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, body.RootElement.GetProperty(propertyName: "evaluatedClusterCount").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty(propertyName: "eligibleClusterCount").GetInt32());
        Assert.True(body.RootElement.TryGetProperty(propertyName: "snapshotGeneratedAtUtc", out _));
        JsonElement cluster = Assert.Single(
            body.RootElement.GetProperty(propertyName: "clusters").EnumerateArray());
        Assert.Equal("second", cluster.GetProperty(propertyName: "representativeId").GetString());
        Assert.Equal("RUS", cluster.GetProperty(propertyName: "countryCode").GetString());
        Assert.Equal("VIIRS_SNPP_NRT", cluster.GetProperty(propertyName: "source").GetString());
        Assert.Equal(50, cluster.GetProperty(propertyName: "latitude").GetDouble());
        Assert.Equal(30.02, cluster.GetProperty(propertyName: "longitude").GetDouble());
        Assert.Equal(2, cluster.GetProperty(propertyName: "detectionCount").GetInt32());
    }

    [Fact]
    public async Task EligibleClusterEndpointReturnsEmptyWarmingSnapshot()
    {
        await using WebApplication app = await CreateAppAsync(publishSnapshot: false);
        using HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client.GetAsync(
            requestUri: "/api/viewer/eligible-notification-clusters",
            TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, body.RootElement.GetProperty(propertyName: "evaluatedClusterCount").GetInt32());
        Assert.Equal(0, body.RootElement.GetProperty(propertyName: "eligibleClusterCount").GetInt32());
        Assert.Empty(body.RootElement.GetProperty(propertyName: "clusters").EnumerateArray());
    }

    private static async Task<WebApplication> CreateAppAsync(bool publishSnapshot = true)
    {
        var noRequests = new NotFoundHandler();
        var nearbyRequests = new NearbyHandler();
        var timeProvider = new FixedTimeProvider(s_now);
        var firmsOptions = new FirmsOptions(
            MapKey: new string('a', count: 32),
            CountryCodes: ["RUS"],
            PollInterval: TimeSpan.FromMinutes(minutes: 5),
            ActiveWindow: TimeSpan.FromHours(hours: 24),
            RequestTimeout: TimeSpan.FromSeconds(seconds: 45),
            MaxConcurrency: 4);
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        builder.Services.AddMemoryCache(options => options.SizeLimit = 64 * 1024 * 1024);
        builder.Services.AddSingleton(new ViewerOptions(GoogleMapsApiKey: null));
        builder.Services.AddSingleton(firmsOptions);
        builder.Services.AddSingleton<TimeProvider>(timeProvider);
        builder.Services.AddSingleton<AnomalySnapshotStore>();
        builder.Services.AddSingleton(DefaultNotificationOptions());
        builder.Services.AddSingleton(serviceProvider => new GibsClient(
            new HttpClient(noRequests) { BaseAddress = new(uriString: "https://gibs.example.test/") },
            serviceProvider.GetRequiredService<IMemoryCache>(),
            NullLogger<GibsClient>.Instance));
        builder.Services.AddSingleton(serviceProvider => new NearbyFeatureClient(
            new HttpClient(nearbyRequests) { BaseAddress = new(uriString: "https://overpass.example.test/api/") },
            serviceProvider.GetRequiredService<IMemoryCache>(),
            NullLogger<NearbyFeatureClient>.Instance));
        builder.Services.AddSingleton<NotificationCandidateEngine>();
        builder.Services.AddSingleton(serviceProvider => new GibsMapTileClient(
            new HttpClient(noRequests) { BaseAddress = new(uriString: "https://gibs.example.test/") },
            serviceProvider.GetRequiredService<IMemoryCache>(),
            NullLogger<GibsMapTileClient>.Instance));

        WebApplication app = builder.Build();
        app.MapThermalWatchViewer();
        if (publishSnapshot)
        {
            AnomalySnapshotStore store = app.Services.GetRequiredService<AnomalySnapshotStore>();
            store.Publish([
                SegmentRefreshResult.Success(
                    new(CountryCode: "RUS", Source: "VIIRS_SNPP_NRT"),
                    attemptedAtUtc: s_now,
                    completedAtUtc: s_now,
                    [
                        CreateAnomaly(id: "first", longitude: 30, frpMegawatts: 100),
                        CreateAnomaly(id: "second", longitude: 30.02, frpMegawatts: 200)
                    ],
                    IngestionModes.Country)
            ]);
        }

        await app.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        return app;
    }

    private static NotificationOptions DefaultNotificationOptions() =>
        new(
            SendExistingOnStartup: false,
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

    private static Anomaly CreateAnomaly(string id, double longitude, double frpMegawatts) =>
        new(
            id,
            CountryCode: "RUS",
            Source: "VIIRS_SNPP_NRT",
            Satellite: "N",
            Instrument: "VIIRS",
            Latitude: 50,
            longitude,
            s_now.AddMinutes(minutes: -30),
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
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class NearbyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            const string json = """
                {
                  "elements": [
                    {
                      "type": "node",
                      "id": 123,
                      "lat": 50,
                      "lon": 30.001,
                      "tags": { "name": "Nearby workshop" }
                    }
                  ]
                }
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, mediaType: "application/json")
            });
        }
    }
}
