using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using StbImageSharp;
using StbImageWriteSharp;

namespace ThermalWatch.Core;

public sealed partial class GibsMapTileClient(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<GibsMapTileClient> logger) : IDisposable
{
    public const int MaximumZoom = 9;

    private const int TileSize = 256;
    private const int MaximumTileBytes = 1024 * 1024;
    private const byte NoDataMaximumChannel = 12;
    private static readonly TimeSpan s_completeTileCacheDuration = TimeSpan.FromMinutes(minutes: 5);
    private readonly SemaphoreSlim _compositionSlots = new(initialCount: 8, maxCount: 8);

    public async Task<GibsMapTileResult> GetMapTileAsync(
        GibsMapTileCoordinates coordinates,
        CancellationToken cancellationToken)
    {
        (string Prefix, int Zoom, int X, int Y) cacheKey = (Prefix: "gibs:map", coordinates.Zoom, coordinates.X, coordinates.Y);
        if (cache.TryGetValue<GibsMapTileResult>(cacheKey, out GibsMapTileResult? cachedTile)
            && cachedTile is not null)
        {
            return cachedTile;
        }

        await _compositionSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cache.TryGetValue<GibsMapTileResult>(cacheKey, out cachedTile)
                && cachedTile is not null)
            {
                return cachedTile;
            }

            GibsMapTileResult tile = await ComposeTileAsync(coordinates, cancellationToken).ConfigureAwait(false);
            if (tile.Coverage == GibsMapTileCoverage.Complete)
            {
                cache.Set(
                    cacheKey,
                    tile,
                    new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = s_completeTileCacheDuration,
                        Size = tile.PngBytes.Length
                    });
            }

            return tile;
        }
        finally
        {
            _compositionSlots.Release();
        }
    }

    private async Task<GibsMapTileResult> ComposeTileAsync(
        GibsMapTileCoordinates coordinates,
        CancellationToken cancellationToken)
    {
        byte[] pixels = new byte[TileSize * TileSize * 4];
        int remainingPixels = TileSize * TileSize;

        foreach (string layer in GibsLayers.MapBaseLayers)
        {
            byte[]? source = await GetTilePixelsAsync(layer, coordinates, cancellationToken).ConfigureAwait(false);
            if (source is null)
                continue;

            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
                if (pixels[offset + 3] != 0 || IsNoData(source, offset))
                    continue;

                pixels[offset] = source[offset];
                pixels[offset + 1] = source[offset + 1];
                pixels[offset + 2] = source[offset + 2];
                pixels[offset + 3] = source[offset + 3];
                remainingPixels--;
            }

            if (remainingPixels == 0)
                break;
        }

        GibsMapTileCoverage coverage = remainingPixels switch
        {
            0 => GibsMapTileCoverage.Complete,
            var remaining when remaining == TileSize * TileSize => GibsMapTileCoverage.None,
            _ => GibsMapTileCoverage.Partial
        };

        using var stream = new MemoryStream();
        var writer = new ImageWriter();
        writer.WritePng(
            pixels,
            TileSize,
            TileSize,
            StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha,
            stream);

        if (coverage != GibsMapTileCoverage.Complete)
        {
            LogIncompleteTileCoverage(
                logger,
                coverage,
                coordinates.Zoom,
                coordinates.X,
                coordinates.Y);
        }

        return new(stream.ToArray(), coverage);
    }

    private async Task<byte[]?> GetTilePixelsAsync(
        string layer,
        GibsMapTileCoordinates coordinates,
        CancellationToken cancellationToken)
    {
        try
        {
            Uri requestUri = BuildTileUri(layer, coordinates);
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentType?.MediaType is not { } mediaType
                || !mediaType.Equals(value: "image/jpeg", StringComparison.OrdinalIgnoreCase)
                || response.Content.Headers.ContentLength > MaximumTileBytes)
            {
                return null;
            }

            byte[]? bytes = await HttpContentReader.ReadLimitedBytesAsync(
                response.Content,
                MaximumTileBytes,
                cancellationToken).ConfigureAwait(false);
            if (bytes is null)
                return null;

            using var imageInfoStream = new MemoryStream(bytes, writable: false);
            ImageInfo? imageInfo = ImageInfo.FromStream(imageInfoStream);
            if (imageInfo is not { Width: TileSize, Height: TileSize })
                return null;

            var image = ImageResult.FromMemory(
                bytes,
                StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            return image.Data;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogProductUnavailable(
                logger,
                exception,
                layer,
                coordinates.Zoom,
                coordinates.X,
                coordinates.Y);
            return null;
        }
    }

    private Uri BuildTileUri(string layer, GibsMapTileCoordinates coordinates)
    {
        Uri baseAddress = httpClient.BaseAddress
            ?? throw new InvalidOperationException(message: "The GIBS HTTP client requires a base address.");
        string path = string.Create(
            CultureInfo.InvariantCulture,
            handler: $"wmts/epsg3857/best/{layer}/default/default/GoogleMapsCompatible_Level9/{coordinates.Zoom}/{coordinates.Y}/{coordinates.X}.jpeg");
        return new(baseAddress, path);
    }

    private static bool IsNoData(byte[] pixels, int offset) =>
        pixels[offset + 3] == 0
        || pixels[offset] <= NoDataMaximumChannel
            && pixels[offset + 1] <= NoDataMaximumChannel
            && pixels[offset + 2] <= NoDataMaximumChannel;

    public void Dispose()
    {
        _compositionSlots.Dispose();
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Debug,
        Message = "GIBS map tile coverage is {Coverage} for zoom {Zoom}, x {X}, y {Y}")]
    private static partial void LogIncompleteTileCoverage(
        ILogger logger,
        GibsMapTileCoverage coverage,
        int zoom,
        int x,
        int y);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Debug,
        Message = "GIBS map product {Product} is unavailable for zoom {Zoom}, x {X}, y {Y}")]
    private static partial void LogProductUnavailable(
        ILogger logger,
        Exception exception,
        string product,
        int zoom,
        int x,
        int y);
}
