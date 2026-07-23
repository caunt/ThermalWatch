using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using StbImageSharp;
using StbImageWriteSharp;

namespace ThermalWatch.Core;

public sealed class GibsMapTileClient(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<GibsMapTileClient> logger)
{
    public const int MaximumZoom = 9;

    private const int TileSize = 256;
    private const int MaximumTileBytes = 1024 * 1024;
    private const byte NoDataMaximumChannel = 12;
    private static readonly TimeSpan CompleteTileCacheDuration = TimeSpan.FromMinutes(5);
    private static readonly GibsMapProduct[] Products =
    [
        new("MODIS_Terra_CorrectedReflectance_TrueColor"),
        new("MODIS_Aqua_CorrectedReflectance_TrueColor"),
        new("VIIRS_NOAA21_CorrectedReflectance_TrueColor"),
        new("VIIRS_NOAA20_CorrectedReflectance_TrueColor"),
        new("VIIRS_SNPP_CorrectedReflectance_TrueColor")
    ];

    private readonly SemaphoreSlim compositionSlots = new(8, 8);

    public async Task<GibsMapTileResult> GetMapTileAsync(
        GibsMapTileCoordinates coordinates,
        CancellationToken cancellationToken)
    {
        var cacheKey = (Prefix: "gibs:map", coordinates.Zoom, coordinates.X, coordinates.Y);
        if (cache.TryGetValue<GibsMapTileResult>(cacheKey, out var cachedTile)
            && cachedTile is not null)
        {
            return cachedTile;
        }

        await compositionSlots.WaitAsync(cancellationToken);
        try
        {
            if (cache.TryGetValue<GibsMapTileResult>(cacheKey, out cachedTile)
                && cachedTile is not null)
            {
                return cachedTile;
            }

            var tile = await ComposeTileAsync(coordinates, cancellationToken);
            if (tile.Coverage == GibsMapTileCoverage.Complete)
            {
                cache.Set(
                    cacheKey,
                    tile,
                    new MemoryCacheEntryOptions
                    {
                        AbsoluteExpirationRelativeToNow = CompleteTileCacheDuration,
                        Size = tile.PngBytes.Length
                    });
            }

            return tile;
        }
        finally
        {
            compositionSlots.Release();
        }
    }

    private async Task<GibsMapTileResult> ComposeTileAsync(
        GibsMapTileCoordinates coordinates,
        CancellationToken cancellationToken)
    {
        var pixels = new byte[TileSize * TileSize * 4];
        var remainingPixels = TileSize * TileSize;

        foreach (var product in Products)
        {
            var source = await GetTilePixelsAsync(product, coordinates, cancellationToken);
            if (source is null)
                continue;

            for (var offset = 0; offset < pixels.Length; offset += 4)
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

        var coverage = remainingPixels switch
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
            logger.LogDebug(
                "GIBS map tile coverage is {Coverage} for zoom {Zoom}, x {X}, y {Y}",
                coverage,
                coordinates.Zoom,
                coordinates.X,
                coordinates.Y);
        }

        return new(stream.ToArray(), coverage);
    }

    private async Task<byte[]?> GetTilePixelsAsync(
        GibsMapProduct product,
        GibsMapTileCoordinates coordinates,
        CancellationToken cancellationToken)
    {
        try
        {
            var requestUri = BuildTileUri(product, coordinates);
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentType?.MediaType is not { } mediaType
                || !mediaType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)
                || response.Content.Headers.ContentLength > MaximumTileBytes)
            {
                return null;
            }

            var bytes = await ReadLimitedBytesAsync(
                response.Content,
                MaximumTileBytes,
                cancellationToken);
            if (bytes is null)
                return null;

            using var imageInfoStream = new MemoryStream(bytes, writable: false);
            var imageInfo = ImageInfo.FromStream(imageInfoStream);
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
            logger.LogDebug(
                exception,
                "GIBS map product {Product} is unavailable for zoom {Zoom}, x {X}, y {Y}",
                product.Layer,
                coordinates.Zoom,
                coordinates.X,
                coordinates.Y);
            return null;
        }
    }

    private Uri BuildTileUri(GibsMapProduct product, GibsMapTileCoordinates coordinates)
    {
        var baseAddress = httpClient.BaseAddress
            ?? throw new InvalidOperationException("The GIBS HTTP client requires a base address.");
        var path = string.Create(
            CultureInfo.InvariantCulture,
            $"wmts/epsg3857/best/{product.Layer}/default/default/GoogleMapsCompatible_Level9/{coordinates.Zoom}/{coordinates.Y}/{coordinates.X}.jpeg");
        return new(baseAddress, path);
    }

    private static bool IsNoData(byte[] pixels, int offset) =>
        pixels[offset + 3] == 0
        || pixels[offset] <= NoDataMaximumChannel
            && pixels[offset + 1] <= NoDataMaximumChannel
            && pixels[offset + 2] <= NoDataMaximumChannel;

    private static async Task<byte[]?> ReadLimitedBytesAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return result.ToArray();

            if (result.Length + read > maximumBytes)
                return null;

            result.Write(buffer, 0, read);
        }
    }

    private sealed record GibsMapProduct(string Layer);
}

public readonly record struct GibsMapTileCoordinates
{
    private GibsMapTileCoordinates(int zoom, int x, int y)
    {
        Zoom = zoom;
        X = x;
        Y = y;
    }

    public int Zoom { get; }

    public int X { get; }

    public int Y { get; }

    public static bool TryCreate(int zoom, int x, int y, out GibsMapTileCoordinates coordinates)
    {
        var tileCount = zoom is >= 0 and <= GibsMapTileClient.MaximumZoom
            ? 1 << zoom
            : 0;
        if (x >= 0 && x < tileCount && y >= 0 && y < tileCount)
        {
            coordinates = new(zoom, x, y);
            return true;
        }

        coordinates = default;
        return false;
    }
}

public sealed record GibsMapTileResult(byte[] PngBytes, GibsMapTileCoverage Coverage);

public enum GibsMapTileCoverage
{
    Complete,
    Partial,
    None
}
