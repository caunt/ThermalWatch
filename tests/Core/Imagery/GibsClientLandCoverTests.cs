using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

using ThermalWatch.Core;

namespace ThermalWatch.Tests.Core.Imagery;

/// <summary>Verifies land-cover tile validation, nearby sampling, and cache reuse.</summary>
public sealed class GibsClientLandCoverTests
{
    /// <summary>Rejects tiles with an unsupported format, dimensions, palette, or incomplete data.</summary>
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
            "rgba" => PngTestData.CreateSolidRgba(width: 512, height: 512, red: 33, green: 138, blue: 33, alpha: 255),
            "wrong-size" => PngTestData.CreateIndexedSolid(width: 256, height: 512, red: 33, green: 138, blue: 33),
            "transparent" => PngTestData.CreateIndexedSolid(width: 512, height: 512, red: 33, green: 138, blue: 33, alpha: 0),
            "unknown-color" => PngTestData.CreateIndexedSolid(width: 512, height: 512, red: 1, green: 2, blue: 3),
            "truncated" => PngTestData.CreateIndexedSolid(width: 512, height: 512, red: 33, green: 138, blue: 33)[..^12],
            _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, message: "Unknown PNG variant.")
        };

        using var handler = new LandCoverHandler(png);

        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new(uriString: "https://gibs.example.test/")
        };

        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });

        var client = new GibsClient(httpClient, cache, NullLogger<GibsClient>.Instance);

        GibsLandCoverResult result = await client.GetLandCoverAsync<Anomaly[]>([CreateAnomaly(latitude: 55.737840, longitude: 38.421440)], builtUpProximityKilometers: 2, TestContext.Current.CancellationToken);

        Assert.False(result.IsAvailable);
    }

    /// <summary>Detects built-up class pixels in the nearby sampling area.</summary>
    [Fact]
    public async Task GetLandCoverAsyncReportsBuiltUpFromNearbySampleSet()
    {
        using var handler = new LandCoverHandler(PngTestData.CreateIndexedSolid(width: 512, height: 512, red: 255, green: 0, blue: 0));

        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new(uriString: "https://gibs.example.test/")
        };

        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });

        var client = new GibsClient(httpClient, cache, NullLogger<GibsClient>.Instance);

        GibsLandCoverResult result = await client.GetLandCoverAsync<Anomaly[]>([CreateAnomaly(latitude: 55.737840, longitude: 38.421440)], builtUpProximityKilometers: 2, TestContext.Current.CancellationToken);

        Assert.True(result.IsAvailable);
        Assert.True(result.SampledClasses.Length > 1);

        foreach (byte landCoverClass in result.SampledClasses)
            Assert.Equal<byte>(expected: 13, landCoverClass);

        Assert.True(result.HasBuiltUpWithinProximity);
    }

    /// <summary>Samples multiple nearby pixels and reuses cached provider tiles.</summary>
    [Fact]
    public async Task GetLandCoverAsyncSamplesNearbyPixelsAndReusesCachedTiles()
    {
        using var handler = new LandCoverHandler(PngTestData.CreateIndexedSolid(width: 512, height: 512, red: 33, green: 138, blue: 33));

        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new(uriString: "https://gibs.example.test/")
        };

        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });

        var client = new GibsClient(httpClient, cache, NullLogger<GibsClient>.Instance);
        Anomaly anomaly = CreateAnomaly(latitude: 55.737840, longitude: 38.421440);

        GibsLandCoverResult first = await client.GetLandCoverAsync<Anomaly[]>([anomaly], builtUpProximityKilometers: 2, TestContext.Current.CancellationToken);
        int requestCount = handler.RequestCount;
        GibsLandCoverResult second = await client.GetLandCoverAsync<Anomaly[]>([anomaly], builtUpProximityKilometers: 2, TestContext.Current.CancellationToken);

        Assert.True(first.IsAvailable);
        Assert.Equal(expected: 2024, first.Year);
        Assert.True(first.SampledClasses.Length > 1);

        foreach (byte landCoverClass in first.SampledClasses)
            Assert.Equal<byte>(expected: 1, landCoverClass);

        Assert.False(first.HasBuiltUpWithinProximity);
        Assert.Equal(first.IsAvailable, second.IsAvailable);
        Assert.Equal(first.Year, second.Year);
        Assert.Equal(first.SampledClasses.ToArray(), second.SampledClasses.ToArray());
        Assert.Equal(first.HasBuiltUpWithinProximity, second.HasBuiltUpWithinProximity);
        Assert.Equal(requestCount, handler.RequestCount);
    }

    private static Anomaly CreateAnomaly(double latitude, double longitude)
    {
        return new(
            Id: "anomaly",
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
            new Uri($"https://www.google.com/maps?q={latitude},{longitude}")
        );
    }

    private sealed class LandCoverHandler(byte[] pngBytes) : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(Interlocked.Increment(ref _requestCount) > 0);
            HttpContent content;

            if (Assert.IsType<Uri>(request.RequestUri).AbsolutePath.EndsWith(value: "/all.xml", StringComparison.Ordinal))
            {
                content = new StringContent(content: "<Domains><Domain>2024-01-01</Domain></Domains>", Encoding.UTF8, mediaType: "application/xml");
            }
            else
            {
                content = new ByteArrayContent(pngBytes);
                content.Headers.ContentType = new MediaTypeHeaderValue(mediaType: "image/png");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }
    }
}
