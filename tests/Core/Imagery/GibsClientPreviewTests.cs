using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

using ThermalWatch.Core;

namespace ThermalWatch.Tests.Core.Imagery;

/// <summary>Verifies preview validation, sensor fallback order, and cache behavior.</summary>
public sealed class GibsClientPreviewTests
{
    private const string AquaBase = "MODIS_Aqua_CorrectedReflectance_TrueColor";
    private const string Noaa20Base = "VIIRS_NOAA20_CorrectedReflectance_TrueColor";
    private const string Noaa20Overlay = "VIIRS_NOAA20_Thermal_Anomalies_375m_All";
    private const string Noaa21Base = "VIIRS_NOAA21_CorrectedReflectance_TrueColor";
    private const string SuomiNppBase = "VIIRS_SNPP_CorrectedReflectance_TrueColor";
    private const string TerraBase = "MODIS_Terra_CorrectedReflectance_TrueColor";
    private const string TerraOverlay = "MODIS_Terra_Thermal_Anomalies_All";

    private static readonly GibsPreviewDimensions Dimensions = new(WidthKilometers: 30, HeightKilometers: 20, PixelWidth: 900, PixelHeight: 600);

    /// <summary>Provides malformed-dimension and truncated probe cases.</summary>
    public static TheoryData<bool> InvalidProbeCases => [false, true];

    /// <summary>Provides probe coverage immediately below and at fifty percent.</summary>
    public static TheoryData<int, bool> ProbeCoverageCases => new()
    {
        { 2047, false },
        { 2048, true }
    };

    /// <summary>Provides exact color and opacity usability boundaries.</summary>
    public static TheoryData<byte, byte, bool> ProbePixelCases => new()
    {
        { 8, 255, false },
        { 9, 255, true },
        { 255, 8, false },
        { 255, 9, true }
    };

    /// <summary>Accepts supported RGB probe imagery.</summary>
    [Fact]
    public async Task GetPreviewAsyncAcceptsRgbProbePixels()
    {
        using var handler = new PreviewHandler(layer => PngTestData.CreateSolidRgb(width: 64, height: 64, red: 30, green: 80, blue: 40));

        using MemoryCache cache = CreateCache();

        using var httpClient = new HttpClient(handler);

        GibsClient client = CreateClient(httpClient, cache);

        GibsPreview preview = await client.GetPreviewAsync(CreateAnomaly(source: "VIIRS_NOAA20_NRT", satellite: "NOAA-20"), Dimensions, TestContext.Current.CancellationToken);

        Assert.True(preview.IsAvailable);
    }

    /// <summary>Classifies pixels at the near-black and transparency boundaries.</summary>
    [Theory]
    [MemberData(nameof(ProbePixelCases))]
    public async Task GetPreviewAsyncClassifiesNearBlackAndTransparentProbePixels(byte color, byte alpha, bool expectedAvailable)
    {
        byte[] probe = PngTestData.CreateSolidRgba(width: 64, height: 64, color, color, color, alpha);

        using var handler = new PreviewHandler(layer => probe);

        using MemoryCache cache = CreateCache();

        using var httpClient = new HttpClient(handler);

        GibsClient client = CreateClient(httpClient, cache);

        GibsPreview preview = await client.GetPreviewAsync(CreateAnomaly(source: "VIIRS_NOAA20_NRT", satellite: "NOAA-20"), Dimensions, TestContext.Current.CancellationToken);

        Assert.Equal(expectedAvailable, preview.IsAvailable);
    }

