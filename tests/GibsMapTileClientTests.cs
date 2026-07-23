using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using StbImageSharp;
using StbImageWriteSharp;
using ThermalWatch.Core;

namespace ThermalWatch.Tests;

public sealed class GibsMapTileClientTests
{
    [Theory]
    [InlineData(0, 0, 0, true)]
    [InlineData(9, 511, 511, true)]
    [InlineData(-1, 0, 0, false)]
    [InlineData(10, 0, 0, false)]
    [InlineData(2, -1, 0, false)]
    [InlineData(2, 4, 0, false)]
    [InlineData(2, 0, 4, false)]
    public void TryCreateValidatesWebMercatorTileCoordinates(
        int zoom,
        int x,
        int y,
        bool expected) => Assert.Equal(expected, GibsMapTileCoordinates.TryCreate(zoom, x, y, out _));

    [Fact]
    public async Task GetMapTileAsyncStopsAfterCompleteTerraCoverage()
    {
        var handler = new TileHandler(_ => JpegResponse(SolidJpeg(red: 42, green: 72, blue: 112)));
        using MemoryCache cache = CreateCache();
        using GibsMapTileClient client = CreateClient(handler, cache);

        GibsMapTileResult tile = await client.GetMapTileAsync(
            Coordinates(zoom: 6, x: 37, y: 21),
            TestContext.Current.CancellationToken);

        Assert.Equal(GibsMapTileCoverage.Complete, tile.Coverage);
        Assert.Equal(
            "https://gibs.example.test/wmts/epsg3857/best/"
            + "MODIS_Terra_CorrectedReflectance_TrueColor/default/default/"
            + "GoogleMapsCompatible_Level9/6/21/37.jpeg",
            Assert.Single(handler.Requests).AbsoluteUri);
        AssertPixelNear(tile.PngBytes, x: 0, red: 42, green: 72, blue: 112, byte.MaxValue);
    }

