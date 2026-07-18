using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ThermalWatch.Core;

public sealed class GibsClient(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<GibsClient> logger)
{
    private const int MaximumPreviewBytes = 10 * 1024 * 1024;
    private const int MaximumLandCoverTileBytes = 1024 * 1024;
    private const int MaximumLandCoverDomainCharacters = 1024 * 1024;
    private const int MaximumLandCoverPixels = 1_000_000;
    private const int LandCoverTileSize = 512;
    private const int LandCoverMatrixWidth = 160;
    private const int LandCoverMatrixHeight = 80;
    private const int LandCoverTotalPixelWidth = LandCoverTileSize * LandCoverMatrixWidth;
    private const int LandCoverTotalPixelHeight = LandCoverTileSize * LandCoverMatrixHeight;
    private const double LandCoverPixelDegrees = 360d / LandCoverTotalPixelWidth;
    private const double EarthRadiusKilometers = 6371.0088;
    private const string LandCoverLayer = "MODIS_Combined_L3_IGBP_Land_Cover_Type_Annual";
    private const string LandCoverTileMatrixSet = "500m";
    private const int LandCoverTileMatrix = 7;
    private const byte UnknownLandCoverClass = 254;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly IReadOnlyDictionary<int, byte> LandCoverClassesByRgb =
        new Dictionary<int, byte>
        {
            [Rgb(33, 138, 33)] = 1,
            [Rgb(49, 204, 49)] = 2,
            [Rgb(152, 204, 49)] = 3,
            [Rgb(150, 250, 150)] = 4,
            [Rgb(141, 186, 141)] = 5,
            [Rgb(186, 141, 141)] = 6,
            [Rgb(245, 222, 179)] = 7,
            [Rgb(218, 235, 157)] = 8,
            [Rgb(255, 213, 0)] = 9,
            [Rgb(240, 185, 103)] = 10,
            [Rgb(71, 131, 181)] = 11,
            [Rgb(250, 239, 115)] = 12,
            [Rgb(255, 0, 0)] = 13,
            [Rgb(153, 147, 86)] = 14,
            [Rgb(255, 255, 255)] = 15,
            [Rgb(191, 191, 189)] = 16,
            [Rgb(134, 202, 227)] = 17,
            [Rgb(100, 100, 100)] = 255
        };

    public async Task<GibsPreview> GetPreviewAsync(
        Anomaly anomaly,
        GibsPreviewDimensions dimensions,
        CancellationToken cancellationToken)
    {
        if (!GibsLayers.TryGet(anomaly, out var layers))
            return GibsPreview.Unavailable;

        var bounds = Geography.CreatePreviewBounds(
            anomaly.Latitude,
            anomaly.Longitude,
            dimensions.WidthKilometers,
            dimensions.HeightKilometers);
        if (bounds is null)
            return GibsPreview.Unavailable;

        var date = DateOnly.FromDateTime(anomaly.AcquiredAtUtc.UtcDateTime);
        var previewCacheKey = (
            Prefix: "gibs:preview",
            anomaly.Id,
            dimensions.WidthKilometers,
            dimensions.HeightKilometers,
            dimensions.PixelWidth,
            dimensions.PixelHeight);

        if (cache.TryGetValue<byte[]>(previewCacheKey, out var cachedBytes))
            return new(cachedBytes);

        try
        {
            var baseAvailable = IsLayerAvailableAsync(
                layers.BaseLayer,
                layers.BaseTileMatrixSet,
                date,
                cancellationToken);
            var overlayAvailable = IsLayerAvailableAsync(
                layers.OverlayLayer,
                layers.OverlayTileMatrixSet,
                date,
                cancellationToken);

            await Task.WhenAll(baseAvailable, overlayAvailable);
            if (!baseAvailable.Result || !overlayAvailable.Result)
                return GibsPreview.Unavailable;

            var requestUri = BuildWmsUri(layers, date, bounds.Value, dimensions);
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentType?.MediaType is not { } mediaType
                || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || response.Content.Headers.ContentLength > MaximumPreviewBytes)
            {
                return GibsPreview.Unavailable;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length is 0 or > MaximumPreviewBytes
                || !bytes.AsSpan().StartsWith(PngSignature))
            {
                return GibsPreview.Unavailable;
            }

            cache.Set(
                previewCacheKey,
                bytes,
                new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(2),
                    Size = bytes.Length
                });