    /// <summary>Reprobes previously unusable imagery so later availability can recover.</summary>
    [Fact]
    public async Task GetPreviewAsyncDoesNotCacheBlackProbeAndCanRecoverLater()
    {
        bool usable = false;

        using var handler = new PreviewHandler(
            layer => usable
            ? PngTestData.CreateSolidRgba(width: 64, height: 64, red: 30, green: 80, blue: 40, alpha: 255)
            : PngTestData.CreateSolidRgba(width: 64, height: 64, red: 0, green: 0, blue: 0, alpha: 255)
        );

        using MemoryCache cache = CreateCache();

        using var httpClient = new HttpClient(handler);

        GibsClient client = CreateClient(httpClient, cache);
        Anomaly anomaly = CreateAnomaly(source: "VIIRS_NOAA20_NRT", satellite: "NOAA-20");

        GibsPreview first = await client.GetPreviewAsync(anomaly, Dimensions, TestContext.Current.CancellationToken);

        usable = true;

        GibsPreview second = await client.GetPreviewAsync(anomaly, Dimensions, TestContext.Current.CancellationToken);

        Assert.False(first.IsAvailable);
        Assert.True(second.IsAvailable);
        Assert.Equal(expected: 6, handler.ProbedBaseLayers.Count);
        Assert.Equal(Noaa20Base, handler.ProbedBaseLayers[handler.ProbedBaseLayers.Count - 1]);
    }

    /// <summary>Tries another VIIRS platform before leaving the representative's sensor family.</summary>
    [Fact]
    public async Task GetPreviewAsyncFallsBackWithinSensorFamilyBeforeComposingOriginalOverlay()
    {
        using var handler = new PreviewHandler(
            layer => Noaa21Base.Equals(layer, StringComparison.Ordinal)
            ? PngTestData.CreateSolidRgba(width: 64, height: 64, red: 30, green: 80, blue: 40, alpha: 255)
            : PngTestData.CreateSolidRgba(width: 64, height: 64, red: 0, green: 0, blue: 0, alpha: 255)
        );

        using MemoryCache cache = CreateCache();

        using var httpClient = new HttpClient(handler);

        GibsClient client = CreateClient(httpClient, cache);

        GibsPreview preview = await client.GetPreviewAsync(CreateAnomaly(source: "VIIRS_NOAA20_NRT", satellite: "NOAA-20"), Dimensions, TestContext.Current.CancellationToken);

        Assert.True(preview.IsAvailable);
        Assert.Equal(new GibsPreviewSource(FirmsSource: "VIIRS_NOAA21_NRT", Satellite: "NOAA-21", Instrument: "VIIRS"), preview.BaseSource);
        Assert.Equal([Noaa20Base, Noaa21Base], handler.ProbedBaseLayers);
        Assert.Equal($"{Noaa21Base},{Noaa20Overlay}", Assert.Single(handler.CompositeLayers));
    }

    /// <summary>Tries another MODIS platform before VIIRS imagery.</summary>
    [Fact]
    public async Task GetPreviewAsyncPrefersOtherModisPlatformBeforeViirsFallbacks()
    {
        using var handler = new PreviewHandler(
            layer => AquaBase.Equals(layer, StringComparison.Ordinal)
            ? PngTestData.CreateSolidRgba(width: 64, height: 64, red: 30, green: 80, blue: 40, alpha: 255)
            : PngTestData.CreateSolidRgba(width: 64, height: 64, red: 0, green: 0, blue: 0, alpha: 255)
        );

        using MemoryCache cache = CreateCache();

        using var httpClient = new HttpClient(handler);

        GibsClient client = CreateClient(httpClient, cache);

        GibsPreview preview = await client.GetPreviewAsync(CreateAnomaly(source: "MODIS_NRT", satellite: "Terra"), Dimensions, TestContext.Current.CancellationToken);

        Assert.True(preview.IsAvailable);
        Assert.Equal(new GibsPreviewSource(FirmsSource: "MODIS_NRT", Satellite: "Aqua", Instrument: "MODIS"), preview.BaseSource);
        Assert.Equal([TerraBase, AquaBase], handler.ProbedBaseLayers);
        Assert.Equal($"{AquaBase},{TerraOverlay}", Assert.Single(handler.CompositeLayers));
    }

