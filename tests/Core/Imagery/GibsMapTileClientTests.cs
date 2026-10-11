using System.Net;
using System.Net.Http.Headers;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using StbImageSharp;

using StbImageWriteSharp;

using ThermalWatch.Core;

namespace ThermalWatch.Tests.Core.Imagery;

/// <summary>Verifies map-tile composition, product ordering, caching, and bounded reads.</summary>
public sealed class GibsMapTileClientTests
{
    private static readonly DateTimeOffset Now = new(year: 2026, month: 7, day: 27, hour: 12, minute: 0, second: 0, TimeSpan.Zero);

    /// <summary>Provides zoom and matrix coordinate boundaries.</summary>
    public static TheoryData<int, int, int, bool> CoordinateCases => new()
    {
        { 0, 0, 0, true },
        { 9, 511, 511, true },
        { -1, 0, 0, false },
        { 10, 0, 0, false },
        { 2, -1, 0, false },
        { 2, 4, 0, false },
        { 2, 0, 4, false }
    };

    /// <summary>Caches complete imagery while retrying unavailable imagery on later requests.</summary>
    [Fact]
    public async Task GetMapTileAsyncCachesOnlyCompleteTiles()
    {
        using MemoryCache completeCache = CreateCache();

        using var completeHandler = new TileHandler(index => JpegResponse(SolidJpeg(red: 40, green: 70, blue: 100)));

        using var completeHttpClient = new HttpClient(completeHandler);

        GibsMapTileClient completeClient = CreateClient(completeHttpClient, completeCache);
        GibsMapTileCoordinates coordinates = Coordinates(zoom: 2, column: 1, row: 1);

        GibsMapTileResult firstComplete = await completeClient.GetMapTileAsync(coordinates, TestContext.Current.CancellationToken);

        GibsMapTileResult secondComplete = await completeClient.GetMapTileAsync(coordinates, TestContext.Current.CancellationToken);

        Assert.Same(firstComplete, secondComplete);
        Uri completeRequest = Assert.Single(completeHandler.Requests);
        Assert.Contains(expectedSubstring: "MODIS_Terra", completeRequest.AbsoluteUri, StringComparison.Ordinal);

        using MemoryCache emptyCache = CreateCache();

        using var emptyHandler = new TileHandler(index => JpegResponse(SolidJpeg(red: 0, green: 0, blue: 0)));

        using var emptyHttpClient = new HttpClient(emptyHandler);

        GibsMapTileClient emptyClient = CreateClient(emptyHttpClient, emptyCache);
        GibsMapTileResult firstEmpty = await emptyClient.GetMapTileAsync(coordinates, TestContext.Current.CancellationToken);
        GibsMapTileResult secondEmpty = await emptyClient.GetMapTileAsync(coordinates, TestContext.Current.CancellationToken);
        Assert.Equal(GibsMapTileCoverage.None, firstEmpty.Coverage);
        Assert.Equal(GibsMapTileCoverage.None, secondEmpty.Coverage);

        Assert.Equal(expected: 20, emptyHandler.Requests.Count);
    }

    /// <summary>Continues through upstream, media-type, and decode failures in product order.</summary>
    [Fact]
    public async Task GetMapTileAsyncContinuesThroughRequestMediaAndDecodeFailuresInOrder()
    {
        byte[] valid = SolidJpeg(red: 55, green: 85, blue: 115);

        using var handler = new TileHandler(
            index => index switch
        {
            0 => new(HttpStatusCode.BadGateway),
            1 => BytesResponse(valid, mediaType: "image/png"),
            2 => JpegResponse([1, 2, 3, 4]),
            _ => JpegResponse(valid)
        }
        );

        using MemoryCache cache = CreateCache();

        using var httpClient = new HttpClient(handler);

        GibsMapTileClient client = CreateClient(httpClient, cache);

        GibsMapTileResult tile = await client.GetMapTileAsync(Coordinates(zoom: 2, column: 1, row: 1), TestContext.Current.CancellationToken);

        Assert.Equal(GibsMapTileCoverage.Complete, tile.Coverage);
        Assert.Equal(expected: 4, handler.Requests.Count);
        Assert.Contains(expectedSubstring: "MODIS_Terra", handler.Requests[index: 0].AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "MODIS_Aqua", handler.Requests[index: 1].AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "VIIRS_NOAA21", handler.Requests[index: 2].AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "VIIRS_NOAA20", handler.Requests[index: 3].AbsoluteUri, StringComparison.Ordinal);
    }