            return new(bytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            logger.LogWarning(
                "GIBS preview is temporarily unavailable for {Satellite} at {AcquiredAtUtc}",
                anomaly.Satellite,
                anomaly.AcquiredAtUtc);
            return GibsPreview.Unavailable;
        }
    }

    public async Task<GibsLandCoverResult> GetLandCoverAsync(
        IReadOnlyList<Anomaly> detections,
        double builtUpProximityKilometers,
        CancellationToken cancellationToken)
    {
        if (detections.Count == 0)
            return GibsLandCoverResult.Unavailable();

        try
        {
            var detectionPixels = detections
                .Select(detection => ToLandCoverPixel(detection.Latitude, detection.Longitude))
                .ToImmutableArray();
            var requiredPixels = detectionPixels.ToHashSet();

            foreach (var detection in detections)
            {
                foreach (var pixel in PixelsWithinProximity(
                    detection.Latitude,
                    detection.Longitude,
                    builtUpProximityKilometers))
                {
                    requiredPixels.Add(pixel);
                    if (requiredPixels.Count > MaximumLandCoverPixels)
                        return GibsLandCoverResult.Unavailable();
                }
            }

            var requiredTiles = requiredPixels
                .Select(ToLandCoverTile)
                .Distinct()
                .ToImmutableArray();
            var dateResults = await Task.WhenAll(requiredTiles.Select(tile =>
                GetLandCoverDatesAsync(tile, cancellationToken)));

            if (dateResults.Any(result => !result.IsAvailable))
                return GibsLandCoverResult.Unavailable();

            var commonDates = dateResults[0].Dates.ToHashSet();
            foreach (var result in dateResults.Skip(1))
                commonDates.IntersectWith(result.Dates);

            if (commonDates.Count == 0)
                return GibsLandCoverResult.Unavailable();

            var selectedDate = commonDates.Max();
            var year = selectedDate.Year;
            var tileResults = await Task.WhenAll(requiredTiles.Select(async tile =>
                (Tile: tile, Result: await GetLandCoverTileAsync(
                    tile,
                    selectedDate,
                    cancellationToken))));

            if (tileResults.Any(item => !item.Result.IsAvailable))
                return GibsLandCoverResult.Unavailable(year);

            var tiles = tileResults.ToDictionary(item => item.Tile, item => item.Result.Classes!);
            var detectionClasses = ImmutableArray.CreateBuilder<byte>(detectionPixels.Length);
            foreach (var pixel in detectionPixels)
            {
                var landCoverClass = GetLandCoverClass(tiles, pixel);
                if (IsUnavailableClass(landCoverClass))
                    return GibsLandCoverResult.Unavailable(year);

                detectionClasses.Add(landCoverClass);
            }

            var hasUnavailableProximityPixel = false;
            foreach (var pixel in requiredPixels)
            {
                var landCoverClass = GetLandCoverClass(tiles, pixel);
                if (landCoverClass == 13)
                {
                    return new(
                        true,
                        year,
                        detectionClasses.MoveToImmutable(),
                        true);
                }

                hasUnavailableProximityPixel |= IsUnavailableClass(landCoverClass);
            }

            return hasUnavailableProximityPixel
                ? GibsLandCoverResult.Unavailable(year)
                : new(true, year, detectionClasses.MoveToImmutable(), false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return GibsLandCoverResult.Unavailable();
        }
    }

    private async Task<LandCoverDatesResult> GetLandCoverDatesAsync(
        LandCoverTile tile,
        CancellationToken cancellationToken)
    {
        var cacheKey = (Prefix: "gibs:land-cover:dates", tile.Row, tile.Column);
        if (cache.TryGetValue<LandCoverDatesResult>(cacheKey, out var cachedResult))
            return cachedResult;

        LandCoverDatesResult result;
        try
        {
            var bounds = GetLandCoverTileBounds(tile);
            var requestUri = new Uri(
                httpClient.BaseAddress!,
                $"wmts/epsg4326/best/1.0.0/{LandCoverLayer}/default/{LandCoverTileMatrixSet}/{bounds.ToInvariantString()}/all.xml");
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            result = LandCoverDatesResult.Unavailable;
            if (response.IsSuccessStatusCode
                && response.Content.Headers.ContentType?.MediaType is { } mediaType
                && mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)
                && response.Content.Headers.ContentLength <= MaximumLandCoverDomainCharacters)
            {
                var xml = await response.Content.ReadAsStringAsync(cancellationToken);
                if (xml.Length <= MaximumLandCoverDomainCharacters
                    && TryParseAnnualDates(xml, out var dates))
                {
                    result = new(true, dates);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            result = LandCoverDatesResult.Unavailable;
        }

        cache.Set(
            cacheKey,
            result,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = result.IsAvailable
                    ? TimeSpan.FromHours(12)
                    : TimeSpan.FromMinutes(5),
                Size = 1
            });
        return result;
    }

    private async Task<LandCoverTileResult> GetLandCoverTileAsync(
        LandCoverTile tile,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        var cacheKey = (
            Prefix: "gibs:land-cover:tile",
            Date: date,
            tile.Row,
            tile.Column);
        if (cache.TryGetValue<LandCoverTileResult>(cacheKey, out var cachedResult)
            && cachedResult is not null)
            return cachedResult;

        LandCoverTileResult result;
        try
        {
            var requestUri = new Uri(
                httpClient.BaseAddress!,
                $"wmts/epsg4326/best/{LandCoverLayer}/default/{date:yyyy-MM-dd}/{LandCoverTileMatrixSet}/{LandCoverTileMatrix}/{tile.Row}/{tile.Column}.png");
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            result = LandCoverTileResult.Unavailable;
            if (response.IsSuccessStatusCode
                && response.Content.Headers.ContentType?.MediaType is { } mediaType
                && mediaType.Equals("image/png", StringComparison.OrdinalIgnoreCase)
                && response.Content.Headers.ContentLength <= MaximumLandCoverTileBytes
                && await ReadLimitedBytesAsync(
                    response.Content,
                    MaximumLandCoverTileBytes,
                    cancellationToken) is { } pngBytes
                && TryDecodeLandCoverPng(pngBytes, out var classes))
            {
                result = new(true, classes);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            result = LandCoverTileResult.Unavailable;
        }

        cache.Set(
            cacheKey,
            result,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = result.IsAvailable
                    ? TimeSpan.FromHours(24)
                    : TimeSpan.FromMinutes(5),
                Size = result.Classes?.Length ?? 1
            });
        return result;
    }

    private static bool TryParseAnnualDates(
        string xml,
        out ImmutableArray<DateOnly> dates)
    {
        dates = [];
        try
        {
            var domain = XDocument.Parse(xml, LoadOptions.None)
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "Domain")
                ?.Value;
            if (string.IsNullOrWhiteSpace(domain))
                return false;

            var parsedDates = new HashSet<DateOnly>();
            foreach (var period in domain.Split(
                ',',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var values = period.Split('/', StringSplitOptions.TrimEntries);
                if (!TryReadDate(values[0], out var start))
                    return false;

                if (values.Length == 1)
                {
                    parsedDates.Add(start);
                    continue;
                }

                if (values.Length != 3
                    || !TryReadDate(values[1], out var end)
                    || values[2] != "P1Y"
                    || end < start)
                {
                    return false;
                }

                for (var date = start; date <= end; date = date.AddYears(1))
                    parsedDates.Add(date);
            }

            dates = [.. parsedDates.Order()];
            return dates.Length > 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Xml.XmlException)
        {
            return false;
        }
    }

    private static LandCoverPixel ToLandCoverPixel(double latitude, double longitude)
    {
        var x = Math.Clamp(
            (int)Math.Floor((longitude + 180) / LandCoverPixelDegrees),
            0,
            LandCoverTotalPixelWidth - 1);
        var y = Math.Clamp(
            (int)Math.Floor((90 - latitude) / LandCoverPixelDegrees),
            0,
            LandCoverTotalPixelHeight - 1);
        return new(x, y);
    }

    private static IEnumerable<LandCoverPixel> PixelsWithinProximity(
        double latitude,
        double longitude,
        double proximityKilometers)
    {
        var angularRadius = proximityKilometers / EarthRadiusKilometers;
        var latitudeRadius = angularRadius * 180 / Math.PI;
        var reachesPole = Math.Abs(latitude) + latitudeRadius >= 90;
        var longitudeRadius = reachesPole
            ? 180
            : Math.Asin(Math.Min(1, Math.Sin(angularRadius) / Math.Cos(latitude * Math.PI / 180)))
                * 180 / Math.PI;
        var north = Math.Min(90, latitude + latitudeRadius);
        var south = Math.Max(-90, latitude - latitudeRadius);
        var firstRow = ToLandCoverPixel(north, longitude).Y;
        var lastRow = ToLandCoverPixel(south, longitude).Y;
        var centerColumn = ToLandCoverPixel(latitude, longitude).X;
        var columnRadius = longitudeRadius >= 180
            ? LandCoverTotalPixelWidth / 2
            : (int)Math.Ceiling(longitudeRadius / LandCoverPixelDegrees) + 1;

        for (var row = Math.Max(0, firstRow - 1);
             row <= Math.Min(LandCoverTotalPixelHeight - 1, lastRow + 1);
             row++)
        {
            var firstOffset = columnRadius >= LandCoverTotalPixelWidth / 2
                ? 0
                : -columnRadius;
            var lastOffset = columnRadius >= LandCoverTotalPixelWidth / 2
                ? LandCoverTotalPixelWidth - 1
                : columnRadius;

            for (var offset = firstOffset; offset <= lastOffset; offset++)
            {
                var column = columnRadius >= LandCoverTotalPixelWidth / 2
                    ? offset
                    : Modulo(centerColumn + offset, LandCoverTotalPixelWidth);
                var pixel = new LandCoverPixel(column, row);
                if (DistanceToLandCoverPixel(latitude, longitude, pixel) <= proximityKilometers)
                    yield return pixel;
            }
        }
    }

    private static double DistanceToLandCoverPixel(
        double latitude,
        double longitude,
        LandCoverPixel pixel)
    {
        var north = 90 - pixel.Y * LandCoverPixelDegrees;
        var south = north - LandCoverPixelDegrees;
        var centerLongitude = -180 + (pixel.X + 0.5) * LandCoverPixelDegrees;
        var longitudeDelta = NormalizeLongitudeDelta(centerLongitude - longitude);
        var nearestLongitudeDelta = Math.CopySign(
            Math.Max(0, Math.Abs(longitudeDelta) - LandCoverPixelDegrees / 2),
            longitudeDelta);
        var nearestLatitude = Math.Clamp(latitude, south, north);
        var nearestLongitude = longitude + nearestLongitudeDelta;
        return Geography.HaversineKilometers(
            latitude,
            longitude,
            nearestLatitude,
            nearestLongitude);
    }

    private static LandCoverTile ToLandCoverTile(LandCoverPixel pixel) =>
        new(pixel.Y / LandCoverTileSize, pixel.X / LandCoverTileSize);

    private static GeographicBounds GetLandCoverTileBounds(LandCoverTile tile)
    {
        var tileDegrees = LandCoverTileSize * LandCoverPixelDegrees;
        var west = -180 + tile.Column * tileDegrees;
        var east = west + tileDegrees;
        var north = 90 - tile.Row * tileDegrees;
        var south = north - tileDegrees;
        return new(west, south, east, north);
    }

    private static byte GetLandCoverClass(
        IReadOnlyDictionary<LandCoverTile, byte[]> tiles,
        LandCoverPixel pixel)
    {
        var tile = ToLandCoverTile(pixel);
        var classes = tiles[tile];
        var localX = pixel.X % LandCoverTileSize;
        var localY = pixel.Y % LandCoverTileSize;
        return classes[localY * LandCoverTileSize + localX];
    }

    private static bool IsUnavailableClass(byte landCoverClass) =>
        landCoverClass is UnknownLandCoverClass or 255;

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

    private static bool TryDecodeLandCoverPng(byte[] pngBytes, out byte[] classes)
    {
        classes = [];
        try
        {
            if (!pngBytes.AsSpan().StartsWith(PngSignature))
                return false;

            byte[]? palette = null;
            byte[]? transparency = null;
            using var compressed = new MemoryStream();
            var offset = PngSignature.Length;
            var validHeader = false;
            var reachedEnd = false;

            while (offset <= pngBytes.Length - 12)
            {
                var length = BinaryPrimitives.ReadUInt32BigEndian(pngBytes.AsSpan(offset, 4));
                if (length > int.MaxValue || offset + 12L + length > pngBytes.Length)
                    return false;

                var chunkLength = (int)length;
                var type = pngBytes.AsSpan(offset + 4, 4);
                var data = pngBytes.AsSpan(offset + 8, chunkLength);
                if (type.SequenceEqual("IHDR"u8))
                {
                    validHeader = chunkLength == 13
                        && BinaryPrimitives.ReadUInt32BigEndian(data[..4]) == LandCoverTileSize
                        && BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4)) == LandCoverTileSize
                        && data[8] == 8
                        && data[9] == 3
                        && data[10] == 0
                        && data[11] == 0
                        && data[12] == 0;
                }
                else if (type.SequenceEqual("PLTE"u8))
                {
                    if (chunkLength is 0 or > 768 || chunkLength % 3 != 0)
                        return false;
                    palette = data.ToArray();
                }
                else if (type.SequenceEqual("tRNS"u8))
                {
                    if (chunkLength > 256)
                        return false;
                    transparency = data.ToArray();
                }
                else if (type.SequenceEqual("IDAT"u8))
                {
                    compressed.Write(data);
                }
                else if (type.SequenceEqual("IEND"u8))
                {
                    reachedEnd = true;
                    break;
                }

                offset += chunkLength + 12;
            }

            if (!validHeader || !reachedEnd || palette is null || compressed.Length == 0)
                return false;

            compressed.Position = 0;
            using var decompressed = new ZLibStream(compressed, CompressionMode.Decompress);
            var previous = new byte[LandCoverTileSize];
            var current = new byte[LandCoverTileSize];
            classes = new byte[LandCoverTileSize * LandCoverTileSize];

            for (var row = 0; row < LandCoverTileSize; row++)
            {
                var filter = decompressed.ReadByte();
                if (filter < 0)
                    return false;
                decompressed.ReadExactly(current);
                ApplyPngFilter((byte)filter, current, previous);

                for (var column = 0; column < LandCoverTileSize; column++)
                {
                    var paletteIndex = current[column];
                    var paletteOffset = paletteIndex * 3;
                    var alpha = transparency is not null && paletteIndex < transparency.Length
                        ? transparency[paletteIndex]
                        : byte.MaxValue;
                    if (alpha != byte.MaxValue || paletteOffset + 2 >= palette.Length)
                    {
                        classes[row * LandCoverTileSize + column] = UnknownLandCoverClass;
                        continue;
                    }

                    var rgb = Rgb(
                        palette[paletteOffset],
                        palette[paletteOffset + 1],
                        palette[paletteOffset + 2]);
                    classes[row * LandCoverTileSize + column] =
                        LandCoverClassesByRgb.GetValueOrDefault(rgb, UnknownLandCoverClass);
                }

                (previous, current) = (current, previous);
            }

            return decompressed.ReadByte() == -1;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
        {
            classes = [];
            return false;
        }
    }

    private static void ApplyPngFilter(byte filter, Span<byte> current, ReadOnlySpan<byte> previous)
    {
        for (var index = 0; index < current.Length; index++)
        {
            var left = index == 0 ? 0 : current[index - 1];
            var up = previous[index];
            var upperLeft = index == 0 ? 0 : previous[index - 1];
            current[index] = filter switch
            {
                0 => current[index],
                1 => unchecked((byte)(current[index] + left)),
                2 => unchecked((byte)(current[index] + up)),
                3 => unchecked((byte)(current[index] + (left + up) / 2)),
                4 => unchecked((byte)(current[index] + PaethPredictor(left, up, upperLeft))),
                _ => throw new InvalidDataException("Unsupported PNG filter.")
            };
        }
    }

    private static int PaethPredictor(int left, int up, int upperLeft)
    {
        var prediction = left + up - upperLeft;
        var leftDistance = Math.Abs(prediction - left);
        var upDistance = Math.Abs(prediction - up);
        var upperLeftDistance = Math.Abs(prediction - upperLeft);
        return leftDistance <= upDistance && leftDistance <= upperLeftDistance
            ? left
            : upDistance <= upperLeftDistance
                ? up
                : upperLeft;
    }

    private static int Modulo(int value, int divisor) =>
        (value % divisor + divisor) % divisor;

    private static double NormalizeLongitudeDelta(double value) =>
        (value + 540) % 360 - 180;

    private static int Rgb(byte red, byte green, byte blue) =>
        red << 16 | green << 8 | blue;

    private readonly record struct LandCoverPixel(int X, int Y);

    private readonly record struct LandCoverTile(int Row, int Column);

    private readonly record struct LandCoverDatesResult(
        bool IsAvailable,
        ImmutableArray<DateOnly> Dates)
    {
        public static LandCoverDatesResult Unavailable { get; } = new(false, []);
    }

    private sealed record LandCoverTileResult(bool IsAvailable, byte[]? Classes)
    {
        public static LandCoverTileResult Unavailable { get; } = new(false, null);
    }

    private async Task<bool> IsLayerAvailableAsync(
        string layer,
        string tileMatrixSet,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"gibs:availability:{layer}:{date:yyyy-MM-dd}";
        if (cache.TryGetValue<bool>(cacheKey, out var available))
            return available;

        var dateText = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var requestUri = new Uri(
            httpClient.BaseAddress!,
            $"wmts/epsg4326/best/1.0.0/{Uri.EscapeDataString(layer)}/default/{tileMatrixSet}/all/{dateText}--{dateText}.xml");

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        available = false;
        if (response.IsSuccessStatusCode
            && response.Content.Headers.ContentType?.MediaType is { } mediaType
            && mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase))
        {
            var xml = await response.Content.ReadAsStringAsync(cancellationToken);
            if (xml.Length <= 1_000_000)
                available = DomainContainsDate(xml, date);
        }

        cache.Set(
            cacheKey,
            available,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = available
                    ? TimeSpan.FromHours(12)
                    : TimeSpan.FromMinutes(5),
                Size = 1
            });

        return available;
    }

    private static bool DomainContainsDate(string xml, DateOnly requestedDate)
    {
        try
        {
            var document = XDocument.Parse(xml, LoadOptions.None);
            var domain = document
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "Domain")
                ?.Value;

            if (string.IsNullOrWhiteSpace(domain))
                return false;

            foreach (var period in domain.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var values = period.Split('/', StringSplitOptions.TrimEntries);
                if (TryReadDate(values[0], out var start)
                    && (values.Length == 1 || TryReadDate(values[1], out var end) && requestedDate <= end)
                    && requestedDate >= start)
                {
                    return true;
                }
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Xml.XmlException)
        {
            return false;
        }

        return false;
    }

    private static bool TryReadDate(string value, out DateOnly date)
    {
        if (value.Length >= 10)
            value = value[..10];

        return DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }

    private static Uri BuildWmsUri(
        GibsLayerPair layers,
        DateOnly date,
        GeographicBounds bounds,
        GibsPreviewDimensions dimensions)
    {
        var parameters = new Dictionary<string, string>
        {
            ["SERVICE"] = "WMS",
            ["REQUEST"] = "GetMap",
            ["VERSION"] = "1.1.1",
            ["SRS"] = "EPSG:4326",
            ["FORMAT"] = "image/png",
            ["WIDTH"] = dimensions.PixelWidth.ToString(CultureInfo.InvariantCulture),
            ["HEIGHT"] = dimensions.PixelHeight.ToString(CultureInfo.InvariantCulture),
            ["TIME"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["BBOX"] = bounds.ToInvariantString(),
            ["LAYERS"] = $"{layers.BaseLayer},{layers.OverlayLayer}",
            ["STYLES"] = "default,size5"
        };
        var query = string.Join('&', parameters.Select(pair =>
            $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));

        return new($"https://gibs.earthdata.nasa.gov/wms/epsg4326/best/wms.cgi?{query}");
    }
}