    /// <summary>Rejects malformed composite imagery and does not cache its failure.</summary>
    [Fact]
    public async Task GetPreviewAsyncRejectsAndDoesNotCacheMalformedCompositePng()
    {
        byte[] validComposite = PngTestData.CreateSolidRgba(width: 900, height: 600, red: 30, green: 80, blue: 40, alpha: 255);
        byte[] truncatedComposite = validComposite[..^12];

        using var handler = new PreviewHandler(layer => PngTestData.CreateSolidRgba(width: 64, height: 64, red: 30, green: 80, blue: 40, alpha: 255), truncatedComposite);

        using MemoryCache cache = CreateCache();

        using var httpClient = new HttpClient(handler);

        GibsClient client = CreateClient(httpClient, cache);
        Anomaly anomaly = CreateAnomaly(source: "VIIRS_NOAA20_NRT", satellite: "NOAA-20");

        GibsPreview first = await client.GetPreviewAsync(anomaly, Dimensions, TestContext.Current.CancellationToken);

        GibsPreview second = await client.GetPreviewAsync(anomaly, Dimensions, TestContext.Current.CancellationToken);

        Assert.False(first.IsAvailable);
        Assert.False(second.IsAvailable);
        Assert.Equal(expected: 2, handler.CompositeLayers.Count);
    }

    /// <summary>Rejects incorrect dimensions and truncated probe data before composition.</summary>
    [Theory]
    [MemberData(nameof(InvalidProbeCases))]
    public async Task GetPreviewAsyncRejectsInvalidProbeStructure(bool truncate)
    {
        byte[] probe = PngTestData.CreateSolidRgba(truncate ? 64 : 63, height: 64, red: 30, green: 80, blue: 40, alpha: 255);

        if (truncate)
            probe = probe[..^12];

        using var handler = new PreviewHandler(layer => probe);

        using MemoryCache cache = CreateCache();

        using var httpClient = new HttpClient(handler);

        GibsClient client = CreateClient(httpClient, cache);

        GibsPreview preview = await client.GetPreviewAsync(CreateAnomaly(source: "VIIRS_NOAA20_NRT", satellite: "NOAA-20"), Dimensions, TestContext.Current.CancellationToken);

        Assert.False(preview.IsAvailable);
        Assert.Empty(handler.CompositeLayers);
    }

    /// <summary>Rejects unsupported indexed probe imagery.</summary>
    [Fact]
    public async Task GetPreviewAsyncRejectsUnsupportedProbePng()
    {
        using var handler = new PreviewHandler(layer => PngTestData.CreateIndexedSolid(width: 64, height: 64, red: 30, green: 80, blue: 40));

        using MemoryCache cache = CreateCache();

        using var httpClient = new HttpClient(handler);

        GibsClient client = CreateClient(httpClient, cache);

        GibsPreview preview = await client.GetPreviewAsync(CreateAnomaly(source: "VIIRS_NOAA20_NRT", satellite: "NOAA-20"), Dimensions, TestContext.Current.CancellationToken);

        Assert.False(preview.IsAvailable);
        Assert.Empty(handler.CompositeLayers);
    }

    /// <summary>Requests enlarged markers for thermal anomaly overlays.</summary>
    [Fact]
    public async Task GetPreviewAsyncRequestsFiveTimesLargerThermalAnomalyMarkers()
    {
        using var handler = new PreviewHandler(layer => PngTestData.CreateSolidRgba(width: 64, height: 64, red: 30, green: 80, blue: 40, alpha: 255));

        using MemoryCache cache = CreateCache();

        using var httpClient = new HttpClient(handler);

        GibsClient client = CreateClient(httpClient, cache);

        GibsPreview preview = await client.GetPreviewAsync(CreateAnomaly(source: "VIIRS_NOAA20_NRT", satellite: "NOAA-20"), Dimensions, TestContext.Current.CancellationToken);

        Assert.True(preview.IsAvailable);
        Assert.Equal(expected: "default,size25", Assert.Single(handler.CompositeStyles));
    }

