using System.Globalization;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using Nito.AsyncEx;

using StbImageSharp;

using StbImageWriteSharp;

namespace ThermalWatch.Core;

/// <summary>Composites recent GIBS products into map tiles, retaining complete coverage in the cache.</summary>
public sealed partial class GibsMapTileClient(HttpClient httpClient, IMemoryCache cache, TimeProvider timeProvider, ILogger<GibsMapTileClient> logger)
{
    /// <summary>The highest zoom level available from the selected GIBS products.</summary>
    public const int MaximumZoom = 9;

    private const int MaximumTileBytes = 1024 * 1024;
    private const byte NoDataMaximumChannel = 12;
    private const int TileSize = 256;
    private static readonly TimeSpan CompleteTileCacheDuration = TimeSpan.FromMinutes(minutes: 5);
    private readonly AsyncSemaphore _compositionSlots = new(initialCount: 8);
    /// <summary>Returns a PNG tile and indicates whether recent products filled all its pixels.</summary>
    public async Task<GibsMapTileResult> GetMapTileAsync(GibsMapTileCoordinates coordinates, CancellationToken cancellationToken)
    {
        var cacheKey = new MapTileCacheKey(coordinates);

        if (cache.TryGetValue(cacheKey, out GibsMapTileResult? cachedTile) && cachedTile is not null)
            return cachedTile;

        await _compositionSlots.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        try
        {
            if (cache.TryGetValue(cacheKey, out cachedTile) && cachedTile is not null)
                return cachedTile;

            GibsMapTileResult tile = await ComposeTileAsync(coordinates, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            return tile.Coverage == GibsMapTileCoverage.Complete
                ? cache.Set(
                    cacheKey,
                    tile,
                    new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = CompleteTileCacheDuration,
                        Size = tile.PngBytes.Length
                    }
                )
                : tile;
        }
        finally
        {
            _compositionSlots.Release();
        }
    }

    private static int FillUnresolvedPixels(byte[] pixels, byte[] source, int remainingPixels)
    {
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

        return remainingPixels;
    }

    private static bool IsNoData(byte[] pixels, int offset)
    {
        return pixels[offset + 3] == 0
        || (pixels[offset] <= NoDataMaximumChannel
            && pixels[offset + 1] <= NoDataMaximumChannel
            && pixels[offset + 2] <= NoDataMaximumChannel);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "GIBS map tile coverage is {Coverage} for zoom {Zoom}, column {Column}, row {Row}")]
    private static partial void LogIncompleteTileCoverage(ILogger logger, GibsMapTileCoverage coverage, int zoom, int column, int row);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "GIBS map product {Product} is unavailable for zoom {Zoom}, column {Column}, row {Row}")]
    private static partial void LogProductUnavailable(ILogger logger, Exception exception, string product, int zoom, int column, int row);

    private Uri BuildTileUri(string layer, DateOnly date, GibsMapTileCoordinates coordinates)
    {
        Uri baseAddress = httpClient.BaseAddress
            ?? throw new InvalidOperationException(message: "The GIBS HTTP client requires a base address.");

        string path = string.Create(
            CultureInfo.InvariantCulture,
            $"wmts/epsg3857/best/{layer}/default/{date:yyyy-MM-dd}/GoogleMapsCompatible_Level9/{coordinates.Zoom}/{coordinates.Row}/{coordinates.Column}.jpeg"
        );

        return new(baseAddress, path);
    }

    private async Task<GibsMapTileResult> ComposeTileAsync(GibsMapTileCoordinates coordinates, CancellationToken cancellationToken)
    {
        byte[] pixels = new byte[TileSize * TileSize * 4];
        int remainingPixels = TileSize * TileSize;
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        foreach (DateOnly date in new[] { today, today.AddDays(value: -1) })
        {
            foreach (string layer in GibsLayers.MapBaseLayers)
            {
                byte[]? source = await GetTilePixelsAsync(layer, date, coordinates, cancellationToken)
                    .ConfigureAwait(continueOnCapturedContext: false);

                if (source is null)
                    continue;

                remainingPixels = FillUnresolvedPixels(pixels, source, remainingPixels);

                if (remainingPixels == 0)
                    break;
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
        writer.WritePng(pixels, TileSize, TileSize, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, stream);

        if (coverage != GibsMapTileCoverage.Complete)
            LogIncompleteTileCoverage(logger, coverage, coordinates.Zoom, coordinates.Column, coordinates.Row);

        return new(stream.ToArray(), coverage);
    }

    private async Task<byte[]?> GetTilePixelsAsync(string layer, DateOnly date, GibsMapTileCoordinates coordinates, CancellationToken cancellationToken)
    {
        try
        {
            Uri requestUri = BuildTileUri(layer, date, coordinates);

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

            using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumTileBytes)
                return null;

            if (response.Content.Headers.ContentType?.MediaType is not { } mediaType)
                return null;

            if (!mediaType.Equals(value: "image/jpeg", StringComparison.OrdinalIgnoreCase))
                return null;

            byte[]? bytes = await HttpContentReader.ReadLimitedBytesAsync(response.Content, MaximumTileBytes, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            if (bytes is null)
                return null;

            using var imageMetadataStream = new MemoryStream(bytes, writable: false);

            ImageInfo? imageMetadata = ImageInfo.FromStream(imageMetadataStream);

            if (imageMetadata is not { Width: TileSize, Height: TileSize })
                return null;

            var image = ImageResult.FromMemory(bytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);

            return image.Data;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException or ArgumentException)
        {
            LogProductUnavailable(logger, exception, layer, coordinates.Zoom, coordinates.Column, coordinates.Row);

            return null;
        }
    }

    private sealed record MapTileCacheKey(GibsMapTileCoordinates Coordinates);
}