public readonly record struct GibsLayerPair(
    string BaseLayer,
    string BaseTileMatrixSet,
    string OverlayLayer,
    string OverlayTileMatrixSet);

public static class GibsLayers
{
    public static bool TryGet(Anomaly anomaly, out GibsLayerPair layers)
    {
        var night = anomaly.DayNight == "N";

        if (anomaly.Source == "MODIS_NRT")
        {
            if (anomaly.Satellite.Equals("Terra", StringComparison.OrdinalIgnoreCase)
                || anomaly.Satellite.Equals("T", StringComparison.OrdinalIgnoreCase))
            {
                layers = new(
                    night
                        ? "MODIS_Terra_Brightness_Temp_Band31_Night"
                        : "MODIS_Terra_CorrectedReflectance_TrueColor",
                    night ? "1km" : "250m",
                    "MODIS_Terra_Thermal_Anomalies_All",
                    "1km");
                return true;
            }

            if (anomaly.Satellite.Equals("Aqua", StringComparison.OrdinalIgnoreCase)
                || anomaly.Satellite.Equals("A", StringComparison.OrdinalIgnoreCase))
            {
                layers = new(
                    night
                        ? "MODIS_Aqua_Brightness_Temp_Band31_Night"
                        : "MODIS_Aqua_CorrectedReflectance_TrueColor",
                    night ? "1km" : "250m",
                    "MODIS_Aqua_Thermal_Anomalies_All",
                    "1km");
                return true;
            }

            layers = default;
            return false;
        }

        var prefix = anomaly.Source switch
        {
            "VIIRS_SNPP_NRT" => "VIIRS_SNPP",
            "VIIRS_NOAA20_NRT" => "VIIRS_NOAA20",
            "VIIRS_NOAA21_NRT" => "VIIRS_NOAA21",
            _ => null
        };

        if (prefix is null)
        {
            layers = default;
            return false;
        }

        layers = new(
            night
                ? $"{prefix}_Brightness_Temp_BandI5_Night"
                : $"{prefix}_CorrectedReflectance_TrueColor",
            "250m",
            $"{prefix}_Thermal_Anomalies_375m_All",
            "500m");
        return true;
    }
}