    /// <summary>Requires at least half the probe pixels to contain usable imagery.</summary>
    [Theory]
    [MemberData(nameof(ProbeCoverageCases))]
    public async Task GetPreviewAsyncRequiresHalfTheProbePixelsToBeUsable(int usablePixelCount, bool expectedAvailable)
    {
        byte[] probe = CreateCoveragePng(usablePixelCount);

        using var handler = new PreviewHandler(layer => probe);

        using MemoryCache cache = CreateCache();

        using var httpClient = new HttpClient(handler);

        GibsClient client = CreateClient(httpClient, cache);

        GibsPreview preview = await client.GetPreviewAsync(CreateAnomaly(source: "VIIRS_NOAA20_NRT", satellite: "NOAA-20"), Dimensions, TestContext.Current.CancellationToken);

        Assert.Equal(expectedAvailable, preview.IsAvailable);
    }

    /// <summary>Exhausts the original sensor family before trying other sensors.</summary>
    [Fact]
    public async Task GetPreviewAsyncTriesEverySameFamilyBaseBeforeCrossFamilyFallback()
    {
        using var handler = new PreviewHandler(
            layer => TerraBase.Equals(layer, StringComparison.Ordinal)
            ? PngTestData.CreateSolidRgba(width: 64, height: 64, red: 30, green: 80, blue: 40, alpha: 255)
            : PngTestData.CreateSolidRgba(width: 64, height: 64, red: 0, green: 0, blue: 0, alpha: 255)
        );

        using MemoryCache cache = CreateCache();

        using var httpClient = new HttpClient(handler);

        GibsClient client = CreateClient(httpClient, cache);

        GibsPreview preview = await client.GetPreviewAsync(CreateAnomaly(source: "VIIRS_NOAA20_NRT", satellite: "NOAA-20"), Dimensions, TestContext.Current.CancellationToken);

        Assert.True(preview.IsAvailable);
        Assert.Equal(new GibsPreviewSource(FirmsSource: "MODIS_NRT", Satellite: "Terra", Instrument: "MODIS"), preview.BaseSource);
        Assert.Equal([Noaa20Base, Noaa21Base, SuomiNppBase, TerraBase], handler.ProbedBaseLayers);
        Assert.Equal($"{TerraBase},{Noaa20Overlay}", Assert.Single(handler.CompositeLayers));
    }

    /// <summary>Composes and caches usable representative imagery with a visible ruler.</summary>
    [Fact]
    public async Task GetPreviewAsyncUsesAndCachesUsableRepresentativeBase()
    {
        using var handler = new PreviewHandler(layer => PngTestData.CreateSolidRgba(width: 64, height: 64, red: 30, green: 80, blue: 40, alpha: 255));

        using MemoryCache cache = CreateCache();

        using var httpClient = new HttpClient(handler);

        GibsClient client = CreateClient(httpClient, cache);
        Anomaly anomaly = CreateAnomaly(source: "VIIRS_NOAA20_NRT", satellite: "NOAA-20");

        GibsPreview first = await client.GetPreviewAsync(anomaly, Dimensions, TestContext.Current.CancellationToken);

        int requestCount = handler.RequestCount;

        GibsPreview second = await client.GetPreviewAsync(anomaly, Dimensions, TestContext.Current.CancellationToken);

        Assert.True(first.IsAvailable);
        Assert.Equal(new GibsPreviewSource(FirmsSource: "VIIRS_NOAA20_NRT", Satellite: "NOAA-20", Instrument: "VIIRS"), first.BaseSource);
        Assert.Equal([Noaa20Base], handler.ProbedBaseLayers);
        Assert.Equal($"{Noaa20Base},{Noaa20Overlay}", Assert.Single(handler.CompositeLayers));
        Assert.True(first.PngBytes.HasValue);

        var image = StbImageSharp.ImageResult.FromMemory(first.PngBytes.GetValueOrDefault().ToArray(), StbImageSharp.ColorComponents.RedGreenBlueAlpha);

        Assert.Contains(image.Data.Chunk(size: 4), pixel => pixel.SequenceEqual([byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue]));
        Assert.Same(first, second);
        Assert.Equal(requestCount, handler.RequestCount);
    }

