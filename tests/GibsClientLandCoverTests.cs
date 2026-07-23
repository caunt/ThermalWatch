using System.Buffers.Binary;
using System.IO.Compression;
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
        var handler = new LandCoverHandler(CreateUniformIndexedPng(red: 33, green: 138, blue: 33));
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
        var handler = new LandCoverHandler(CreateUniformIndexedPng(red: 255, green: 0, blue: 0));
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

    private static byte[] CreateUniformIndexedPng(byte red, byte green, byte blue)
    {
        const int size = 512;
        byte[] raw = new byte[(size + 1) * size];
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(raw);

        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header[..4], size);
        BinaryPrimitives.WriteUInt32BigEndian(header.Slice(start: 4, length: 4), size);
        header[8] = 8;
        header[9] = 3;
        WriteChunk(png, "IHDR"u8, header);
        WriteChunk(png, "PLTE"u8, [red, green, blue]);
        WriteChunk(png, "IDAT"u8, compressed.ToArray());
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> value = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(value, (uint)data.Length);
        stream.Write(value);
        stream.Write(type);
        stream.Write(data);

        uint crc = uint.MaxValue;
        UpdateCrc(ref crc, type);
        UpdateCrc(ref crc, data);
        BinaryPrimitives.WriteUInt32BigEndian(value, ~crc);
        stream.Write(value);
    }

    private static void UpdateCrc(ref uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte item in data)
        {
            crc ^= item;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
        }
    }

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
