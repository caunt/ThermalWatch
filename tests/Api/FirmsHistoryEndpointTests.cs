using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using ThermalWatch.Api;
using ThermalWatch.Core;

namespace ThermalWatch.Tests.Api;

/// <summary>Verifies historical API serialization and inclusive date-range validation.</summary>
public sealed class FirmsHistoryEndpointTests
{
    private static readonly DateTimeOffset Now = new(year: 2026, month: 7, day: 27, hour: 12, minute: 0, second: 0, TimeSpan.Zero);

    /// <summary>Returns partial retained history with raw anomalies and cluster member identifiers.</summary>
    [Fact]
    public async Task EndpointReturnsPartialFullRetentionWithDailyAnomaliesAndClusters()
    {
        WebApplication app = await CreateAppAsync();

        await using ConfiguredAsyncDisposable applicationLifetime = app.ConfigureAwait(continueOnCapturedContext: true);

        FirmsHistoryStore store = app.Services.GetRequiredService<FirmsHistoryStore>();
        var date = new DateOnly(year: 2026, month: 7, day: 26);
        PublishDay(store, date);

        using HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(uriString: "/api/history", UriKind.Relative), TestContext.Current.CancellationToken);

        using JsonDocument body = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken),
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(body.RootElement.GetProperty(propertyName: "isReady").GetBoolean());
        Assert.Equal(expected: 31, body.RootElement.GetProperty(propertyName: "days").GetArrayLength());

        JsonElement[] days = [.. body.RootElement.GetProperty(propertyName: "days").EnumerateArray()];
        JsonElement day = days.Single(candidate => string.Equals(candidate.GetProperty(propertyName: "date").GetString(), b: "2026-07-26", StringComparison.Ordinal));

        Assert.Equal(expected: 2, day.GetProperty(propertyName: "anomalyCount").GetInt32());
        JsonElement[] clusters = [.. day.GetProperty(propertyName: "clusters").EnumerateArray()];
        JsonElement cluster = Assert.Single(clusters);
        Assert.Equal(expected: 2, cluster.GetProperty(propertyName: "detectionCount").GetInt32());
        Assert.Equal(["first", "second"], cluster.GetProperty(propertyName: "memberIds").Deserialize<string[]>(), StringComparer.Ordinal);
    }

    /// <summary>Accepts inclusive ranges and rejects reversed, expired, or duplicate bounds.</summary>
    [Fact]
    public async Task EndpointSupportsInclusiveDateRangeAndRejectsInvalidBounds()
    {
        WebApplication app = await CreateAppAsync();

        await using ConfiguredAsyncDisposable applicationLifetime = app.ConfigureAwait(continueOnCapturedContext: true);

        using HttpClient client = app.GetTestClient();

        using HttpResponseMessage selected = await client.GetAsync(new Uri(uriString: "/api/history?from=2026-07-25&to=2026-07-26", UriKind.Relative), TestContext.Current.CancellationToken);

        using JsonDocument selectedBody = await JsonDocument.ParseAsync(
            await selected.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken),
            cancellationToken: TestContext.Current.CancellationToken
        );

        using HttpResponseMessage reversed = await client.GetAsync(new Uri(uriString: "/api/history?from=2026-07-26&to=2026-07-25", UriKind.Relative), TestContext.Current.CancellationToken);

        using HttpResponseMessage expired = await client.GetAsync(new Uri(uriString: "/api/history?from=2026-06-26", UriKind.Relative), TestContext.Current.CancellationToken);

        using HttpResponseMessage duplicate = await client.GetAsync(new Uri(uriString: "/api/history?from=2026-07-25&from=2026-07-26", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        Assert.Equal(expected: 2, selectedBody.RootElement.GetProperty(propertyName: "days").GetArrayLength());
        Assert.Equal(expected: "2026-07-25", selectedBody.RootElement.GetProperty(propertyName: "fromDate").GetString());
        Assert.Equal(expected: "2026-07-26", selectedBody.RootElement.GetProperty(propertyName: "toDate").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, reversed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
    }

    private static Anomaly Anomaly(string id, DateOnly date, double longitude, double frp)
    {
        return new(
            id,
            CountryCode: "UKR",
            Source: "VIIRS_SNPP_NRT",
            Satellite: "Suomi-NPP",
            Instrument: "VIIRS",
            Latitude: 50,
            longitude,
            new DateTimeOffset(date.ToDateTime(new(hour: 12, minute: 0), DateTimeKind.Unspecified), TimeSpan.Zero),
            DayNight: "D",
            BrightnessKelvin: 330,
            SecondaryBrightnessKelvin: 300,
            frp,
            ScanKilometers: 0.4,
            TrackKilometers: 0.4,
            ConfidenceRaw: "n",
            ConfidencePercent: null,
            ConfidenceCategory: "nominal",
            Version: "2.0NRT",
            new Uri($"https://example.test/{id}")
        );
    }

    private static async Task<WebApplication> CreateAppAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        IWebHostBuilder webHost = builder.WebHost.UseTestServer();

        FirmsOptions options = new(
            new string(c: 'A', count: 32),
            ["UKR"],
            TimeSpan.FromMinutes(minutes: 5),
            TimeSpan.FromHours(hours: 24),
            TimeSpan.FromSeconds(seconds: 45),
            MaximumConcurrency: 4
        );

        webHost = webHost.ConfigureServices(
            services => services = services
            .AddSingleton(options)
            .AddSingleton(ApplicationConfiguration.ParseNotificationOptions(name => null))
            .AddSingleton<TimeProvider>(new FakeTimeProvider(Now))
            .AddSingleton<FirmsHistoryStore>()
            .ConfigureHttpJsonOptions(json => json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase)
        );
        WebApplication app = builder.Build();
        Assert.NotEmpty(app.MapFirmsHistory().DataSources);
        await app.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        return app;
    }

    private static void PublishDay(FirmsHistoryStore store, DateOnly date)
    {
        SegmentRefreshResult[] results =
        [
            Success(source: "MODIS_NRT", []),
            Success(source: "VIIRS_SNPP_NRT", [Anomaly(id: "first", date, longitude: 30, frp: 100)]),
            Success(source: "VIIRS_NOAA20_NRT", [Anomaly(id: "second", date, longitude: 30.01, frp: 200)]),
            Success(source: "VIIRS_NOAA21_NRT", [])
        ];

        FirmsHistory published = store.Publish(date, dayCount: 1, results);
        Assert.Same(published, store.Current);
    }

    private static SegmentRefreshResult Success(string source, IEnumerable<Anomaly> anomalies)
    {
        return SegmentRefreshResult.Success(new(CountryCode: "UKR", source), Now, Now, [.. anomalies], IngestionModes.Country);
    }
}