    /// <summary>Uses nighttime imagery throughout fallback for a nighttime observation.</summary>
    [Fact]
    public async Task GetPreviewAsyncUsesPassMatchedNighttimeFallbackLayers()
    {
        const string noaa20NightBase = "VIIRS_NOAA20_Brightness_Temp_BandI5_Night";
        const string noaa21NightBase = "VIIRS_NOAA21_Brightness_Temp_BandI5_Night";

        using var handler = new PreviewHandler(
            layer => noaa21NightBase.Equals(layer, StringComparison.Ordinal)
            ? PngTestData.CreateSolidRgba(width: 64, height: 64, red: 30, green: 80, blue: 40, alpha: 255)
            : PngTestData.CreateSolidRgba(width: 64, height: 64, red: 0, green: 0, blue: 0, alpha: 255)
        );

        using MemoryCache cache = CreateCache();

        using var httpClient = new HttpClient(handler);

        GibsClient client = CreateClient(httpClient, cache);

        GibsPreview preview = await client.GetPreviewAsync(
            CreateAnomaly(source: "VIIRS_NOAA20_NRT", satellite: "NOAA-20") with { DayNight = "N" },
            Dimensions,
            TestContext.Current.CancellationToken
        );

        Assert.True(preview.IsAvailable);
        Assert.Equal([noaa20NightBase, noaa21NightBase], handler.ProbedBaseLayers);
        Assert.Equal($"{noaa21NightBase},{Noaa20Overlay}", Assert.Single(handler.CompositeLayers));
    }

    private static Anomaly CreateAnomaly(string source, string satellite)
    {
        return new(
            Id: "anomaly",
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
            new Uri(uriString: "https://www.google.com/maps?q=57.946080,60.061420")
        );
    }

    private static MemoryCache CreateCache()
    {
        return new(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
    }

    private static GibsClient CreateClient(HttpClient httpClient, IMemoryCache cache)
    {
        httpClient.BaseAddress = new(uriString: "https://gibs.example.test/");

        return new(httpClient, cache, NullLogger<GibsClient>.Instance);
    }

    private static byte[] CreateCoveragePng(int usablePixelCount)
    {
        return PngTestData.CreateRgba(
            width: 64,
            height: 64,
            pixel => pixel < usablePixelCount
                ? new PixelColor(Red: 30, Green: 80, Blue: 40)
                : new PixelColor(Red: 0, Green: 0, Blue: 0)
        );
    }

    private sealed class PreviewHandler(Func<string, byte[]> createProbePng, byte[]? compositePng = null) : HttpMessageHandler
    {
        private readonly byte[] _compositePng = compositePng
            ?? PngTestData.CreateSolidRgba(width: 900, height: 600, red: 30, green: 80, blue: 40, alpha: 255);
        private int _requestCount;

        public List<string> ProbedBaseLayers { get; } = [];

        public List<string> CompositeLayers { get; } = [];

        public List<string> CompositeStyles { get; } = [];

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(Interlocked.Increment(ref _requestCount) > 0);
            Uri requestUri = Assert.IsType<Uri>(request.RequestUri);
            HttpContent content;

            if (requestUri.AbsolutePath.EndsWith(value: ".xml", StringComparison.Ordinal))
            {
                content = new StringContent(content: "<Domains><Domain>2026-07-23</Domain></Domains>", Encoding.UTF8, mediaType: "application/xml");
            }
            else
            {
                string layers = ReadQueryValue(requestUri, name: "LAYERS");
                byte[] bytes;

                if (layers.Contains(value: ',', StringComparison.Ordinal))
                {
                    CompositeLayers.Add(layers);
                    CompositeStyles.Add(ReadQueryValue(requestUri, name: "STYLES"));
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

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }

        private static string ReadQueryValue(Uri uri, string name)
        {
            foreach (string item in uri.Query.TrimStart(trimChar: '?').Split(separator: '&'))
            {
                string[] pair = item.Split(separator: '=', count: 2);

                if (pair.Length == 2 && pair[0].Equals(name, StringComparison.Ordinal))
                    return Uri.UnescapeDataString(pair[1]);
            }

            throw new InvalidOperationException($"Missing {name} query value.");
        }
    }
}
