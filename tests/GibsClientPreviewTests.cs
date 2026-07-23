using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using ThermalWatch.Core;

namespace ThermalWatch.Tests;

public sealed class GibsClientPreviewTests
{
    private const string Noaa20Base = "VIIRS_NOAA20_CorrectedReflectance_TrueColor";
    private const string Noaa20Overlay = "VIIRS_NOAA20_Thermal_Anomalies_375m_All";
    private const string Noaa21Base = "VIIRS_NOAA21_CorrectedReflectance_TrueColor";
    private const string SuomiNppBase = "VIIRS_SNPP_CorrectedReflectance_TrueColor";
    private const string TerraBase = "MODIS_Terra_CorrectedReflectance_TrueColor";
    private const string TerraOverlay = "MODIS_Terra_Thermal_Anomalies_All";
    private const string AquaBase = "MODIS_Aqua_CorrectedReflectance_TrueColor";

    [Fact]
    public async Task GetPreviewAsync_UsesAndCachesUsableRepresentativeBase()
    {
        var handler = new PreviewHandler(_ => CreateSolidPng(64, 64, 30, 80, 40, 255));
        using var cache = CreateCache();
        var client = CreateClient(handler, cache);
        var anomaly = Detection("VIIRS_NOAA20_NRT", "NOAA-20");

        var first = await client.GetPreviewAsync(
            anomaly,
            Dimensions,
            TestContext.Current.CancellationToken);
        var requestCount = handler.RequestCount;
        var second = await client.GetPreviewAsync(
            anomaly,
            Dimensions,
            TestContext.Current.CancellationToken);

        Assert.True(first.IsAvailable);
        Assert.Equal(new GibsPreviewSource("VIIRS_NOAA20_NRT", "NOAA-20", "VIIRS"), first.BaseSource);
        Assert.Equal([Noaa20Base], handler.ProbedBaseLayers);
        Assert.Equal($"{Noaa20Base},{Noaa20Overlay}", Assert.Single(handler.CompositeLayers));
        Assert.Same(first, second);
        Assert.Equal(requestCount, handler.RequestCount);
    }

    [Fact]
    public async Task GetPreviewAsync_FallsBackWithinSensorFamilyBeforeComposingOriginalOverlay()
    {
        var handler = new PreviewHandler(layer => layer == Noaa21Base
            ? CreateSolidPng(64, 64, 30, 80, 40, 255)
            : CreateSolidPng(64, 64, 0, 0, 0, 255));
        using var cache = CreateCache();
        var client = CreateClient(handler, cache);

        var preview = await client.GetPreviewAsync(
            Detection("VIIRS_NOAA20_NRT", "NOAA-20"),
            Dimensions,
            TestContext.Current.CancellationToken);

        Assert.True(preview.IsAvailable);
        Assert.Equal(new GibsPreviewSource("VIIRS_NOAA21_NRT", "NOAA-21", "VIIRS"), preview.BaseSource);
        Assert.Equal([Noaa20Base, Noaa21Base], handler.ProbedBaseLayers);
        Assert.Equal($"{Noaa21Base},{Noaa20Overlay}", Assert.Single(handler.CompositeLayers));
    }

    [Fact]
    public async Task GetPreviewAsync_TriesEverySameFamilyBaseBeforeCrossFamilyFallback()
    {
        var handler = new PreviewHandler(layer => layer == TerraBase
            ? CreateSolidPng(64, 64, 30, 80, 40, 255)
            : CreateSolidPng(64, 64, 0, 0, 0, 255));
        using var cache = CreateCache();
        var client = CreateClient(handler, cache);

        var preview = await client.GetPreviewAsync(
            Detection("VIIRS_NOAA20_NRT", "NOAA-20"),
            Dimensions,
            TestContext.Current.CancellationToken);

        Assert.True(preview.IsAvailable);
        Assert.Equal(new GibsPreviewSource("MODIS_NRT", "Terra", "MODIS"), preview.BaseSource);
        Assert.Equal(
            [Noaa20Base, Noaa21Base, SuomiNppBase, TerraBase],
            handler.ProbedBaseLayers);
        Assert.Equal($"{TerraBase},{Noaa20Overlay}", Assert.Single(handler.CompositeLayers));
    }

    [Fact]
    public async Task GetPreviewAsync_PrefersOtherModisPlatformBeforeViirsFallbacks()
    {
        var handler = new PreviewHandler(layer => layer == AquaBase
            ? CreateSolidPng(64, 64, 30, 80, 40, 255)
            : CreateSolidPng(64, 64, 0, 0, 0, 255));
        using var cache = CreateCache();
        var client = CreateClient(handler, cache);

        var preview = await client.GetPreviewAsync(
            Detection("MODIS_NRT", "Terra"),
            Dimensions,
            TestContext.Current.CancellationToken);

        Assert.True(preview.IsAvailable);
        Assert.Equal(new GibsPreviewSource("MODIS_NRT", "Aqua", "MODIS"), preview.BaseSource);
        Assert.Equal([TerraBase, AquaBase], handler.ProbedBaseLayers);
        Assert.Equal($"{AquaBase},{TerraOverlay}", Assert.Single(handler.CompositeLayers));
    }

