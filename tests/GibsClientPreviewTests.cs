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
    public async Task GetPreviewAsyncUsesAndCachesUsableRepresentativeBase()
    {
        var handler = new PreviewHandler(_ => CreateSolidPng(width: 64, height: 64, red: 30, green: 80, blue: 40, alpha: 255));
        using MemoryCache cache = CreateCache();
        GibsClient client = CreateClient(handler, cache);
        Anomaly anomaly = Detection(source: "VIIRS_NOAA20_NRT", satellite: "NOAA-20");

        GibsPreview first = await client.GetPreviewAsync(
            anomaly,
            s_dimensions,
            TestContext.Current.CancellationToken);
        int requestCount = handler.RequestCount;
        GibsPreview second = await client.GetPreviewAsync(
            anomaly,
            s_dimensions,
            TestContext.Current.CancellationToken);

        Assert.True(first.IsAvailable);
        Assert.Equal(new GibsPreviewSource(FirmsSource: "VIIRS_NOAA20_NRT", Satellite: "NOAA-20", Instrument: "VIIRS"), first.BaseSource);
        Assert.Equal([Noaa20Base], handler.ProbedBaseLayers);
        Assert.Equal($"{Noaa20Base},{Noaa20Overlay}", Assert.Single(handler.CompositeLayers));
        Assert.Same(first, second);
        Assert.Equal(requestCount, handler.RequestCount);
    }

    [Fact]
    public async Task GetPreviewAsyncFallsBackWithinSensorFamilyBeforeComposingOriginalOverlay()
    {
        var handler = new PreviewHandler(layer => Noaa21Base.Equals(layer, StringComparison.Ordinal)
            ? CreateSolidPng(width: 64, height: 64, red: 30, green: 80, blue: 40, alpha: 255)
            : CreateSolidPng(width: 64, height: 64, red: 0, green: 0, blue: 0, alpha: 255));
        using MemoryCache cache = CreateCache();
        GibsClient client = CreateClient(handler, cache);

        GibsPreview preview = await client.GetPreviewAsync(
            Detection(source: "VIIRS_NOAA20_NRT", satellite: "NOAA-20"),
            s_dimensions,
            TestContext.Current.CancellationToken);

        Assert.True(preview.IsAvailable);
        Assert.Equal(new GibsPreviewSource(FirmsSource: "VIIRS_NOAA21_NRT", Satellite: "NOAA-21", Instrument: "VIIRS"), preview.BaseSource);
        Assert.Equal([Noaa20Base, Noaa21Base], handler.ProbedBaseLayers);
        Assert.Equal($"{Noaa21Base},{Noaa20Overlay}", Assert.Single(handler.CompositeLayers));
    }

    [Fact]
    public async Task GetPreviewAsyncTriesEverySameFamilyBaseBeforeCrossFamilyFallback()
    {
        var handler = new PreviewHandler(layer => TerraBase.Equals(layer, StringComparison.Ordinal)
            ? CreateSolidPng(width: 64, height: 64, red: 30, green: 80, blue: 40, alpha: 255)
            : CreateSolidPng(width: 64, height: 64, red: 0, green: 0, blue: 0, alpha: 255));
        using MemoryCache cache = CreateCache();
        GibsClient client = CreateClient(handler, cache);

        GibsPreview preview = await client.GetPreviewAsync(
            Detection(source: "VIIRS_NOAA20_NRT", satellite: "NOAA-20"),
            s_dimensions,
            TestContext.Current.CancellationToken);

        Assert.True(preview.IsAvailable);
        Assert.Equal(new GibsPreviewSource(FirmsSource: "MODIS_NRT", Satellite: "Terra", Instrument: "MODIS"), preview.BaseSource);
        Assert.Equal(
            [Noaa20Base, Noaa21Base, SuomiNppBase, TerraBase],
            handler.ProbedBaseLayers);
        Assert.Equal($"{TerraBase},{Noaa20Overlay}", Assert.Single(handler.CompositeLayers));
    }

    [Fact]
    public async Task GetPreviewAsyncPrefersOtherModisPlatformBeforeViirsFallbacks()
    {
        var handler = new PreviewHandler(layer => AquaBase.Equals(layer, StringComparison.Ordinal)
            ? CreateSolidPng(width: 64, height: 64, red: 30, green: 80, blue: 40, alpha: 255)
            : CreateSolidPng(width: 64, height: 64, red: 0, green: 0, blue: 0, alpha: 255));
        using MemoryCache cache = CreateCache();
        GibsClient client = CreateClient(handler, cache);

        GibsPreview preview = await client.GetPreviewAsync(
            Detection(source: "MODIS_NRT", satellite: "Terra"),
            s_dimensions,
            TestContext.Current.CancellationToken);

        Assert.True(preview.IsAvailable);
        Assert.Equal(new GibsPreviewSource(FirmsSource: "MODIS_NRT", Satellite: "Aqua", Instrument: "MODIS"), preview.BaseSource);
        Assert.Equal([TerraBase, AquaBase], handler.ProbedBaseLayers);
        Assert.Equal($"{AquaBase},{TerraOverlay}", Assert.Single(handler.CompositeLayers));
    }

    [Theory]
    [InlineData(2047, false)]
    [InlineData(2048, true)]
    public async Task GetPreviewAsyncRequiresHalfTheProbePixelsToBeUsable(
        int usablePixelCount,
        bool expectedAvailable)
    {
        byte[] probe = CreateCoveragePng(usablePixelCount);
        var handler = new PreviewHandler(_ => probe);
        using MemoryCache cache = CreateCache();
        GibsClient client = CreateClient(handler, cache);

        GibsPreview preview = await client.GetPreviewAsync(
            Detection(source: "VIIRS_NOAA20_NRT", satellite: "NOAA-20"),
            s_dimensions,
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedAvailable, preview.IsAvailable);
    }

    [Theory]
    [InlineData(8, 255, false)]
    [InlineData(9, 255, true)]
    [InlineData(255, 8, false)]
    [InlineData(255, 9, true)]
    public async Task GetPreviewAsyncClassifiesNearBlackAndTransparentProbePixels(
        byte color,
        byte alpha,
        bool expectedAvailable)
    {
        byte[] probe = CreateSolidPng(width: 64, height: 64, color, color, color, alpha);
        var handler = new PreviewHandler(_ => probe);
        using MemoryCache cache = CreateCache();
        GibsClient client = CreateClient(handler, cache);

        GibsPreview preview = await client.GetPreviewAsync(
            Detection(source: "VIIRS_NOAA20_NRT", satellite: "NOAA-20"),
            s_dimensions,
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedAvailable, preview.IsAvailable);
    }

    [Fact]
    public async Task GetPreviewAsyncRejectsUnsupportedProbePng()
    {
        var handler = new PreviewHandler(_ => CreateIndexedPng(width: 64, height: 64));
        using MemoryCache cache = CreateCache();
        GibsClient client = CreateClient(handler, cache);

        GibsPreview preview = await client.GetPreviewAsync(
            Detection(source: "VIIRS_NOAA20_NRT", satellite: "NOAA-20"),
            s_dimensions,
            TestContext.Current.CancellationToken);

        Assert.False(preview.IsAvailable);
        Assert.Empty(handler.CompositeLayers);
    }

    [Fact]
    public async Task GetPreviewAsyncDoesNotCacheBlackProbeAndCanRecoverLater()
    {
        bool usable = false;
        var handler = new PreviewHandler(_ => usable
            ? CreateSolidPng(width: 64, height: 64, red: 30, green: 80, blue: 40, alpha: 255)
            : CreateSolidPng(width: 64, height: 64, red: 0, green: 0, blue: 0, alpha: 255));
        using MemoryCache cache = CreateCache();
        GibsClient client = CreateClient(handler, cache);
        Anomaly anomaly = Detection(source: "VIIRS_NOAA20_NRT", satellite: "NOAA-20");

        GibsPreview first = await client.GetPreviewAsync(
            anomaly,
            s_dimensions,
            TestContext.Current.CancellationToken);
        usable = true;
        GibsPreview second = await client.GetPreviewAsync(
            anomaly,
            s_dimensions,
            TestContext.Current.CancellationToken);

        Assert.False(first.IsAvailable);
        Assert.True(second.IsAvailable);
        Assert.Equal(6, handler.ProbedBaseLayers.Count);
        Assert.Equal(Noaa20Base, handler.ProbedBaseLayers[^1]);
    }

    [Fact]
    public async Task GetPreviewAsyncRejectsAndDoesNotCacheMalformedCompositePng()
    {
        byte[] validComposite = CreateSolidPng(width: 900, height: 600, red: 30, green: 80, blue: 40, alpha: 255);
        byte[] truncatedComposite = validComposite[..^12];
        var handler = new PreviewHandler(
            _ => CreateSolidPng(width: 64, height: 64, red: 30, green: 80, blue: 40, alpha: 255),
            truncatedComposite);
        using MemoryCache cache = CreateCache();
        GibsClient client = CreateClient(handler, cache);
        Anomaly anomaly = Detection(source: "VIIRS_NOAA20_NRT", satellite: "NOAA-20");

        GibsPreview first = await client.GetPreviewAsync(
            anomaly,
            s_dimensions,
            TestContext.Current.CancellationToken);
        GibsPreview second = await client.GetPreviewAsync(
            anomaly,
            s_dimensions,
            TestContext.Current.CancellationToken);

        Assert.False(first.IsAvailable);
        Assert.False(second.IsAvailable);
        Assert.Equal(2, handler.CompositeLayers.Count);
    }

    [Fact]
    public async Task GetPreviewAsyncUsesPassMatchedNighttimeFallbackLayers()
    {
        const string noaa20NightBase = "VIIRS_NOAA20_Brightness_Temp_BandI5_Night";
        const string noaa21NightBase = "VIIRS_NOAA21_Brightness_Temp_BandI5_Night";
        var handler = new PreviewHandler(layer => noaa21NightBase.Equals(layer, StringComparison.Ordinal)
            ? CreateSolidPng(width: 64, height: 64, red: 30, green: 80, blue: 40, alpha: 255)
            : CreateSolidPng(width: 64, height: 64, red: 0, green: 0, blue: 0, alpha: 255));
        using MemoryCache cache = CreateCache();
        GibsClient client = CreateClient(handler, cache);

        GibsPreview preview = await client.GetPreviewAsync(
            Detection(source: "VIIRS_NOAA20_NRT", satellite: "NOAA-20") with { DayNight = "N" },
            s_dimensions,
            TestContext.Current.CancellationToken);

        Assert.True(preview.IsAvailable);
        Assert.Equal([noaa20NightBase, noaa21NightBase], handler.ProbedBaseLayers);
        Assert.Equal($"{noaa21NightBase},{Noaa20Overlay}", Assert.Single(handler.CompositeLayers));
    }

    private static readonly GibsPreviewDimensions s_dimensions = new(WidthKilometers: 30, HeightKilometers: 20, PixelWidth: 900, PixelHeight: 600);

    private static MemoryCache CreateCache() =>
        new(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });

    private static GibsClient CreateClient(PreviewHandler handler, IMemoryCache cache)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new(uriString: "https://gibs.example.test/")
        };
        return new(httpClient, cache, NullLogger<GibsClient>.Instance);
    }

    private static Anomaly Detection(string source, string satellite) =>
        new(
            Id: "detection",
            CountryCode: "RUS",
            source,
            satellite,
            "MODIS_NRT".Equals(source, StringComparison.Ordinal) ? "MODIS" : "VIIRS",
            Latitude: 57.946080,
            Longitude: 60.061420,
            new(year: 2026, month: 7, day: 23, hour: 8, minute: 18, second: 0, TimeSpan.Zero),
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
            GoogleMapsUrl: "https://www.google.com/maps?q=57.946080,60.061420");

    private static byte[] CreateCoveragePng(int usablePixelCount) =>
        CreateRgbaPng(
            width: 64,
            height: 64,
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
        int rowLength = width * 4;
        byte[] raw = new byte[(rowLength + 1) * height];
        for (int row = 0; row < height; row++)
        {
            int rowOffset = row * (rowLength + 1);
            for (int column = 0; column < width; column++)
            {
                (byte Red, byte Green, byte Blue, byte Alpha) color = pixelFactory(row * width + column);
                int pixelOffset = rowOffset + 1 + column * 4;
                raw[pixelOffset] = color.Red;
                raw[pixelOffset + 1] = color.Green;
                raw[pixelOffset + 2] = color.Blue;
                raw[pixelOffset + 3] = color.Alpha;
            }
        }

        return CreatePng(width, height, colorType: 6, raw, palette: null);
    }

    private static byte[] CreateIndexedPng(int width, int height)
    {
        byte[] raw = new byte[(width + 1) * height];
        return CreatePng(width, height, colorType: 3, raw, [30, 80, 40]);
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
        BinaryPrimitives.WriteUInt32BigEndian(header.Slice(start: 4, length: 4), (uint)height);
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

    private sealed class PreviewHandler(
        Func<string, byte[]> createProbePng,
        byte[]? compositePng = null) : HttpMessageHandler
    {
        private readonly byte[] _compositePng = compositePng
            ?? CreateSolidPng(width: 900, height: 600, red: 30, green: 80, blue: 40, alpha: 255);
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
            if (request.RequestUri!.AbsolutePath.EndsWith(value: ".xml", StringComparison.Ordinal))
            {
                content = new StringContent(
                    content: "<Domains><Domain>2026-07-23</Domain></Domains>",
                    Encoding.UTF8,
                    mediaType: "application/xml");
            }
            else
            {
                string layers = ReadQueryValue(request.RequestUri, name: "LAYERS");
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
                content.Headers.ContentType = new MediaTypeHeaderValue(mediaType: "image/png");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request
            });
        }

        private static string ReadQueryValue(Uri uri, string name)
        {
            foreach (string item in uri.Query.TrimStart('?').Split('&'))
            {
                string[] pair = item.Split('=', count: 2);
                if (pair.Length == 2 && pair[0].Equals(name, StringComparison.Ordinal))
                    return Uri.UnescapeDataString(pair[1]);
            }

            throw new InvalidOperationException(message: $"Missing {name} query value.");
        }
    }
}