    /// <summary>Tries all previous-day products after exhausting current-day imagery.</summary>
    [Fact]
    public async Task GetMapTileAsyncFallsBackToAllProductsFromPreviousUtcDay()
    {
        byte[] black = SolidJpeg(red: 0, green: 0, blue: 0);
        byte[] yesterday = SolidJpeg(red: 52, green: 82, blue: 112);

        using var handler = new TileHandler(index => JpegResponse(index == 9 ? yesterday : black));

        using MemoryCache cache = CreateCache();

        using var httpClient = new HttpClient(handler);

        GibsMapTileClient client = CreateClient(httpClient, cache);

        GibsMapTileResult tile = await client.GetMapTileAsync(Coordinates(zoom: 3, column: 1, row: 2), TestContext.Current.CancellationToken);

        Assert.Equal(GibsMapTileCoverage.Complete, tile.Coverage);
        Assert.Equal(expected: 10, handler.Requests.Count);
        Assert.All(
            handler.Requests.Take(count: 5),
            request => Assert.Contains(expectedSubstring: "/2026-07-27/", request.AbsoluteUri, StringComparison.Ordinal)
        );
        Assert.All(
            handler.Requests.Skip(count: 5),
            request => Assert.Contains(expectedSubstring: "/2026-07-26/", request.AbsoluteUri, StringComparison.Ordinal)
        );
        Assert.Contains(expectedSubstring: "MODIS_Terra", handler.Requests[index: 5].AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "VIIRS_SNPP", handler.Requests[index: 9].AbsoluteUri, StringComparison.Ordinal);
        AssertPixelNear(tile.PngBytes, column: 20, red: 52, green: 82, blue: 112, byte.MaxValue);
    }

    /// <summary>Fills current-day gaps with previous-day imagery while preserving valid current pixels.</summary>
    [Fact]
    public async Task GetMapTileAsyncFillsRemainingTodayHolesWithoutReplacingTodayPixels()
    {
        byte[] today = CreateJpeg((column, row) => column < 128 ? new PixelColor(Red: 72, Green: 42, Blue: 32) : new PixelColor(Red: 0, Green: 0, Blue: 0));

        byte[] black = SolidJpeg(red: 0, green: 0, blue: 0);
        byte[] yesterday = SolidJpeg(red: 32, green: 92, blue: 62);

        using var handler = new TileHandler(index => JpegResponse(index switch { 0 => today, 5 => yesterday, _ => black }));

        using MemoryCache cache = CreateCache();

        using var httpClient = new HttpClient(handler);

        GibsMapTileClient client = CreateClient(httpClient, cache);

        GibsMapTileResult tile = await client.GetMapTileAsync(Coordinates(zoom: 3, column: 1, row: 2), TestContext.Current.CancellationToken);

        Assert.Equal(GibsMapTileCoverage.Complete, tile.Coverage);
        Assert.Equal(expected: 6, handler.Requests.Count);
        Assert.Contains(expectedSubstring: "/2026-07-26/", handler.Requests[index: 5].AbsoluteUri, StringComparison.Ordinal);
        AssertPixelNear(tile.PngBytes, column: 20, red: 72, green: 42, blue: 32, byte.MaxValue);
        AssertPixelNear(tile.PngBytes, column: 230, red: 32, green: 92, blue: 62, byte.MaxValue);
    }