    [Theory]
    [InlineData(2047, false)]
    [InlineData(2048, true)]
    public async Task GetPreviewAsync_RequiresHalfTheProbePixelsToBeUsable(
        int usablePixelCount,
        bool expectedAvailable)
    {
        var probe = CreateCoveragePng(usablePixelCount);
        var handler = new PreviewHandler(_ => probe);
        using var cache = CreateCache();
        var client = CreateClient(handler, cache);

        var preview = await client.GetPreviewAsync(
            Detection("VIIRS_NOAA20_NRT", "NOAA-20"),
            Dimensions,
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedAvailable, preview.IsAvailable);
    }

    [Theory]
    [InlineData(8, 255, false)]
    [InlineData(9, 255, true)]
    [InlineData(255, 8, false)]
    [InlineData(255, 9, true)]
    public async Task GetPreviewAsync_ClassifiesNearBlackAndTransparentProbePixels(
        byte color,
        byte alpha,
        bool expectedAvailable)
    {
        var probe = CreateSolidPng(64, 64, color, color, color, alpha);
        var handler = new PreviewHandler(_ => probe);
        using var cache = CreateCache();
        var client = CreateClient(handler, cache);

        var preview = await client.GetPreviewAsync(
            Detection("VIIRS_NOAA20_NRT", "NOAA-20"),
            Dimensions,
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedAvailable, preview.IsAvailable);
    }

    [Fact]
    public async Task GetPreviewAsync_RejectsUnsupportedProbePng()
    {
        var handler = new PreviewHandler(_ => CreateIndexedPng(64, 64));
        using var cache = CreateCache();
        var client = CreateClient(handler, cache);

        var preview = await client.GetPreviewAsync(
            Detection("VIIRS_NOAA20_NRT", "NOAA-20"),
            Dimensions,
            TestContext.Current.CancellationToken);

        Assert.False(preview.IsAvailable);
        Assert.Empty(handler.CompositeLayers);
    }

    [Fact]
    public async Task GetPreviewAsync_DoesNotCacheBlackProbeAndCanRecoverLater()
    {
        var usable = false;
        var handler = new PreviewHandler(_ => usable
            ? CreateSolidPng(64, 64, 30, 80, 40, 255)
            : CreateSolidPng(64, 64, 0, 0, 0, 255));
        using var cache = CreateCache();
        var client = CreateClient(handler, cache);
        var anomaly = Detection("VIIRS_NOAA20_NRT", "NOAA-20");

        var first = await client.GetPreviewAsync(
            anomaly,
            Dimensions,
            TestContext.Current.CancellationToken);
        usable = true;
        var second = await client.GetPreviewAsync(
            anomaly,
            Dimensions,
            TestContext.Current.CancellationToken);

        Assert.False(first.IsAvailable);
        Assert.True(second.IsAvailable);
        Assert.Equal(6, handler.ProbedBaseLayers.Count);
        Assert.Equal(Noaa20Base, handler.ProbedBaseLayers[^1]);
    }

    [Fact]
    public async Task GetPreviewAsync_RejectsAndDoesNotCacheMalformedCompositePng()
    {
        var validComposite = CreateSolidPng(900, 600, 30, 80, 40, 255);
        var truncatedComposite = validComposite[..^12];
        var handler = new PreviewHandler(
            _ => CreateSolidPng(64, 64, 30, 80, 40, 255),
            truncatedComposite);
        using var cache = CreateCache();
        var client = CreateClient(handler, cache);
        var anomaly = Detection("VIIRS_NOAA20_NRT", "NOAA-20");

        var first = await client.GetPreviewAsync(
            anomaly,
            Dimensions,
            TestContext.Current.CancellationToken);
        var second = await client.GetPreviewAsync(
            anomaly,
            Dimensions,
            TestContext.Current.CancellationToken);

        Assert.False(first.IsAvailable);
        Assert.False(second.IsAvailable);
        Assert.Equal(2, handler.CompositeLayers.Count);
    }

