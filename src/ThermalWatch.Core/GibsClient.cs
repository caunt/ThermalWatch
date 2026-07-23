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
    private const int MaximumPreviewProbeBytes = 256 * 1024;
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
    private const int PreviewProbePixelSize = 64;
    private const byte PreviewNoDataMaximumChannel = 8;
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
        var layerCandidates = GibsLayers.GetCandidates(anomaly);
        if (layerCandidates.IsDefaultOrEmpty)
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

        if (cache.TryGetValue<GibsPreview>(previewCacheKey, out var cachedPreview)
            && cachedPreview is not null)
        {
            return cachedPreview;
        }

        try
        {
            var representativeLayers = layerCandidates[0];
            if (!await IsLayerAvailableAsync(
                representativeLayers.OverlayLayer,
                representativeLayers.OverlayTileMatrixSet,
                date,
                cancellationToken))
            {
                return GibsPreview.Unavailable;
            }

            GibsLayerCandidate? selectedLayers = null;
            foreach (var candidate in layerCandidates)
            {
                if (!await IsLayerAvailableAsync(
                    candidate.BaseLayer,
                    candidate.BaseTileMatrixSet,
                    date,
                    cancellationToken))
                {
                    continue;
                }

                if (!await IsBaseLayerUsableAsync(
                    candidate,
                    date,
                    bounds.Value,
                    cancellationToken))
                {
                    logger.LogDebug(
                        "GIBS base imagery has insufficient usable coverage for {BaseSatellite} on {Date}",
                        candidate.BaseSource.Satellite,
                        date);
                    continue;
                }

                selectedLayers = candidate;
                break;
            }

            if (selectedLayers is not { } selected)
                return GibsPreview.Unavailable;

            if (selected.BaseSource != representativeLayers.BaseSource)
            {
                logger.LogInformation(
                    "Selected fallback GIBS base imagery from {BaseSatellite} for {OverlaySatellite} thermal overlay on {Date}",
                    selected.BaseSource.Satellite,
                    representativeLayers.BaseSource.Satellite,
                    date);
            }

            var requestUri = BuildWmsUri(selected, date, bounds.Value, dimensions);
            var bytes = await GetPngAsync(requestUri, MaximumPreviewBytes, cancellationToken);
            if (bytes is null
                || !HasExpectedPreviewPng(
                    bytes,
                    dimensions.PixelWidth,
                    dimensions.PixelHeight))
            {
                return GibsPreview.Unavailable;
            }

            var preview = new GibsPreview(bytes, selected.BaseSource);

            cache.Set(
                previewCacheKey,
                preview,
                new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(2),
                    Size = bytes.Length
                });

            return preview;
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

    private async Task<bool> IsBaseLayerUsableAsync(
        GibsLayerCandidate layers,
        DateOnly date,
        GeographicBounds bounds,
        CancellationToken cancellationToken)
    {
        var dimensions = new GibsPreviewDimensions(
            0,
            0,
            PreviewProbePixelSize,
            PreviewProbePixelSize);
        var requestUri = BuildBaseProbeWmsUri(layers, date, bounds, dimensions);
        var bytes = await GetPngAsync(requestUri, MaximumPreviewProbeBytes, cancellationToken);
        return bytes is not null && HasUsablePreviewCoverage(bytes);
    }

    private async Task<byte[]?> GetPngAsync(
        Uri requestUri,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode
            || response.Content.Headers.ContentType?.MediaType is not { } mediaType
            || !mediaType.Equals("image/png", StringComparison.OrdinalIgnoreCase)
            || response.Content.Headers.ContentLength > maximumBytes)
        {
            return null;
        }

        return await ReadLimitedBytesAsync(response.Content, maximumBytes, cancellationToken);
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
            var requiredPixels = detections
                .Select(detection => ToLandCoverPixel(detection.Latitude, detection.Longitude))
                .ToHashSet();

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
            var sampledClasses = ImmutableArray.CreateBuilder<byte>(requiredPixels.Count);
            var hasBuiltUpWithinProximity = false;
            foreach (var pixel in requiredPixels)
            {
                var landCoverClass = GetLandCoverClass(tiles, pixel);
                if (IsUnavailableClass(landCoverClass))
                    return GibsLandCoverResult.Unavailable(year);

                sampledClasses.Add(landCoverClass);
                hasBuiltUpWithinProximity |= landCoverClass == 13;
            }

            return new(
                true,
                year,
                sampledClasses.MoveToImmutable(),
                hasBuiltUpWithinProximity);
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

    private static bool HasExpectedPreviewPng(
        byte[] pngBytes,
        int expectedWidth,
        int expectedHeight)
    {
        if (expectedWidth <= 0
            || expectedHeight <= 0
            || !TryReadPreviewPngHeader(
                pngBytes,
                out var width,
                out var height,
                out _))
        {
            return false;
        }

        if (width != expectedWidth || height != expectedHeight)
            return false;

        var offset = PngSignature.Length;
        var hasImageData = false;
        while (offset <= pngBytes.Length - 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(pngBytes.AsSpan(offset, 4));
            if (length > int.MaxValue || offset + 12L + length > pngBytes.Length)
                return false;

            var chunkLength = (int)length;
            var type = pngBytes.AsSpan(offset + 4, 4);
            hasImageData |= type.SequenceEqual("IDAT"u8) && chunkLength > 0;
            offset += chunkLength + 12;
            if (type.SequenceEqual("IEND"u8))
                return chunkLength == 0 && hasImageData && offset == pngBytes.Length;
        }

        return false;
    }

    private static bool HasUsablePreviewCoverage(byte[] pngBytes)
    {
        try
        {
            if (!TryReadPreviewPngHeader(
                pngBytes,
                out var width,
                out var height,
                out var bytesPerPixel)
                || width != PreviewProbePixelSize
                || height != PreviewProbePixelSize)
            {
                return false;
            }

            using var compressed = new MemoryStream();
            var offset = PngSignature.Length;
            var reachedEnd = false;
            while (offset <= pngBytes.Length - 12)
            {
                var length = BinaryPrimitives.ReadUInt32BigEndian(pngBytes.AsSpan(offset, 4));
                if (length > int.MaxValue || offset + 12L + length > pngBytes.Length)
                    return false;

                var chunkLength = (int)length;
                var type = pngBytes.AsSpan(offset + 4, 4);
                var data = pngBytes.AsSpan(offset + 8, chunkLength);
                if (type.SequenceEqual("IDAT"u8))
                {
                    compressed.Write(data);
                    if (compressed.Length > MaximumPreviewProbeBytes)
                        return false;
                }
                else if (type.SequenceEqual("IEND"u8))
                {
                    reachedEnd = chunkLength == 0;
                    break;
                }

                offset += chunkLength + 12;
            }

            if (!reachedEnd || compressed.Length == 0)
                return false;

            var rowLength = checked(width * bytesPerPixel);
            var totalPixels = checked(width * height);
            var requiredUsablePixels = (totalPixels + 1) / 2;
            var usablePixels = 0;
            compressed.Position = 0;
            using var decompressed = new ZLibStream(compressed, CompressionMode.Decompress);
            var previous = new byte[rowLength];
            var current = new byte[rowLength];

            for (var row = 0; row < height; row++)
            {
                var filter = decompressed.ReadByte();
                if (filter < 0)
                    return false;
                decompressed.ReadExactly(current);
                ApplyPngFilter((byte)filter, current, previous, bytesPerPixel);

                for (var pixel = 0; pixel < width; pixel++)
                {
                    var pixelOffset = pixel * bytesPerPixel;
                    var hasAlpha = bytesPerPixel == 4;
                    var alphaUsable = !hasAlpha
                        || current[pixelOffset + 3] > PreviewNoDataMaximumChannel;
                    var colorUsable = current[pixelOffset] > PreviewNoDataMaximumChannel
                        || current[pixelOffset + 1] > PreviewNoDataMaximumChannel
                        || current[pixelOffset + 2] > PreviewNoDataMaximumChannel;
                    if (alphaUsable && colorUsable)
                        usablePixels++;
                }

                (previous, current) = (current, previous);
            }

            return decompressed.ReadByte() == -1
                && usablePixels >= requiredUsablePixels;
        }
        catch (Exception exception) when (exception is
            IOException or
            InvalidDataException or
            ArgumentException or
            OverflowException)
        {
            return false;
        }
    }

    private static bool TryReadPreviewPngHeader(
        byte[] pngBytes,
        out int width,
        out int height,
        out int bytesPerPixel)
    {
        width = 0;
        height = 0;
        bytesPerPixel = 0;
        if (!pngBytes.AsSpan().StartsWith(PngSignature)
            || pngBytes.Length < PngSignature.Length + 25)
        {
            return false;
        }

        var headerLength = BinaryPrimitives.ReadUInt32BigEndian(
            pngBytes.AsSpan(PngSignature.Length, 4));
        var headerType = pngBytes.AsSpan(PngSignature.Length + 4, 4);
        var header = pngBytes.AsSpan(PngSignature.Length + 8, 13);
        if (headerLength != 13
            || !headerType.SequenceEqual("IHDR"u8)
            || header[8] != 8
            || header[10] != 0
            || header[11] != 0
            || header[12] != 0)
        {
            return false;
        }

        var unsignedWidth = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
        var unsignedHeight = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(4, 4));
        if (unsignedWidth is 0 or > int.MaxValue || unsignedHeight is 0 or > int.MaxValue)
            return false;

        bytesPerPixel = header[9] switch
        {
            2 => 3,
            6 => 4,
            _ => 0
        };
        if (bytesPerPixel == 0)
            return false;

        width = (int)unsignedWidth;
        height = (int)unsignedHeight;
        return true;
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
                ApplyPngFilter((byte)filter, current, previous, 1);

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

    private static void ApplyPngFilter(
        byte filter,
        Span<byte> current,
        ReadOnlySpan<byte> previous,
        int bytesPerPixel)
    {
        for (var index = 0; index < current.Length; index++)
        {
            var left = index < bytesPerPixel ? 0 : current[index - bytesPerPixel];
            var up = previous[index];
            var upperLeft = index < bytesPerPixel ? 0 : previous[index - bytesPerPixel];
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
        GibsLayerCandidate layers,
        DateOnly date,
        GeographicBounds bounds,
        GibsPreviewDimensions dimensions) =>
        BuildWmsUri(
            $"{layers.BaseLayer},{layers.OverlayLayer}",
            "default,size5",
            date,
            bounds,
            dimensions);

    private static Uri BuildBaseProbeWmsUri(
        GibsLayerCandidate layers,
        DateOnly date,
        GeographicBounds bounds,
        GibsPreviewDimensions dimensions) =>
        BuildWmsUri(
            layers.BaseLayer,
            "default",
            date,
            bounds,
            dimensions);

    private static Uri BuildWmsUri(
        string layerNames,
        string styles,
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
            ["LAYERS"] = layerNames,
            ["STYLES"] = styles
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

internal readonly record struct GibsLayerCandidate(
    string BaseLayer,
    string BaseTileMatrixSet,
    string OverlayLayer,
    string OverlayTileMatrixSet,
    GibsPreviewSource BaseSource);

public static class GibsLayers
{
    private static readonly GibsSourceDefinition Terra = new(
        "MODIS_NRT",
        "Terra",
        "MODIS",
        "MODIS_Terra_CorrectedReflectance_TrueColor",
        "250m",
        "MODIS_Terra_Brightness_Temp_Band31_Night",
        "1km",
        "MODIS_Terra_Thermal_Anomalies_All",
        "1km");
    private static readonly GibsSourceDefinition Aqua = new(
        "MODIS_NRT",
        "Aqua",
        "MODIS",
        "MODIS_Aqua_CorrectedReflectance_TrueColor",
        "250m",
        "MODIS_Aqua_Brightness_Temp_Band31_Night",
        "1km",
        "MODIS_Aqua_Thermal_Anomalies_All",
        "1km");
    private static readonly GibsSourceDefinition Noaa21 = CreateViirs(
        "VIIRS_NOAA21_NRT",
        "NOAA-21",
        "VIIRS_NOAA21");
    private static readonly GibsSourceDefinition Noaa20 = CreateViirs(
        "VIIRS_NOAA20_NRT",
        "NOAA-20",
        "VIIRS_NOAA20");
    private static readonly GibsSourceDefinition SuomiNpp = CreateViirs(
        "VIIRS_SNPP_NRT",
        "Suomi-NPP",
        "VIIRS_SNPP");
    private static readonly ImmutableArray<GibsSourceDefinition> ModisSources = [Terra, Aqua];
    private static readonly ImmutableArray<GibsSourceDefinition> ViirsSources =
        [Noaa21, Noaa20, SuomiNpp];

    public static bool TryGet(Anomaly anomaly, out GibsLayerPair layers)
    {
        var candidates = GetCandidates(anomaly);
        if (!candidates.IsDefaultOrEmpty)
        {
            var candidate = candidates[0];
            layers = new(
                candidate.BaseLayer,
                candidate.BaseTileMatrixSet,
                candidate.OverlayLayer,
                candidate.OverlayTileMatrixSet);
            return true;
        }

        layers = default;
        return false;
    }

    internal static ImmutableArray<GibsLayerCandidate> GetCandidates(Anomaly anomaly)
    {
        var night = anomaly.DayNight == "N";
        var family = anomaly.Source == "MODIS_NRT" ? ModisSources : ViirsSources;
        var otherFamily = anomaly.Source == "MODIS_NRT" ? ViirsSources : ModisSources;
        var representative = family.FirstOrDefault(source => source.Matches(anomaly));
        if (representative is null)
            return [];

        var candidates = ImmutableArray.CreateBuilder<GibsLayerCandidate>(
            ModisSources.Length + ViirsSources.Length);
        AddCandidate(candidates, representative, representative, night);
        foreach (var source in family)
        {
            if (source != representative)
                AddCandidate(candidates, source, representative, night);
        }

        foreach (var source in otherFamily)
            AddCandidate(candidates, source, representative, night);

        return candidates.MoveToImmutable();
    }

    private static void AddCandidate(
        ImmutableArray<GibsLayerCandidate>.Builder candidates,
        GibsSourceDefinition baseSource,
        GibsSourceDefinition representative,
        bool night)
    {
        candidates.Add(new(
            night ? baseSource.NightBaseLayer : baseSource.DayBaseLayer,
            night ? baseSource.NightBaseTileMatrixSet : baseSource.DayBaseTileMatrixSet,
            representative.OverlayLayer,
            representative.OverlayTileMatrixSet,
            new(
                baseSource.FirmsSource,
                baseSource.Satellite,
                baseSource.Instrument)));
    }

    private static GibsSourceDefinition CreateViirs(
        string firmsSource,
        string satellite,
        string prefix) =>
        new(
            firmsSource,
            satellite,
            "VIIRS",
            $"{prefix}_CorrectedReflectance_TrueColor",
            "250m",
            $"{prefix}_Brightness_Temp_BandI5_Night",
            "250m",
            $"{prefix}_Thermal_Anomalies_375m_All",
            "500m");

    private sealed record GibsSourceDefinition(
        string FirmsSource,
        string Satellite,
        string Instrument,
        string DayBaseLayer,
        string DayBaseTileMatrixSet,
        string NightBaseLayer,
        string NightBaseTileMatrixSet,
        string OverlayLayer,
        string OverlayTileMatrixSet)
    {
        public bool Matches(Anomaly anomaly)
        {
            if (!anomaly.Source.Equals(FirmsSource, StringComparison.Ordinal))
                return false;

            if (FirmsSource != "MODIS_NRT")
                return true;

            return anomaly.Satellite.Equals(Satellite, StringComparison.OrdinalIgnoreCase)
                || Satellite == "Terra"
                    && anomaly.Satellite.Equals("T", StringComparison.OrdinalIgnoreCase)
                || Satellite == "Aqua"
                    && anomaly.Satellite.Equals("A", StringComparison.OrdinalIgnoreCase);
        }
    }
}