    /// <summary>Fills Terra gaps with Aqua imagery without replacing valid Terra pixels.</summary>
    [Fact]
    public async Task GetMapTileAsyncFillsTerraHolesFromAquaWithoutReplacingTerra()
    {
        byte[] terra = CreateJpeg((column, row) => column < 128 ? new PixelColor(Red: 82, Green: 46, Blue: 34) : new PixelColor(Red: 0, Green: 0, Blue: 0));

        byte[] aqua = SolidJpeg(red: 31, green: 91, blue: 43);

        using var handler = new TileHandler(index => JpegResponse(index == 0 ? terra : aqua));

        using MemoryCache cache = CreateCache();

        using var httpClient = new HttpClient(handler);

        GibsMapTileClient client = CreateClient(httpClient, cache);

        GibsMapTileResult tile = await client.GetMapTileAsync(Coordinates(zoom: 3, column: 1, row: 2), TestContext.Current.CancellationToken);

        Assert.Equal(GibsMapTileCoverage.Complete, tile.Coverage);
        Assert.Equal(expected: 2, handler.Requests.Count);
        Assert.Contains(expectedSubstring: "MODIS_Terra", handler.Requests[index: 0].AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains(expectedSubstring: "MODIS_Aqua", handler.Requests[index: 1].AbsoluteUri, StringComparison.Ordinal);
        AssertPixelNear(tile.PngBytes, column: 20, red: 82, green: 46, blue: 34, byte.MaxValue);
        AssertPixelNear(tile.PngBytes, column: 230, red: 31, green: 91, blue: 43, byte.MaxValue);
    }

    /// <summary>Propagates caller cancellation during a blocked tile request.</summary>
    [Fact]
    public async Task GetMapTileAsyncPropagatesCallerCancellation()
    {
        using var handler = new BlockingHandler();

        using MemoryCache cache = CreateCache();

        using var httpClient = new HttpClient(handler);

        GibsMapTileClient client = CreateClient(httpClient, cache);

        using var cancellation = new CancellationTokenSource();

        Task<GibsMapTileResult> loading = client.GetMapTileAsync(Coordinates(zoom: 2, column: 1, row: 1), cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(seconds: 2), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        Assert.True(exception.CancellationToken.IsCancellationRequested);
    }

    /// <summary>Rejects oversized declared content before reading source bytes.</summary>
    [Fact]
    public async Task GetMapTileAsyncRejectsOversizedSourceBeforeReadingIt()
    {
        byte[] valid = SolidJpeg(red: 44, green: 74, blue: 104);

        using var handler = new TileHandler(
            index =>
        {
            if (index > 0)
                return JpegResponse(valid);

            HttpResponseMessage response = JpegResponse([1]);
            response.Content.Headers.ContentLength = (1024 * 1024) + 1;

            return response;
        }
        );

        using MemoryCache cache = CreateCache();

        using var httpClient = new HttpClient(handler);

        GibsMapTileClient client = CreateClient(httpClient, cache);

        GibsMapTileResult tile = await client.GetMapTileAsync(Coordinates(zoom: 1, column: 0, row: 0), TestContext.Current.CancellationToken);

        Assert.Equal(GibsMapTileCoverage.Complete, tile.Coverage);
        Assert.Equal(expected: 2, handler.Requests.Count);
    }

    /// <summary>Enforces the source size bound when no content length is supplied.</summary>
    [Fact]
    public async Task GetMapTileAsyncRejectsOversizedSourceWhileReadingIt()
    {
        byte[] valid = SolidJpeg(red: 44, green: 74, blue: 104);

        using var handler = new TileHandler(
            index => index == 0
            ? BytesResponse(new UnknownLengthContent(new byte[(1024 * 1024) + 1]), mediaType: "image/jpeg")
            : JpegResponse(valid)
        );

        using MemoryCache cache = CreateCache();

        using var httpClient = new HttpClient(handler);

        GibsMapTileClient client = CreateClient(httpClient, cache);

        GibsMapTileResult tile = await client.GetMapTileAsync(Coordinates(zoom: 1, column: 0, row: 0), TestContext.Current.CancellationToken);

        Assert.Equal(GibsMapTileCoverage.Complete, tile.Coverage);
        Assert.Equal(expected: 2, handler.Requests.Count);
    }

    /// <summary>Returns partial coverage after exhausting all imagery products.</summary>
    [Fact]
    public async Task GetMapTileAsyncReturnsPartialCoverageAfterEveryProduct()
    {
        byte[] partial = CreateJpeg((column, row) => column < 128 ? new PixelColor(Red: 65, Green: 95, Blue: 125) : new PixelColor(Red: 0, Green: 0, Blue: 0));

        byte[] black = SolidJpeg(red: 0, green: 0, blue: 0);

        using var handler = new TileHandler(index => JpegResponse(index == 4 ? partial : black));

        using MemoryCache cache = CreateCache();

        using var httpClient = new HttpClient(handler);

        GibsMapTileClient client = CreateClient(httpClient, cache);

        GibsMapTileResult tile = await client.GetMapTileAsync(Coordinates(zoom: 2, column: 1, row: 1), TestContext.Current.CancellationToken);

        Assert.Equal(GibsMapTileCoverage.Partial, tile.Coverage);
        Assert.Equal(expected: 10, handler.Requests.Count);
        AssertPixelNear(tile.PngBytes, column: 20, red: 65, green: 95, blue: 125, byte.MaxValue);
        AssertPixelNear(tile.PngBytes, column: 230, red: 0, green: 0, blue: 0, alpha: 0);
    }

    /// <summary>Returns a transparent PNG when every source product contains no data.</summary>
    [Fact]
    public async Task GetMapTileAsyncReturnsTransparentPngWhenEveryProductIsNoData()
    {
        using var handler = new TileHandler(index => JpegResponse(SolidJpeg(red: 0, green: 0, blue: 0)));

        using MemoryCache cache = CreateCache();

        using var httpClient = new HttpClient(handler);

        GibsMapTileClient client = CreateClient(httpClient, cache);

        GibsMapTileResult tile = await client.GetMapTileAsync(Coordinates(zoom: 2, column: 1, row: 1), TestContext.Current.CancellationToken);

        Assert.Equal(GibsMapTileCoverage.None, tile.Coverage);
        Assert.Equal(expected: 10, handler.Requests.Count);
        AssertPixelNear(tile.PngBytes, column: 0, red: 0, green: 0, blue: 0, alpha: 0);
    }

    /// <summary>Stops requesting products once Terra provides complete tile coverage.</summary>
    [Fact]
    public async Task GetMapTileAsyncStopsAfterCompleteTerraCoverage()
    {
        using var handler = new TileHandler(index => JpegResponse(SolidJpeg(red: 42, green: 72, blue: 112)));

        using MemoryCache cache = CreateCache();

        using var httpClient = new HttpClient(handler);

        GibsMapTileClient client = CreateClient(httpClient, cache);

        GibsMapTileResult tile = await client.GetMapTileAsync(Coordinates(zoom: 6, column: 37, row: 21), TestContext.Current.CancellationToken);

        Assert.Equal(GibsMapTileCoverage.Complete, tile.Coverage);
        Assert.Equal(
            "https://gibs.example.test/wmts/epsg3857/best/"
            + "MODIS_Terra_CorrectedReflectance_TrueColor/default/2026-07-27/"
            + "GoogleMapsCompatible_Level9/6/21/37.jpeg",
            Assert.Single(handler.Requests).AbsoluteUri
        );
        AssertPixelNear(tile.PngBytes, column: 0, red: 42, green: 72, blue: 112, byte.MaxValue);
    }

    /// <summary>Validates coordinates against the supported Web Mercator matrix.</summary>
    [Theory]
    [MemberData(nameof(CoordinateCases))]
    public void TryCreateValidatesWebMercatorTileCoordinates(int zoom, int column, int row, bool expected)
    {
        bool created = GibsMapTileCoordinates.TryCreate(zoom, column, row, out GibsMapTileCoordinates coordinates);
        Assert.Equal(expected, created);

        if (created)
        {
            Assert.Equal(zoom, coordinates.Zoom);
            Assert.Equal(column, coordinates.Column);
            Assert.Equal(row, coordinates.Row);
        }
    }

    private static void AssertPixelNear(ReadOnlyMemory<byte> pngBytes, int column, byte red, byte green, byte blue, byte alpha)
    {
        var image = ImageResult.FromMemory(pngBytes.ToArray(), StbImageSharp.ColorComponents.RedGreenBlueAlpha);

        Assert.Equal(expected: 256, image.Width);
        Assert.Equal(expected: 256, image.Height);
        int offset = column * 4;
        Assert.InRange(image.Data[offset], Math.Max(byte.MinValue, red - 4), Math.Min(byte.MaxValue, red + 4));
        Assert.InRange(image.Data[offset + 1], Math.Max(byte.MinValue, green - 4), Math.Min(byte.MaxValue, green + 4));
        Assert.InRange(image.Data[offset + 2], Math.Max(byte.MinValue, blue - 4), Math.Min(byte.MaxValue, blue + 4));
        Assert.Equal(alpha, image.Data[offset + 3]);
    }

    private static HttpResponseMessage BytesResponse(byte[] bytes, string mediaType)
    {
        return BytesResponse(new ByteArrayContent(bytes), mediaType);
    }

    private static HttpResponseMessage BytesResponse(HttpContent content, string mediaType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content
        };

        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);

        return response;
    }