    [Fact]
    public async Task GetPreviewAsync_UsesPassMatchedNighttimeFallbackLayers()
    {
        const string noaa20NightBase = "VIIRS_NOAA20_Brightness_Temp_BandI5_Night";
        const string noaa21NightBase = "VIIRS_NOAA21_Brightness_Temp_BandI5_Night";
        var handler = new PreviewHandler(layer => layer == noaa21NightBase
            ? CreateSolidPng(64, 64, 30, 80, 40, 255)
            : CreateSolidPng(64, 64, 0, 0, 0, 255));
        using var cache = CreateCache();
        var client = CreateClient(handler, cache);

        var preview = await client.GetPreviewAsync(
            Detection("VIIRS_NOAA20_NRT", "NOAA-20") with { DayNight = "N" },
            Dimensions,
            TestContext.Current.CancellationToken);

        Assert.True(preview.IsAvailable);
        Assert.Equal([noaa20NightBase, noaa21NightBase], handler.ProbedBaseLayers);
        Assert.Equal($"{noaa21NightBase},{Noaa20Overlay}", Assert.Single(handler.CompositeLayers));
    }

    private static readonly GibsPreviewDimensions Dimensions = new(30, 20, 900, 600);

    private static MemoryCache CreateCache() =>
        new(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });

    private static GibsClient CreateClient(PreviewHandler handler, IMemoryCache cache)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new("https://gibs.example.test/")
        };
        return new(httpClient, cache, NullLogger<GibsClient>.Instance);
    }

    private static Anomaly Detection(string source, string satellite) =>
        new(
            "detection",
            "RUS",
            source,
            satellite,
            source == "MODIS_NRT" ? "MODIS" : "VIIRS",
            57.946080,
            60.061420,
            new(2026, 7, 23, 8, 18, 0, TimeSpan.Zero),
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
            "https://www.google.com/maps?q=57.946080,60.061420");

    private static byte[] CreateCoveragePng(int usablePixelCount) =>
        CreateRgbaPng(
            64,
            64,
            pixel => pixel < usablePixelCount
                ? ((byte)30, (byte)80, (byte)40, byte.MaxValue)
                : ((byte)0, (byte)0, (byte)0, byte.MaxValue));

    private static byte[] CreateSolidPng(
        int width,
        int height,
        byte red,
        byte green,
        byte blue,
        byte alpha) =>
        CreateRgbaPng(width, height, _ => (red, green, blue, alpha));

    private static byte[] CreateRgbaPng(
        int width,
        int height,
        Func<int, (byte Red, byte Green, byte Blue, byte Alpha)> pixelFactory)
    {
        var rowLength = width * 4;
        var raw = new byte[(rowLength + 1) * height];
        for (var row = 0; row < height; row++)
        {
            var rowOffset = row * (rowLength + 1);
            for (var column = 0; column < width; column++)
            {
                var color = pixelFactory(row * width + column);
                var pixelOffset = rowOffset + 1 + column * 4;
                raw[pixelOffset] = color.Red;
                raw[pixelOffset + 1] = color.Green;
                raw[pixelOffset + 2] = color.Blue;
                raw[pixelOffset + 3] = color.Alpha;
            }
        }

        return CreatePng(width, height, 6, raw, null);
    }

    private static byte[] CreateIndexedPng(int width, int height)
    {
        var raw = new byte[(width + 1) * height];
        return CreatePng(width, height, 3, raw, [30, 80, 40]);
    }

    private static byte[] CreatePng(
        int width,
        int height,
        byte colorType,
        byte[] raw,
        byte[]? palette)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(raw);

        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header[..4], (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.Slice(4, 4), (uint)height);
        header[8] = 8;
        header[9] = colorType;
        WriteChunk(png, "IHDR"u8, header);
        if (palette is not null)
            WriteChunk(png, "PLTE"u8, palette);
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

    private sealed class PreviewHandler(
        Func<string, byte[]> createProbePng,
        byte[]? compositePng = null) : HttpMessageHandler
    {
        private readonly byte[] _compositePng = compositePng
            ?? CreateSolidPng(900, 600, 30, 80, 40, 255);
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public List<string> ProbedBaseLayers { get; } = [];

        public List<string> CompositeLayers { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            HttpContent content;
            if (request.RequestUri!.AbsolutePath.EndsWith(".xml", StringComparison.Ordinal))
            {
                content = new StringContent(
                    "<Domains><Domain>2026-07-23</Domain></Domains>",
                    Encoding.UTF8,
                    "application/xml");
            }
            else
            {
                var layers = ReadQueryValue(request.RequestUri, "LAYERS");
                byte[] bytes;
                if (layers.Contains(',', StringComparison.Ordinal))
                {
                    CompositeLayers.Add(layers);
                    bytes = _compositePng;
                }
                else
                {
                    ProbedBaseLayers.Add(layers);
                    bytes = createProbePng(layers);
                }

                content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request
            });
        }

        private static string ReadQueryValue(Uri uri, string name)
        {
            foreach (var item in uri.Query.TrimStart('?').Split('&'))
            {
                var pair = item.Split('=', 2);
                if (pair.Length == 2 && pair[0].Equals(name, StringComparison.Ordinal))
                    return Uri.UnescapeDataString(pair[1]);
            }

            throw new InvalidOperationException($"Missing {name} query value.");
        }
    }
}
