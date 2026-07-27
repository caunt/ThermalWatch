using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ThermalWatch.Api;
using ThermalWatch.Core;

namespace ThermalWatch.Tests;

public sealed class FirmsHistoryEndpointTests
{
    private static readonly DateTimeOffset s_now = new(
        year: 2026,
        month: 7,
        day: 27,
        hour: 12,
        minute: 0,
        second: 0,
        TimeSpan.Zero);

    [Fact]
    public async Task EndpointReturnsPartialFullRetentionWithDailyAnomaliesAndClusters()
    {
        await using WebApplication app = await CreateAppAsync();
        FirmsHistoryStore store = app.Services.GetRequiredService<FirmsHistoryStore>();
        var date = new DateOnly(year: 2026, month: 7, day: 26);
        PublishDay(store, date);
        using HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client.GetAsync(
            requestUri: "/api/history",
            TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(body.RootElement.GetProperty(propertyName: "isReady").GetBoolean());
        Assert.Equal(31, body.RootElement.GetProperty(propertyName: "days").GetArrayLength());
        JsonElement day = body.RootElement.GetProperty(propertyName: "days")
            .EnumerateArray()
            .Single(candidate => string.Equals(
                a: candidate.GetProperty(propertyName: "date").GetString(),
                b: "2026-07-26",
                StringComparison.Ordinal));
        Assert.Equal(2, day.GetProperty(propertyName: "anomalyCount").GetInt32());
        JsonElement cluster = Assert.Single(day.GetProperty(propertyName: "clusters").EnumerateArray());
        Assert.Equal(2, cluster.GetProperty(propertyName: "detectionCount").GetInt32());
        Assert.Equal(
            ["first", "second"],
            cluster.GetProperty(propertyName: "memberIds").EnumerateArray().Select(item => item.GetString()),
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task EndpointSupportsInclusiveDateRangeAndRejectsInvalidBounds()
    {
        await using WebApplication app = await CreateAppAsync();
        using HttpClient client = app.GetTestClient();

        using HttpResponseMessage selected = await client.GetAsync(
            requestUri: "/api/history?from=2026-07-25&to=2026-07-26",
            TestContext.Current.CancellationToken);
        using var selectedBody = JsonDocument.Parse(await selected.Content.ReadAsStreamAsync(
            TestContext.Current.CancellationToken));
        using HttpResponseMessage reversed = await client.GetAsync(
            requestUri: "/api/history?from=2026-07-26&to=2026-07-25",
            TestContext.Current.CancellationToken);
        using HttpResponseMessage expired = await client.GetAsync(
            requestUri: "/api/history?from=2026-06-26",
            TestContext.Current.CancellationToken);
        using HttpResponseMessage duplicate = await client.GetAsync(
            requestUri: "/api/history?from=2026-07-25&from=2026-07-26",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        Assert.Equal(2, selectedBody.RootElement.GetProperty(propertyName: "days").GetArrayLength());
        Assert.Equal("2026-07-25", selectedBody.RootElement.GetProperty(propertyName: "fromDate").GetString());
        Assert.Equal("2026-07-26", selectedBody.RootElement.GetProperty(propertyName: "toDate").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, reversed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
    }

    private static async Task<WebApplication> CreateAppAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        FirmsOptions options = new(
            MapKey: new string('A', count: 32),
            CountryCodes: ["UKR"],
            PollInterval: TimeSpan.FromMinutes(minutes: 5),
            ActiveWindow: TimeSpan.FromHours(hours: 24),
            RequestTimeout: TimeSpan.FromSeconds(seconds: 45),
            MaxConcurrency: 4);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(ApplicationConfiguration.ParseNotificationOptions(_ => null));
        builder.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(s_now));
        builder.Services.AddSingleton<FirmsHistoryStore>();
        builder.Services.ConfigureHttpJsonOptions(json =>
            json.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        WebApplication app = builder.Build();
        app.MapFirmsHistory();
        await app.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        return app;
    }

    private static void PublishDay(FirmsHistoryStore store, DateOnly date)
    {
        SegmentRefreshResult[] results =
        [
            Success(source: "MODIS_NRT", anomalies: []),
            Success(source: "VIIRS_SNPP_NRT", anomalies: [Anomaly(id: "first", date: date, longitude: 30, frp: 100)]),
            Success(source: "VIIRS_NOAA20_NRT", anomalies: [Anomaly(id: "second", date: date, longitude: 30.01, frp: 200)]),
            Success(source: "VIIRS_NOAA21_NRT", anomalies: [])
        ];
        store.Publish(startDate: date, dayCount: 1, results);
    }

    private static SegmentRefreshResult Success(string source, IEnumerable<Anomaly> anomalies) =>
        SegmentRefreshResult.Success(
            new(CountryCode: "UKR", source),
            s_now,
            s_now,
            [.. anomalies],
            IngestionModes.Country);

    private static Anomaly Anomaly(string id, DateOnly date, double longitude, double frp) =>
        new(
            id,
            CountryCode: "UKR",
            Source: "VIIRS_SNPP_NRT",
            Satellite: "Suomi-NPP",
            Instrument: "VIIRS",
            Latitude: 50,
            longitude,
            new DateTimeOffset(
                date.ToDateTime(new(hour: 12, minute: 0), DateTimeKind.Unspecified),
                TimeSpan.Zero),
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
            GoogleMapsUrl: $"https://example.test/{id}");
}