    private static GibsMapTileCoordinates Coordinates(int zoom, int column, int row)
    {
        Assert.True(GibsMapTileCoordinates.TryCreate(zoom, column, row, out GibsMapTileCoordinates coordinates));

        return coordinates;
    }

    private static MemoryCache CreateCache()
    {
        return new(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
    }

    private static GibsMapTileClient CreateClient(HttpClient httpClient, IMemoryCache cache)
    {
        httpClient.BaseAddress = new(uriString: "https://gibs.example.test/");

        return new(httpClient, cache, new FakeTimeProvider(Now), NullLogger<GibsMapTileClient>.Instance);
    }

    private static byte[] CreateJpeg(Func<int, int, PixelColor> pixel)
    {
        const int size = 256;
        byte[] bytes = new byte[size * size * 3];

        for (int row = 0; row < size; row++)
        {
            for (int column = 0; column < size; column++)
            {
                PixelColor color = pixel(column, row);
                int offset = ((row * size) + column) * 3;
                bytes[offset] = color.Red;
                bytes[offset + 1] = color.Green;
                bytes[offset + 2] = color.Blue;
            }
        }

        using var stream = new MemoryStream();

        new ImageWriter().WriteJpg(bytes, size, size, StbImageWriteSharp.ColorComponents.RedGreenBlue, stream, quality: 100);

        return stream.ToArray();
    }

    private static HttpResponseMessage JpegResponse(byte[] bytes)
    {
        return BytesResponse(bytes, mediaType: "image/jpeg");
    }

    private static byte[] SolidJpeg(byte red, byte green, byte blue)
    {
        return CreateJpeg((column, row) => new(red, green, blue));
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            throw new InvalidOperationException(message: "The blocking handler should be cancelled.");
        }
    }

    private sealed class TileHandler(Func<int, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(Assert.IsType<Uri>(request.RequestUri));

            return Task.FromResult(responseFactory(Requests.Count - 1));
        }
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return stream.WriteAsync(bytes).AsTask();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return false;
        }
    }
}
