using System.Net;
using System.Runtime.CompilerServices;
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

namespace ThermalWatch.Tests.Viewer;

/// <summary>Verifies public notification diagnostics against the current snapshot.</summary>
public sealed class ViewerNotificationDiagnosticEndpointTests
{
    private static readonly DateTimeOffset Now = new(year: 2026, month: 7, day: 23, hour: 9, minute: 0, second: 0, TimeSpan.Zero);

    /// <summary>Checks the empty response before the first publication.</summary>
    [Fact]
    public async Task EligibleClusterEndpointReturnsEmptyWarmingSnapshot()
    {
        WebApplication app = await CreateAppAsync(publishSnapshot: false);

        await using ConfiguredAsyncDisposable applicationLifetime = app.ConfigureAwait(continueOnCapturedContext: true);

        using HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(uriString: "/api/viewer/eligible-notification-clusters", UriKind.Relative), TestContext.Current.CancellationToken);

        using JsonDocument body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken),
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected: 0, body.RootElement.GetProperty(propertyName: "evaluatedClusterCount").GetInt32());
        Assert.Equal(expected: 0, body.RootElement.GetProperty(propertyName: "eligibleClusterCount").GetInt32());
        Assert.Equal(expected: 0, body.RootElement.GetProperty(propertyName: "clusters").GetArrayLength());
    }

    /// <summary>Checks the representative and membership of an eligible cluster.</summary>
    [Fact]
    public async Task EligibleClusterEndpointReturnsRepresentativeSummary()
    {
        WebApplication app = await CreateAppAsync();

        await using ConfiguredAsyncDisposable applicationLifetime = app.ConfigureAwait(continueOnCapturedContext: true);

        using HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(uriString: "/api/viewer/eligible-notification-clusters", UriKind.Relative), TestContext.Current.CancellationToken);

        using JsonDocument body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken),
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected: 1, body.RootElement.GetProperty(propertyName: "evaluatedClusterCount").GetInt32());
        Assert.Equal(expected: 1, body.RootElement.GetProperty(propertyName: "eligibleClusterCount").GetInt32());
        Assert.Equal(Now, body.RootElement.GetProperty(propertyName: "snapshotGeneratedAtUtc").GetDateTimeOffset());

        JsonElement[] clusters = [.. body.RootElement.GetProperty(propertyName: "clusters").EnumerateArray()];
        JsonElement cluster = Assert.Single(clusters);

        Assert.Equal(expected: "second", cluster.GetProperty(propertyName: "representativeId").GetString());
        Assert.Equal(expected: "RUS", cluster.GetProperty(propertyName: "countryCode").GetString());
        Assert.Equal(expected: "VIIRS_SNPP_NRT", cluster.GetProperty(propertyName: "source").GetString());
        Assert.Equal(expected: 50, cluster.GetProperty(propertyName: "latitude").GetDouble());
        Assert.Equal(expected: 30.02, cluster.GetProperty(propertyName: "longitude").GetDouble());
        Assert.Equal(expected: 200, cluster.GetProperty(propertyName: "frpMegawatts").GetDouble());
        Assert.Equal(expected: 300, cluster.GetProperty(propertyName: "totalFrpMegawatts").GetDouble());
        Assert.Equal(expected: 2, cluster.GetProperty(propertyName: "detectionCount").GetInt32());
        Assert.Equal(
            ["first", "second"],
            Assert.IsType<string[]>(cluster.GetProperty(propertyName: "memberIds").Deserialize<string[]>()).Order(StringComparer.Ordinal),
            StringComparer.Ordinal
        );
    }

    /// <summary>Checks all criteria and the bounded nearby-feature list.</summary>
    [Fact]
    public async Task EndpointReturnsActiveClusterAndEveryCriterion()
    {
        WebApplication app = await CreateAppAsync();

        await using ConfiguredAsyncDisposable applicationLifetime = app.ConfigureAwait(continueOnCapturedContext: true);

        using HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(uriString: "/api/viewer/notification-diagnostics/first", UriKind.Relative), TestContext.Current.CancellationToken);

        using JsonDocument body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken),
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected: "first", body.RootElement.GetProperty(propertyName: "selectedAnomalyId").GetString());
        Assert.Equal(expected: 2, body.RootElement.GetProperty(propertyName: "detectionCount").GetInt32());
        Assert.Equal(expected: 300, body.RootElement.GetProperty(propertyName: "totalFrpMegawatts").GetDouble());
        Assert.Equal(expected: 9, body.RootElement.GetProperty(propertyName: "criteria").GetArrayLength());
        Assert.True(body.RootElement.GetProperty(propertyName: "isEligible").GetBoolean());

        JsonElement[] nearbyFeatures =
        [
            .. body.RootElement.GetProperty(propertyName: "nearbyFeatures").EnumerateArray()
        ];

        Assert.Equal(expected: 25, nearbyFeatures.Length);
        Assert.Equal(expected: "Nearby workshop 1", nearbyFeatures[0].GetProperty(propertyName: "name").GetString());
        Assert.Equal(expected: "https://www.openstreetmap.org/node/1", nearbyFeatures[0].GetProperty(propertyName: "openStreetMapUrl").GetString());
        Assert.Equal(expected: 25, nearbyFeatures[nearbyFeatures.Length - 1].GetProperty(propertyName: "osmId").GetInt64());
        Assert.Equal(
            ["first", "second"],
            Assert.IsType<string[]>(body.RootElement.GetProperty(propertyName: "memberIds").Deserialize<string[]>()).Order(StringComparer.Ordinal),
            StringComparer.Ordinal
        );
    }

    /// <summary>Checks that diagnostics cannot select an inactive anomaly.</summary>
    [Fact]
    public async Task EndpointReturnsNotFoundForAnomalyOutsideCurrentSnapshot()
    {
        WebApplication app = await CreateAppAsync();

        await using ConfiguredAsyncDisposable applicationLifetime = app.ConfigureAwait(continueOnCapturedContext: true);

        using HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(uriString: "/api/viewer/notification-diagnostics/missing", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static Anomaly CreateAnomaly(string id, double longitude, double frpMegawatts)
    {
        return new(
            id,
            CountryCode: "RUS",
            Source: "VIIRS_SNPP_NRT",
            Satellite: "N",
            Instrument: "VIIRS",
            Latitude: 50,
            longitude,
            Now.AddMinutes(minutes: -30),
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
            new Uri($"https://example.test/{id}")
        );
    }

    private static async Task<WebApplication> CreateAppAsync(bool publishSnapshot = true)
    {
        var timeProvider = new FixedTimeProvider(Now);

        var firmsOptions = new FirmsOptions(
            new string(c: 'a', count: 32),
            ["RUS"],
            TimeSpan.FromMinutes(minutes: 5),
            TimeSpan.FromHours(hours: 24),
            TimeSpan.FromSeconds(seconds: 45),
            MaximumConcurrency: 4
        );

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        IWebHostBuilder webHost = builder.WebHost.UseTestServer();
        webHost = webHost.ConfigureServices(
            services => services = services
            .ConfigureHttpJsonOptions(options => options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
            .AddMemoryCache(options => options.SizeLimit = 64 * 1024 * 1024)
            .AddSingleton(new ViewerOptions(GoogleMapsApiKey: null))
            .AddSingleton(firmsOptions)
            .AddSingleton<TimeProvider>(timeProvider)
            .AddSingleton<AnomalySnapshotStore>()
            .AddSingleton(DefaultNotificationOptions())
            .AddKeyedSingleton(
                serviceKey: "gibs",
                (provider, key) => new HttpClient(new NotFoundHandler()) { BaseAddress = new(uriString: "https://gibs.example.test/") }
            )
            .AddKeyedSingleton(
                serviceKey: "nearby",
                (provider, key) => new HttpClient(new NearbyHandler()) { BaseAddress = new(uriString: "https://overpass.example.test/api/") }
            )
            .AddSingleton(
            serviceProvider => new GibsClient(
                serviceProvider.GetRequiredKeyedService<HttpClient>(serviceKey: "gibs"),
                serviceProvider.GetRequiredService<IMemoryCache>(),
                NullLogger<GibsClient>.Instance
            )
        )
        .AddSingleton(
            serviceProvider => new NearbyFeatureClient(
                serviceProvider.GetRequiredKeyedService<HttpClient>(serviceKey: "nearby"),
                serviceProvider.GetRequiredService<IMemoryCache>(),
                NullLogger<NearbyFeatureClient>.Instance
            )
        )
        .AddSingleton<NotificationCandidateEngine>()
        .AddSingleton(
            serviceProvider => new GibsMapTileClient(
                serviceProvider.GetRequiredKeyedService<HttpClient>(serviceKey: "gibs"),
                serviceProvider.GetRequiredService<IMemoryCache>(),
                serviceProvider.GetRequiredService<TimeProvider>(),
                NullLogger<GibsMapTileClient>.Instance
            )
        )
        );

        WebApplication app = builder.Build();
        Assert.NotEmpty(app.MapThermalWatchViewer().DataSources);

        if (publishSnapshot)
        {
            AnomalySnapshotStore store = app.Services.GetRequiredService<AnomalySnapshotStore>();

            AnomalySnapshot published = store.Publish(
                [
                SegmentRefreshResult.Success(
                    new(CountryCode: "RUS", Source: "VIIRS_SNPP_NRT"),
                    Now,
                    Now,
                    [
                        CreateAnomaly(id: "first", longitude: 30, frpMegawatts: 100),
                        CreateAnomaly(id: "second", longitude: 30.02, frpMegawatts: 200)
                    ],
                    IngestionModes.Country
                )
            ]
            );

            Assert.Same(published, store.Current);
        }

        await app.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        return app;
    }

    private static NotificationOptions DefaultNotificationOptions()
    {
        return new(
            SendExistingOnStartup: false,
            ClusterRadiusKilometers: 5,
            TimeSpan.FromMinutes(minutes: 90),
            TimeSpan.FromHours(hours: 48),
            HistoricalFrpFilterEnabled: false,
            new(
                new(WidthKilometers: 30, HeightKilometers: 20),
                new(WidthKilometers: 45, HeightKilometers: 30),
                PixelWidth: 900,
                PixelHeight: 600,
                LargeClusterMinimumDetections: 8,
                LargeClusterMinimumFrpMegawatts: 500,
                LargeClusterMinimumDiameterKilometers: 8
            ),
            new(
                Enabled: false,
                VegetationPercentThreshold: 50,
                BuiltUpProximityKilometers: 2,
                VegetationMaximumFrpMegawatts: 300,
                KeepHighFrpVegetation: false,
                KeepMultiSatelliteVegetation: false
            ),
            new(
                Enabled: true,
                MinimumFrpMegawatts: 50,
                MinimumClusterTotalFrpMegawatts: 50,
                MinimumThermalContrastKelvin: 20,
                MinimumClusterDetections: 2,
                MinimumModisConfidencePercent: 60,
                NotificationViirsConfidenceLevel.Nominal,
                RequireDaytime: true,
                RequirePreview: false
            )
        );
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }

    private sealed class NearbyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string json = JsonSerializer.Serialize(
                new
                {
                    elements = Enumerable.Range(start: 1, count: 26).Select(id => new { type = "node", id, lat = 50, lon = 30.001, tags = new { name = $"Nearby workshop {id}" } })
                }
            );

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, mediaType: "application/json") });
        }
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
