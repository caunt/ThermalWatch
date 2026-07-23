using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using StbImageSharp;

namespace ThermalWatch.Core;

public sealed partial class GibsClient(
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
    private const string LandCoverLayer = "MODIS_Combined_L3_IGBP_Land_Cover_Type_Annual";
    private const string LandCoverTileMatrixSet = "500m";
    private const int LandCoverTileMatrix = 7;
    private const int PreviewProbePixelSize = 64;
    private const byte PreviewNoDataMaximumChannel = 8;
    private const byte UnknownLandCoverClass = 254;
    private const double MaximumTrigonometricRatio = 1;
    private const double MaximumLatitudeDegrees = 90;
    private const int MinimumLandCoverPixelIndex = 0;
    private const double MinimumDistanceDegrees = 0;
    private static readonly byte[] s_pngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly IReadOnlyDictionary<int, byte> s_landCoverClassesByRgb =
        new Dictionary<int, byte>
        {
            [Rgb(red: 33, green: 138, blue: 33)] = 1,
            [Rgb(red: 49, green: 204, blue: 49)] = 2,
            [Rgb(red: 152, green: 204, blue: 49)] = 3,
            [Rgb(red: 150, green: 250, blue: 150)] = 4,
            [Rgb(red: 141, green: 186, blue: 141)] = 5,
            [Rgb(red: 186, green: 141, blue: 141)] = 6,
            [Rgb(red: 245, green: 222, blue: 179)] = 7,
            [Rgb(red: 218, green: 235, blue: 157)] = 8,
            [Rgb(red: 255, green: 213, blue: 0)] = 9,
            [Rgb(red: 240, green: 185, blue: 103)] = 10,
            [Rgb(red: 71, green: 131, blue: 181)] = 11,
            [Rgb(red: 250, green: 239, blue: 115)] = 12,
            [Rgb(red: 255, green: 0, blue: 0)] = 13,
            [Rgb(red: 153, green: 147, blue: 86)] = 14,
            [Rgb(red: 255, green: 255, blue: 255)] = 15,
            [Rgb(red: 191, green: 191, blue: 189)] = 16,
            [Rgb(red: 134, green: 202, blue: 227)] = 17,
            [Rgb(red: 100, green: 100, blue: 100)] = 255
        };

    public async Task<GibsPreview> GetPreviewAsync(
        Anomaly anomaly,
        GibsPreviewDimensions dimensions,
        CancellationToken cancellationToken)
    {
        ImmutableArray<GibsLayerCandidate> layerCandidates = GibsLayers.GetCandidates(anomaly);
        if (layerCandidates.IsDefaultOrEmpty)
            return GibsPreview.Unavailable;

        GeographicBounds? bounds = Geography.CreatePreviewBounds(
            anomaly.Latitude,
            anomaly.Longitude,
            dimensions.WidthKilometers,
            dimensions.HeightKilometers);
        if (bounds is null)
            return GibsPreview.Unavailable;

        var date = DateOnly.FromDateTime(anomaly.AcquiredAtUtc.UtcDateTime);
        (string Prefix, string Id, double WidthKilometers, double HeightKilometers, int PixelWidth, int PixelHeight) previewCacheKey = (
            Prefix: "gibs:preview",
            anomaly.Id,
            dimensions.WidthKilometers,
            dimensions.HeightKilometers,
            dimensions.PixelWidth,
            dimensions.PixelHeight);

        if (cache.TryGetValue<GibsPreview>(previewCacheKey, out GibsPreview? cachedPreview)
            && cachedPreview is not null)
        {
            return cachedPreview;
        }

        try
        {
            return await CreatePreviewAsync(
                layerCandidates,
                bounds.Value,
                date,
                dimensions,
                previewCacheKey,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            LogPreviewUnavailable(
                logger,
                anomaly.Satellite,
                anomaly.AcquiredAtUtc);
            return GibsPreview.Unavailable;
        }
    }

    private async Task<GibsPreview> CreatePreviewAsync(
        ImmutableArray<GibsLayerCandidate> layerCandidates,
        GeographicBounds bounds,
        DateOnly date,
        GibsPreviewDimensions dimensions,
        object previewCacheKey,
        CancellationToken cancellationToken)
    {
        GibsLayerCandidate representativeLayers = layerCandidates[0];
        if (!await IsLayerAvailableAsync(
            representativeLayers.OverlayLayer,
            representativeLayers.OverlayTileMatrixSet,
            date,
            cancellationToken).ConfigureAwait(false))
        {
            return GibsPreview.Unavailable;
        }

        GibsLayerCandidate? selectedLayers = await SelectBaseLayersAsync(
            layerCandidates,
            date,
            bounds,
            cancellationToken).ConfigureAwait(false);
        if (selectedLayers is not { } selected)
            return GibsPreview.Unavailable;

        if (selected.BaseSource != representativeLayers.BaseSource)
        {
            LogFallbackBaseSelected(
                logger,
                selected.BaseSource.Satellite,
                representativeLayers.BaseSource.Satellite,
                date);
        }

        Uri requestUri = BuildWmsUri(selected, date, bounds, dimensions);
        byte[]? bytes = await GetPngAsync(requestUri, MaximumPreviewBytes, cancellationToken).ConfigureAwait(false);
        if (bytes is null
            || !HasExpectedPreviewPng(bytes, dimensions.PixelWidth, dimensions.PixelHeight))
        {
            return GibsPreview.Unavailable;
        }

        var preview = new GibsPreview(bytes, selected.BaseSource);
        cache.Set(
            previewCacheKey,
            preview,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(hours: 2),
                Size = bytes.Length
            });
        return preview;
    }

    private async Task<GibsLayerCandidate?> SelectBaseLayersAsync(
        ImmutableArray<GibsLayerCandidate> layerCandidates,
        DateOnly date,
        GeographicBounds bounds,
        CancellationToken cancellationToken)
    {
        foreach (GibsLayerCandidate candidate in layerCandidates)
        {
            if (!await IsLayerAvailableAsync(
                candidate.BaseLayer,
                candidate.BaseTileMatrixSet,
                date,
                cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            if (await IsBaseLayerUsableAsync(
                candidate,
                date,
                bounds,
                cancellationToken).ConfigureAwait(false))
            {
                return candidate;
            }

            LogInsufficientBaseCoverage(logger, candidate.BaseSource.Satellite, date);
        }

        return null;
    }

    private async Task<bool> IsBaseLayerUsableAsync(
        GibsLayerCandidate layers,
        DateOnly date,
        GeographicBounds bounds,
        CancellationToken cancellationToken)
    {
        var dimensions = new GibsPreviewDimensions(
            WidthKilometers: 0,
            HeightKilometers: 0,
            PreviewProbePixelSize,
            PreviewProbePixelSize);
        Uri requestUri = BuildBaseProbeWmsUri(layers, date, bounds, dimensions);
        byte[]? bytes = await GetPngAsync(requestUri, MaximumPreviewProbeBytes, cancellationToken).ConfigureAwait(false);
        return bytes is not null && HasUsablePreviewCoverage(bytes);
    }

    private async Task<byte[]?> GetPngAsync(
        Uri requestUri,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        using HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode
            || response.Content.Headers.ContentType?.MediaType is not { } mediaType
            || !mediaType.Equals(value: "image/png", StringComparison.OrdinalIgnoreCase)
            || response.Content.Headers.ContentLength > maximumBytes)
        {
            return null;
        }

        return await HttpContentReader.ReadLimitedBytesAsync(
            response.Content,
            maximumBytes,
            cancellationToken).ConfigureAwait(false);
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
            HashSet<LandCoverPixel>? requiredPixels = CollectRequiredPixels(
                detections,
                builtUpProximityKilometers);
            if (requiredPixels is null)
                return GibsLandCoverResult.Unavailable();

            var requiredTiles = requiredPixels
                .Select(ToLandCoverTile)
                .Distinct()
                .ToImmutableArray();
            LandCoverDatesResult[] dateResults = await Task.WhenAll(requiredTiles.Select(tile =>
                GetLandCoverDatesAsync(tile, cancellationToken))).ConfigureAwait(false);

            if (dateResults.Any(result => !result.IsAvailable))
                return GibsLandCoverResult.Unavailable();

            var commonDates = dateResults[0].Dates.ToHashSet();
            foreach (LandCoverDatesResult result in dateResults.Skip(count: 1))
                commonDates.IntersectWith(result.Dates);

            if (commonDates.Count == 0)
                return GibsLandCoverResult.Unavailable();

            DateOnly selectedDate = commonDates.Max();
            int year = selectedDate.Year;
            (LandCoverTile Tile, LandCoverTileResult Result)[] tileResults = await Task.WhenAll(requiredTiles.Select(async tile =>
                (Tile: tile, Result: await GetLandCoverTileAsync(
                    tile,
                    selectedDate,
                    cancellationToken).ConfigureAwait(false)))).ConfigureAwait(false);

            if (tileResults.Any(item => !item.Result.IsAvailable))
                return GibsLandCoverResult.Unavailable(year);

            return CreateLandCoverResult(requiredPixels, tileResults, year);
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

    private static HashSet<LandCoverPixel>? CollectRequiredPixels(
        IReadOnlyList<Anomaly> detections,
        double builtUpProximityKilometers)
    {
        var requiredPixels = detections
            .Select(detection => ToLandCoverPixel(detection.Latitude, detection.Longitude))
            .ToHashSet();

        foreach (Anomaly detection in detections)
        {
            foreach (LandCoverPixel pixel in PixelsWithinProximity(
                detection.Latitude,
                detection.Longitude,
                builtUpProximityKilometers))
            {
                requiredPixels.Add(pixel);
                if (requiredPixels.Count > MaximumLandCoverPixels)
                    return null;
            }
        }

        return requiredPixels;
    }

    private static GibsLandCoverResult CreateLandCoverResult(
        HashSet<LandCoverPixel> requiredPixels,
        (LandCoverTile Tile, LandCoverTileResult Result)[] tileResults,
        int year)
    {
        Dictionary<LandCoverTile, byte[]> tiles = tileResults.ToDictionary(
            item => item.Tile,
            item => item.Result.Classes!);
        ImmutableArray<byte>.Builder sampledClasses = ImmutableArray.CreateBuilder<byte>(requiredPixels.Count);
        bool hasBuiltUpWithinProximity = false;
        foreach (LandCoverPixel pixel in requiredPixels)
        {
            byte landCoverClass = GetLandCoverClass(tiles, pixel);
            if (IsUnavailableClass(landCoverClass))
                return GibsLandCoverResult.Unavailable(year);

            sampledClasses.Add(landCoverClass);
            hasBuiltUpWithinProximity |= landCoverClass == 13;
        }

        return new(
            IsAvailable: true,
            year,
            sampledClasses.MoveToImmutable(),
            hasBuiltUpWithinProximity);
    }

    private async Task<LandCoverDatesResult> GetLandCoverDatesAsync(
        LandCoverTile tile,
        CancellationToken cancellationToken)
    {
        (string Prefix, int Row, int Column) cacheKey = (Prefix: "gibs:land-cover:dates", tile.Row, tile.Column);
        if (cache.TryGetValue<LandCoverDatesResult>(cacheKey, out LandCoverDatesResult cachedResult))
            return cachedResult;

        LandCoverDatesResult result;
        try
        {
            GeographicBounds bounds = GetLandCoverTileBounds(tile);
            var requestUri = new Uri(
                httpClient.BaseAddress!,
                relativeUri: $"wmts/epsg4326/best/1.0.0/{LandCoverLayer}/default/{LandCoverTileMatrixSet}/{bounds.ToInvariantString()}/all.xml");
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            result = LandCoverDatesResult.Unavailable;
            if (response.IsSuccessStatusCode
                && response.Content.Headers.ContentType?.MediaType is { } mediaType
                && mediaType.Contains(value: "xml", StringComparison.OrdinalIgnoreCase)
                && response.Content.Headers.ContentLength <= MaximumLandCoverDomainCharacters)
            {
                string xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (xml.Length <= MaximumLandCoverDomainCharacters
                    && TryParseAnnualDates(xml, out ImmutableArray<DateOnly> dates))
                {
                    result = new(IsAvailable: true, dates);
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
                    ? TimeSpan.FromHours(hours: 12)
                    : TimeSpan.FromMinutes(minutes: 5),
                Size = 1
            });
        return result;
    }

    private async Task<LandCoverTileResult> GetLandCoverTileAsync(
        LandCoverTile tile,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        (string Prefix, DateOnly Date, int Row, int Column) cacheKey = (
            Prefix: "gibs:land-cover:tile",
            Date: date,
            tile.Row,
            tile.Column);
        if (cache.TryGetValue<LandCoverTileResult>(cacheKey, out LandCoverTileResult? cachedResult)
            && cachedResult is not null)
        {
            return cachedResult;
        }

        LandCoverTileResult result;
        try
        {
            var requestUri = new Uri(
                httpClient.BaseAddress!,
                relativeUri: $"wmts/epsg4326/best/{LandCoverLayer}/default/{date:yyyy-MM-dd}/{LandCoverTileMatrixSet}/{LandCoverTileMatrix}/{tile.Row}/{tile.Column}.png");
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            result = LandCoverTileResult.Unavailable;
            if (response.IsSuccessStatusCode
                && response.Content.Headers.ContentType?.MediaType is { } mediaType
                && mediaType.Equals(value: "image/png", StringComparison.OrdinalIgnoreCase)
                && response.Content.Headers.ContentLength <= MaximumLandCoverTileBytes
                && await HttpContentReader.ReadLimitedBytesAsync(
                    response.Content,
                    MaximumLandCoverTileBytes,
                    cancellationToken).ConfigureAwait(false) is { } pngBytes
                && TryDecodeLandCoverPng(pngBytes, out byte[]? classes))
            {
                result = new(IsAvailable: true, classes);
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
                    ? TimeSpan.FromHours(hours: 24)
                    : TimeSpan.FromMinutes(minutes: 5),
                Size = result.Classes?.Length ?? 1
            });
        return result;
    }

    private static bool TryParseAnnualDates(
        string xml,
        out ImmutableArray<DateOnly> dates)
    {
        dates = [];
        if (!TryReadDomain(xml, out string domain))
            return false;

        var parsedDates = new HashSet<DateOnly>();
        foreach (string period in domain.Split(
            ',',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] values = period.Split('/', StringSplitOptions.TrimEntries);
            if (!TryReadDate(values[0], out DateOnly start))
                return false;

            if (values.Length == 1)
            {
                parsedDates.Add(start);
                continue;
            }

            if (values.Length != 3
                || !TryReadDate(values[1], out DateOnly end)
                || !"P1Y".Equals(values[2], StringComparison.Ordinal)
                || end < start)
            {
                return false;
            }

            for (DateOnly date = start; date <= end; date = date.AddYears(1))
                parsedDates.Add(date);
        }

        dates = [.. parsedDates.Order()];
        return dates.Length > 0;
    }

    private static LandCoverPixel ToLandCoverPixel(double latitude, double longitude)
    {
        int x = Math.Clamp(
            (int)Math.Floor((longitude + 180) / LandCoverPixelDegrees),
            min: 0,
            LandCoverTotalPixelWidth - 1);
        int y = Math.Clamp(
            (int)Math.Floor((90 - latitude) / LandCoverPixelDegrees),
            min: 0,
            LandCoverTotalPixelHeight - 1);
        return new(x, y);
    }

    private static IEnumerable<LandCoverPixel> PixelsWithinProximity(
        double latitude,
        double longitude,
        double proximityKilometers)
    {
        double angularRadius = proximityKilometers / Geography.EarthRadiusKilometers;
        double latitudeRadius = angularRadius * 180 / Math.PI;
        bool reachesPole = Math.Abs(latitude) + latitudeRadius >= 90;
        double longitudeRadius = reachesPole
            ? 180
            : Math.Asin(Math.Min(MaximumTrigonometricRatio, Math.Sin(angularRadius) / Math.Cos(latitude * Math.PI / 180)))
                * 180 / Math.PI;
        double north = Math.Min(MaximumLatitudeDegrees, latitude + latitudeRadius);
        double south = Math.Max(-90, latitude - latitudeRadius);
        int firstRow = ToLandCoverPixel(north, longitude).Y;
        int lastRow = ToLandCoverPixel(south, longitude).Y;
        int centerColumn = ToLandCoverPixel(latitude, longitude).X;
        int columnRadius = longitudeRadius >= 180
            ? LandCoverTotalPixelWidth / 2
            : (int)Math.Ceiling(longitudeRadius / LandCoverPixelDegrees) + 1;

        for (int row = Math.Max(MinimumLandCoverPixelIndex, firstRow - 1);
             row <= Math.Min(LandCoverTotalPixelHeight - 1, lastRow + 1);
             row++)
        {
            int firstOffset = columnRadius >= LandCoverTotalPixelWidth / 2
                ? 0
                : -columnRadius;
            int lastOffset = columnRadius >= LandCoverTotalPixelWidth / 2
                ? LandCoverTotalPixelWidth - 1
                : columnRadius;

            for (int offset = firstOffset; offset <= lastOffset; offset++)
            {
                int column = columnRadius >= LandCoverTotalPixelWidth / 2
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
        double north = 90 - pixel.Y * LandCoverPixelDegrees;
        double south = north - LandCoverPixelDegrees;
        double centerLongitude = -180 + (pixel.X + 0.5) * LandCoverPixelDegrees;
        double longitudeDelta = NormalizeLongitudeDelta(centerLongitude - longitude);
        double nearestLongitudeDelta = Math.CopySign(
            Math.Max(MinimumDistanceDegrees, Math.Abs(longitudeDelta) - LandCoverPixelDegrees / 2),
            longitudeDelta);
        double nearestLatitude = Math.Clamp(latitude, south, north);
        double nearestLongitude = longitude + nearestLongitudeDelta;
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
        double tileDegrees = LandCoverTileSize * LandCoverPixelDegrees;
        double west = -180 + tile.Column * tileDegrees;
        double east = west + tileDegrees;
        double north = 90 - tile.Row * tileDegrees;
        double south = north - tileDegrees;
        return new(west, south, east, north);
    }

    private static byte GetLandCoverClass(
        IReadOnlyDictionary<LandCoverTile, byte[]> tiles,
        LandCoverPixel pixel)
    {
        LandCoverTile tile = ToLandCoverTile(pixel);
        byte[] classes = tiles[tile];
        int localX = pixel.X % LandCoverTileSize;
        int localY = pixel.Y % LandCoverTileSize;
        return classes[localY * LandCoverTileSize + localX];
    }

    private static bool IsUnavailableClass(byte landCoverClass) =>
        landCoverClass is UnknownLandCoverClass or 255;

    private static bool HasExpectedPreviewPng(
        byte[] pngBytes,
        int expectedWidth,
        int expectedHeight)
    {
        if (expectedWidth <= 0
            || expectedHeight <= 0
            || !TryReadPngHeader(
                pngBytes,
                out int width,
                out int height,
                out byte colorType)
            || colorType is not (2 or 6))
        {
            return false;
        }

        if (width != expectedWidth || height != expectedHeight)
            return false;

        return HasCompletePngStructure(pngBytes);
    }

    private static bool HasCompletePngStructure(byte[] pngBytes)
    {
        if (!pngBytes.AsSpan().StartsWith(s_pngSignature))
            return false;

        int offset = s_pngSignature.Length;
        bool hasImageData = false;
        while (offset <= pngBytes.Length - 12)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(pngBytes.AsSpan(offset, length: 4));
            if (length > int.MaxValue || offset + 12L + length > pngBytes.Length)
                return false;

            int chunkLength = (int)length;
            Span<byte> type = pngBytes.AsSpan(offset + 4, length: 4);
            hasImageData |= type.SequenceEqual("IDAT"u8) && chunkLength > 0;
            offset += chunkLength + 12;
            if (type.SequenceEqual("IEND"u8))
                return chunkLength == 0 && hasImageData && offset == pngBytes.Length;
        }

        return false;
    }

    private static bool HasUsablePreviewCoverage(byte[] pngBytes)
    {
        if (!HasExpectedPreviewPng(
            pngBytes,
            PreviewProbePixelSize,
            PreviewProbePixelSize)
            || !TryDecodeRgba(
                pngBytes,
                PreviewProbePixelSize,
                PreviewProbePixelSize,
                out byte[] pixels))
        {
            return false;
        }

        int totalPixels = PreviewProbePixelSize * PreviewProbePixelSize;
        int requiredUsablePixels = (totalPixels + 1) / 2;
        int usablePixels = 0;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            bool alphaUsable = pixels[offset + 3] > PreviewNoDataMaximumChannel;
            bool colorUsable = pixels[offset] > PreviewNoDataMaximumChannel
                || pixels[offset + 1] > PreviewNoDataMaximumChannel
                || pixels[offset + 2] > PreviewNoDataMaximumChannel;
            if (alphaUsable && colorUsable)
                usablePixels++;
        }

        return usablePixels >= requiredUsablePixels;
    }

    private static bool TryReadPngHeader(
        byte[] pngBytes,
        out int width,
        out int height,
        out byte colorType)
    {
        width = 0;
        height = 0;
        colorType = 0;
        if (!pngBytes.AsSpan().StartsWith(s_pngSignature)
            || pngBytes.Length < s_pngSignature.Length + 25)
        {
            return false;
        }

        uint headerLength = BinaryPrimitives.ReadUInt32BigEndian(
            pngBytes.AsSpan(s_pngSignature.Length, length: 4));
        Span<byte> headerType = pngBytes.AsSpan(s_pngSignature.Length + 4, length: 4);
        Span<byte> header = pngBytes.AsSpan(s_pngSignature.Length + 8, length: 13);
        if (headerLength != 13
            || !headerType.SequenceEqual("IHDR"u8)
            || header[8] != 8
            || header[10] != 0
            || header[11] != 0
            || header[12] != 0)
        {
            return false;
        }

        uint unsignedWidth = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
        uint unsignedHeight = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(start: 4, length: 4));
        if (unsignedWidth is 0 or > int.MaxValue || unsignedHeight is 0 or > int.MaxValue)
            return false;

        width = (int)unsignedWidth;
        height = (int)unsignedHeight;
        colorType = header[9];
        return true;
    }

    private static bool TryDecodeLandCoverPng(byte[] pngBytes, out byte[] classes)
    {
        classes = [];
        if (!TryReadPngHeader(
            pngBytes,
            out int width,
            out int height,
            out byte colorType)
            || width != LandCoverTileSize
            || height != LandCoverTileSize
            || colorType != 3
            || !HasCompletePngStructure(pngBytes)
            || !TryDecodeRgba(
                pngBytes,
                LandCoverTileSize,
                LandCoverTileSize,
                out byte[] pixels))
        {
            return false;
        }

        classes = new byte[LandCoverTileSize * LandCoverTileSize];
        for (int pixel = 0; pixel < classes.Length; pixel++)
        {
            int offset = pixel * 4;
            if (pixels[offset + 3] != byte.MaxValue)
            {
                classes[pixel] = UnknownLandCoverClass;
                continue;
            }

            int rgb = Rgb(pixels[offset], pixels[offset + 1], pixels[offset + 2]);
            classes[pixel] = s_landCoverClassesByRgb.GetValueOrDefault(rgb, UnknownLandCoverClass);
        }

        return true;
    }

    private static bool TryDecodeRgba(
        byte[] pngBytes,
        int expectedWidth,
        int expectedHeight,
        out byte[] pixels)
    {
        pixels = [];
        try
        {
            var image = ImageResult.FromMemory(
                pngBytes,
                StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            if (image.Width != expectedWidth
                || image.Height != expectedHeight
                || image.Data.Length != checked(expectedWidth * expectedHeight * 4))
            {
                return false;
            }

            pixels = image.Data;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int Modulo(int value, int divisor) =>
        (value % divisor + divisor) % divisor;

    private static double NormalizeLongitudeDelta(double value) =>
        (value + 540) % 360 - 180;

    private static int Rgb(byte red, byte green, byte blue) =>
        red << 16 | green << 8 | blue;

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct LandCoverPixel(int X, int Y);

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct LandCoverTile(int Row, int Column);

    private readonly record struct LandCoverDatesResult(
        bool IsAvailable,
        ImmutableArray<DateOnly> Dates)
    {
        public static LandCoverDatesResult Unavailable { get; } = new(IsAvailable: false, []);
    }

    private sealed record LandCoverTileResult(bool IsAvailable, byte[]? Classes)
    {
        public static LandCoverTileResult Unavailable { get; } = new(IsAvailable: false, Classes: null);
    }

    private async Task<bool> IsLayerAvailableAsync(
        string layer,
        string tileMatrixSet,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        string cacheKey = $"gibs:availability:{layer}:{date:yyyy-MM-dd}";
        if (cache.TryGetValue<bool>(cacheKey, out bool available))
            return available;

        string dateText = date.ToString(format: "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var requestUri = new Uri(
            httpClient.BaseAddress!,
            relativeUri: $"wmts/epsg4326/best/1.0.0/{Uri.EscapeDataString(layer)}/default/{tileMatrixSet}/all/{dateText}--{dateText}.xml");

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        using HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        available = false;
        if (response.IsSuccessStatusCode
            && response.Content.Headers.ContentType?.MediaType is { } mediaType
            && mediaType.Contains(value: "xml", StringComparison.OrdinalIgnoreCase))
        {
            string xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (xml.Length <= 1_000_000)
                available = DomainContainsDate(xml, date);
        }

        cache.Set(
            cacheKey,
            available,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = available
                    ? TimeSpan.FromHours(hours: 12)
                    : TimeSpan.FromMinutes(minutes: 5),
                Size = 1
            });

        return available;
    }

    private static bool DomainContainsDate(string xml, DateOnly requestedDate)
    {
        if (!TryReadDomain(xml, out string domain))
            return false;

        foreach (string period in domain.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] values = period.Split('/', StringSplitOptions.TrimEntries);
            if (TryReadDate(values[0], out DateOnly start)
                && (values.Length == 1 || TryReadDate(values[1], out DateOnly end) && requestedDate <= end)
                && requestedDate >= start)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadDomain(string xml, out string domain)
    {
        domain = string.Empty;
        try
        {
            string? value = XDocument.Parse(xml, LoadOptions.None)
                .Descendants()
                .FirstOrDefault(element => "Domain".Equals(element.Name.LocalName, StringComparison.Ordinal))
                ?.Value;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            domain = value;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Xml.XmlException)
        {
            return false;
        }
    }

    private static bool TryReadDate(string value, out DateOnly date)
    {
        if (value.Length >= 10)
            value = value[..10];

        return DateOnly.TryParseExact(
            value,
            format: "yyyy-MM-dd",
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
            layerNames: $"{layers.BaseLayer},{layers.OverlayLayer}",
            styles: "default,size5",
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
            styles: "default",
            date,
            bounds,
            dimensions);

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Debug,
        Message = "GIBS base imagery has insufficient usable coverage for {BaseSatellite} on {Date}")]
    private static partial void LogInsufficientBaseCoverage(
        ILogger logger,
        string baseSatellite,
        DateOnly date);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Selected fallback GIBS base imagery from {BaseSatellite} for {OverlaySatellite} thermal overlay on {Date}")]
    private static partial void LogFallbackBaseSelected(
        ILogger logger,
        string baseSatellite,
        string overlaySatellite,
        DateOnly date);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "GIBS preview is temporarily unavailable for {Satellite} at {AcquiredAtUtc}")]
    private static partial void LogPreviewUnavailable(
        ILogger logger,
        string satellite,
        DateTimeOffset acquiredAtUtc);

    private static Uri BuildWmsUri(
        string layerNames,
        string styles,
        DateOnly date,
        GeographicBounds bounds,
        GibsPreviewDimensions dimensions)
    {
        KeyValuePair<string, string>[] parameters =
        [
            new(key: "SERVICE", value: "WMS"),
            new(key: "REQUEST", value: "GetMap"),
            new(key: "VERSION", value: "1.1.1"),
            new(key: "SRS", value: "EPSG:4326"),
            new(key: "FORMAT", value: "image/png"),
            new(key: "WIDTH", value: dimensions.PixelWidth.ToString(CultureInfo.InvariantCulture)),
            new(key: "HEIGHT", value: dimensions.PixelHeight.ToString(CultureInfo.InvariantCulture)),
            new(key: "TIME", value: date.ToString(format: "yyyy-MM-dd", CultureInfo.InvariantCulture)),
            new(key: "BBOX", value: bounds.ToInvariantString()),
            new(key: "LAYERS", value: layerNames),
            new(key: "STYLES", value: styles)
        ];
        string query = string.Join('&', parameters.Select(pair =>
            $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));

        return new(uriString: $"https://gibs.earthdata.nasa.gov/wms/epsg4326/best/wms.cgi?{query}");
    }
}
