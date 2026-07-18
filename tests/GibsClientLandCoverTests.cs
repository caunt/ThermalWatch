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
    public async Task GetLandCoverAsync_SamplesNearbyPixelsAndReusesCachedTiles()
    {
        var handler = new LandCoverHandler(CreateUniformIndexedPng(33, 138, 33));
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new("https://gibs.example.test/")
        };
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
        var client = new GibsClient(httpClient, cache, NullLogger<GibsClient>.Instance);
        var detection = Detection(55.737840, 38.421440);

        var first = await client.GetLandCoverAsync([detection], 2, TestContext.Current.CancellationToken);
        var requestCount = handler.RequestCount;
        var second = await client.GetLandCoverAsync([detection], 2, TestContext.Current.CancellationToken);

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
    public async Task GetLandCoverAsync_ReportsBuiltUpFromNearbySampleSet()
    {
        var handler = new LandCoverHandler(CreateUniformIndexedPng(255, 0, 0));
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new("https://gibs.example.test/")
        };
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
        var client = new GibsClient(httpClient, cache, NullLogger<GibsClient>.Instance);

        var result = await client.GetLandCoverAsync(
            [Detection(55.737840, 38.421440)],
            2,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsAvailable);
        Assert.True(result.SampledClasses.Length > 1);
        Assert.All(result.SampledClasses, landCoverClass => Assert.Equal(13, landCoverClass));
        Assert.True(result.HasBuiltUpWithinProximity);
    }

    private static Anomaly Detection(double latitude, double longitude) =>
        new(
            "detection",
            "RUS",
            "VIIRS_SNPP_NRT",
            "Suomi-NPP",
            "VIIRS",
            latitude,
            longitude,
            new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero),
            "D",
            330,
            300,
            100,
            0.4,
            0.4,
            "n",
            null,
            "nominal",
            "2.0NRT",
            $"https://www.google.com/maps?q={latitude},{longitude}");

    private static byte[] CreateUniformIndexedPng(byte red, byte green, byte blue)
    {
        const int size = 512;
        var raw = new byte[(size + 1) * size];
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(raw);

        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header[..4], size);
        BinaryPrimitives.WriteUInt32BigEndian(header.Slice(4, 4), size);
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

        var crc = uint.MaxValue;
        UpdateCrc(ref crc, type);
        UpdateCrc(ref crc, data);
        BinaryPrimitives.WriteUInt32BigEndian(value, ~crc);
        stream.Write(value);
    }

    private static void UpdateCrc(ref uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var item in data)
        {
            crc ^= item;
            for (var bit = 0; bit < 8; bit++)
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
            if (request.RequestUri!.AbsolutePath.EndsWith("/all.xml", StringComparison.Ordinal))
            {
                content = new StringContent(
                    "<Domains><Domain>2024-01-01</Domain></Domains>",
                    Encoding.UTF8,
                    "application/xml");
            }
            else
            {
                content = new ByteArrayContent(pngBytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request
            });
        }
    }
}
