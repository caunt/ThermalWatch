using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using ThermalWatch.Core;

namespace ThermalWatch.Tests;

public sealed class GibsClientLandCoverTests
{
    [Fact]
    public async Task GetLandCoverAsyncSamplesNearbyPixelsAndReusesCachedTiles()
    {
        var handler = new LandCoverHandler(PngTestData.CreateIndexedSolid(
            width: 512,
            height: 512,
            red: 33,
            green: 138,
            blue: 33));
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new(uriString: "https://gibs.example.test/")
        };
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
        var client = new GibsClient(httpClient, cache, NullLogger<GibsClient>.Instance);
        Anomaly detection = Detection(latitude: 55.737840, longitude: 38.421440);

        GibsLandCoverResult first = await client.GetLandCoverAsync([detection], builtUpProximityKilometers: 2, TestContext.Current.CancellationToken);
        int requestCount = handler.RequestCount;
        GibsLandCoverResult second = await client.GetLandCoverAsync([detection], builtUpProximityKilometers: 2, TestContext.Current.CancellationToken);

        Assert.True(first.IsAvailable);
        Assert.Equal(2024, first.Year);
        Assert.True(first.SampledClasses.Length > 1);
        Assert.All(first.SampledClasses, landCoverClass => Assert.Equal(1, landCoverClass));
        Assert.False(first.HasBuiltUpWithinProximity);
        Assert.Equal(first.IsAvailable, second.IsAvailable);
        Assert.Equal(first.Year, second.Year);
        Assert.Equal(first.SampledClasses.ToArray(), second.SampledClasses.ToArray());
        Assert.Equal(first.HasBuiltUpWithinProximity, second.HasBuiltUpWithinProximity);
        Assert.Equal(requestCount, handler.RequestCount);
    }

    [Fact]
    public async Task GetLandCoverAsyncReportsBuiltUpFromNearbySampleSet()
    {
        var handler = new LandCoverHandler(PngTestData.CreateIndexedSolid(
            width: 512,
            height: 512,
            red: 255,
            green: 0,
            blue: 0));
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new(uriString: "https://gibs.example.test/")
        };
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
        var client = new GibsClient(httpClient, cache, NullLogger<GibsClient>.Instance);

        GibsLandCoverResult result = await client.GetLandCoverAsync(
            [Detection(latitude: 55.737840, longitude: 38.421440)],
            builtUpProximityKilometers: 2,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsAvailable);
        Assert.True(result.SampledClasses.Length > 1);
        Assert.All(result.SampledClasses, landCoverClass => Assert.Equal(13, landCoverClass));
        Assert.True(result.HasBuiltUpWithinProximity);
    }

    [Theory]
    [InlineData("rgba")]
    [InlineData("wrong-size")]
    [InlineData("transparent")]
    [InlineData("unknown-color")]
    [InlineData("truncated")]
    public async Task GetLandCoverAsyncRejectsInvalidTilePng(string variant)
    {
        byte[] png = variant switch
        {
            "rgba" => PngTestData.CreateSolidRgba(
                width: 512,
                height: 512,
                red: 33,
                green: 138,
                blue: 33,
                alpha: 255),
            "wrong-size" => PngTestData.CreateIndexedSolid(
                width: 256,
                height: 512,
                red: 33,
                green: 138,
                blue: 33),
            "transparent" => PngTestData.CreateIndexedSolid(
                width: 512,
                height: 512,
                red: 33,
                green: 138,
                blue: 33,
                alpha: 0),
            "unknown-color" => PngTestData.CreateIndexedSolid(
                width: 512,
                height: 512,
                red: 1,
                green: 2,
                blue: 3),
            "truncated" => PngTestData.CreateIndexedSolid(
                width: 512,
                height: 512,
                red: 33,
                green: 138,
                blue: 33)[..^12],
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, message: "Unknown PNG variant.")
        };
        var handler = new LandCoverHandler(png);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new(uriString: "https://gibs.example.test/")
        };
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
        var client = new GibsClient(httpClient, cache, NullLogger<GibsClient>.Instance);

        GibsLandCoverResult result = await client.GetLandCoverAsync(
            [Detection(latitude: 55.737840, longitude: 38.421440)],
            builtUpProximityKilometers: 2,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsAvailable);
    }

    private static Anomaly Detection(double latitude, double longitude) =>
        new(
            Id: "detection",
            CountryCode: "RUS",
            Source: "VIIRS_SNPP_NRT",
            Satellite: "Suomi-NPP",
            Instrument: "VIIRS",
            latitude,
            longitude,
            new(year: 2026, month: 7, day: 19, hour: 12, minute: 0, second: 0, TimeSpan.Zero),
            DayNight: "D",
            BrightnessKelvin: 330,
            SecondaryBrightnessKelvin: 300,
            FrpMegawatts: 100,
            ScanKilometers: 0.4,
            TrackKilometers: 0.4,
            ConfidenceRaw: "n",
            ConfidencePercent: null,
            ConfidenceCategory: "nominal",
            Version: "2.0NRT",
            GoogleMapsUrl: $"https://www.google.com/maps?q={latitude},{longitude}");

    private sealed class LandCoverHandler(byte[] pngBytes) : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            HttpContent content;
            if (request.RequestUri!.AbsolutePath.EndsWith(value: "/all.xml", StringComparison.Ordinal))
            {
                content = new StringContent(
                    content: "<Domains><Domain>2024-01-01</Domain></Domains>",
                    Encoding.UTF8,
                    mediaType: "application/xml");
            }
            else
            {
                content = new ByteArrayContent(pngBytes);
                content.Headers.ContentType = new MediaTypeHeaderValue(mediaType: "image/png");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request
            });
        }
    }
}