    [Fact]
    public async Task GetMapTileAsyncFillsTerraHolesFromAquaWithoutReplacingTerra()
    {
        byte[] terra = CreateJpeg((x, _) => x < 128
            ? ((byte)82, (byte)46, (byte)34)
            : ((byte)0, (byte)0, (byte)0));
        byte[] aqua = SolidJpeg(red: 31, green: 91, blue: 43);
        var handler = new TileHandler(index => JpegResponse(index == 0 ? terra : aqua));
        using MemoryCache cache = CreateCache();
        using GibsMapTileClient client = CreateClient(handler, cache);

        GibsMapTileResult tile = await client.GetMapTileAsync(
            Coordinates(zoom: 3, x: 1, y: 2),
            TestContext.Current.CancellationToken);

        Assert.Equal(GibsMapTileCoverage.Complete, tile.Coverage);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("MODIS_Terra", handler.Requests[0].AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("MODIS_Aqua", handler.Requests[1].AbsoluteUri, StringComparison.Ordinal);
        AssertPixelNear(tile.PngBytes, x: 20, red: 82, green: 46, blue: 34, byte.MaxValue);
        AssertPixelNear(tile.PngBytes, x: 230, red: 31, green: 91, blue: 43, byte.MaxValue);
    }

    [Fact]
    public async Task GetMapTileAsyncContinuesThroughRequestMediaAndDecodeFailuresInOrder()
    {
        byte[] valid = SolidJpeg(red: 55, green: 85, blue: 115);
        var handler = new TileHandler(index => index switch
        {
            0 => new(HttpStatusCode.BadGateway),
            1 => BytesResponse(valid, mediaType: "image/png"),
            2 => JpegResponse([1, 2, 3, 4]),
            _ => JpegResponse(valid)
        });
        using MemoryCache cache = CreateCache();
        using GibsMapTileClient client = CreateClient(handler, cache);

        GibsMapTileResult tile = await client.GetMapTileAsync(
            Coordinates(zoom: 2, x: 1, y: 1),
            TestContext.Current.CancellationToken);

        Assert.Equal(GibsMapTileCoverage.Complete, tile.Coverage);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Contains("MODIS_Terra", handler.Requests[0].AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("MODIS_Aqua", handler.Requests[1].AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("VIIRS_NOAA21", handler.Requests[2].AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("VIIRS_NOAA20", handler.Requests[3].AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetMapTileAsyncRejectsOversizedSourceBeforeReadingIt()
    {
        byte[] valid = SolidJpeg(red: 44, green: 74, blue: 104);
        var handler = new TileHandler(index =>
        {
            if (index > 0)
                return JpegResponse(valid);

            HttpResponseMessage response = JpegResponse([1]);
            response.Content.Headers.ContentLength = 1024 * 1024 + 1;
            return response;
        });
        using MemoryCache cache = CreateCache();
        using GibsMapTileClient client = CreateClient(handler, cache);

        GibsMapTileResult tile = await client.GetMapTileAsync(
            Coordinates(zoom: 1, x: 0, y: 0),
            TestContext.Current.CancellationToken);

        Assert.Equal(GibsMapTileCoverage.Complete, tile.Coverage);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GetMapTileAsyncRejectsOversizedSourceWhileReadingIt()
    {
        byte[] valid = SolidJpeg(red: 44, green: 74, blue: 104);
        var handler = new TileHandler(index => index == 0
            ? BytesResponse(
                new UnknownLengthContent(new byte[1024 * 1024 + 1]),
                mediaType: "image/jpeg")
            : JpegResponse(valid));
        using MemoryCache cache = CreateCache();
        using GibsMapTileClient client = CreateClient(handler, cache);

        GibsMapTileResult tile = await client.GetMapTileAsync(
            Coordinates(zoom: 1, x: 0, y: 0),
            TestContext.Current.CancellationToken);

        Assert.Equal(GibsMapTileCoverage.Complete, tile.Coverage);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GetMapTileAsyncReturnsTransparentPngWhenEveryProductIsNoData()
    {
        var handler = new TileHandler(_ => JpegResponse(SolidJpeg(red: 0, green: 0, blue: 0)));
        using MemoryCache cache = CreateCache();
        using GibsMapTileClient client = CreateClient(handler, cache);

        GibsMapTileResult tile = await client.GetMapTileAsync(
            Coordinates(zoom: 2, x: 1, y: 1),
            TestContext.Current.CancellationToken);

        Assert.Equal(GibsMapTileCoverage.None, tile.Coverage);
        Assert.Equal(5, handler.Requests.Count);
        AssertPixelNear(tile.PngBytes, x: 0, red: 0, green: 0, blue: 0, alpha: 0);
    }

    [Fact]
    public async Task GetMapTileAsyncReturnsPartialCoverageAfterEveryProduct()
    {
        byte[] partial = CreateJpeg((x, _) => x < 128
            ? ((byte)65, (byte)95, (byte)125)
            : ((byte)0, (byte)0, (byte)0));
        byte[] black = SolidJpeg(red: 0, green: 0, blue: 0);
        var handler = new TileHandler(index => JpegResponse(index == 4 ? partial : black));
        using MemoryCache cache = CreateCache();
        using GibsMapTileClient client = CreateClient(handler, cache);

        GibsMapTileResult tile = await client.GetMapTileAsync(
            Coordinates(zoom: 2, x: 1, y: 1),
            TestContext.Current.CancellationToken);

        Assert.Equal(GibsMapTileCoverage.Partial, tile.Coverage);
        Assert.Equal(5, handler.Requests.Count);
        AssertPixelNear(tile.PngBytes, x: 20, red: 65, green: 95, blue: 125, byte.MaxValue);
        AssertPixelNear(tile.PngBytes, x: 230, red: 0, green: 0, blue: 0, alpha: 0);
    }

    [Fact]
    public async Task GetMapTileAsyncCachesOnlyCompleteTiles()
    {
        using MemoryCache completeCache = CreateCache();
        var completeHandler = new TileHandler(_ => JpegResponse(SolidJpeg(red: 40, green: 70, blue: 100)));
        using GibsMapTileClient completeClient = CreateClient(completeHandler, completeCache);
        GibsMapTileCoordinates coordinates = Coordinates(zoom: 2, x: 1, y: 1);

        GibsMapTileResult firstComplete = await completeClient.GetMapTileAsync(
            coordinates,
            TestContext.Current.CancellationToken);
        GibsMapTileResult secondComplete = await completeClient.GetMapTileAsync(
            coordinates,
            TestContext.Current.CancellationToken);

        Assert.Same(firstComplete, secondComplete);
        Assert.Single(completeHandler.Requests);

        using MemoryCache emptyCache = CreateCache();
        var emptyHandler = new TileHandler(_ => JpegResponse(SolidJpeg(red: 0, green: 0, blue: 0)));
        using GibsMapTileClient emptyClient = CreateClient(emptyHandler, emptyCache);
        await emptyClient.GetMapTileAsync(coordinates, TestContext.Current.CancellationToken);
        await emptyClient.GetMapTileAsync(coordinates, TestContext.Current.CancellationToken);

        Assert.Equal(10, emptyHandler.Requests.Count);
    }

    [Fact]
    public async Task GetMapTileAsyncPropagatesCallerCancellation()
    {
        var handler = new BlockingHandler();
        using MemoryCache cache = CreateCache();
        using GibsMapTileClient client = CreateClient(handler, cache);
        using var cancellation = new CancellationTokenSource();

        Task<GibsMapTileResult> loading = client.GetMapTileAsync(Coordinates(zoom: 2, x: 1, y: 1), cancellation.Token);
        await handler.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(seconds: 2),
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
    }

    private static GibsMapTileClient CreateClient(HttpMessageHandler handler, IMemoryCache cache) =>
        new(
            new HttpClient(handler)
            {
                BaseAddress = new(uriString: "https://gibs.example.test/")
            },
            cache,
            NullLogger<GibsMapTileClient>.Instance);

    private static MemoryCache CreateCache() =>
        new(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });

    private static GibsMapTileCoordinates Coordinates(int zoom, int x, int y)
    {
        Assert.True(GibsMapTileCoordinates.TryCreate(zoom, x, y, out GibsMapTileCoordinates coordinates));
        return coordinates;
    }

    private static HttpResponseMessage JpegResponse(byte[] bytes) =>
        BytesResponse(bytes, mediaType: "image/jpeg");

    private static HttpResponseMessage BytesResponse(byte[] bytes, string mediaType)
        => BytesResponse(new ByteArrayContent(bytes), mediaType);

    private static HttpResponseMessage BytesResponse(HttpContent content, string mediaType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return response;
    }

    private static byte[] SolidJpeg(byte red, byte green, byte blue) =>
        CreateJpeg((_, _) => (red, green, blue));

    private static byte[] CreateJpeg(Func<int, int, (byte Red, byte Green, byte Blue)> pixel)
    {
        const int size = 256;
        byte[] bytes = new byte[size * size * 3];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                (byte Red, byte Green, byte Blue) color = pixel(x, y);
                int offset = (y * size + x) * 3;
                bytes[offset] = color.Red;
                bytes[offset + 1] = color.Green;
                bytes[offset + 2] = color.Blue;
            }
        }

        using var stream = new MemoryStream();
        new ImageWriter().WriteJpg(
            bytes,
            size,
            size,
            StbImageWriteSharp.ColorComponents.RedGreenBlue,
            stream,
            quality: 100);
        return stream.ToArray();
    }

    private static void AssertPixelNear(
        byte[] png,
        int x,
        byte red,
        byte green,
        byte blue,
        byte alpha)
    {
        var image = ImageResult.FromMemory(
            png,
            StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        Assert.Equal(256, image.Width);
        Assert.Equal(256, image.Height);
        int offset = x * 4;
        Assert.InRange(image.Data[offset], Math.Max(byte.MinValue, red - 4), Math.Min(byte.MaxValue, red + 4));
        Assert.InRange(image.Data[offset + 1], Math.Max(byte.MinValue, green - 4), Math.Min(byte.MaxValue, green + 4));
        Assert.InRange(image.Data[offset + 2], Math.Max(byte.MinValue, blue - 4), Math.Min(byte.MaxValue, blue + 4));
        Assert.Equal(alpha, image.Data[offset + 3]);
    }

    private sealed class TileHandler(Func<int, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            return Task.FromResult(responseFactory(Requests.Count - 1));
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(message: "The blocking handler should be cancelled.");
        }
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
