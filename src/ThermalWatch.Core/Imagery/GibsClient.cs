using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml.Linq;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using StbImageSharp;

namespace ThermalWatch.Core;

/// <summary>Retrieves exact-date notification imagery and annual NASA land-cover samples through a bounded cache.</summary>
/// <param name="httpClient">The host-owned GIBS HTTP client.</param>
/// <param name="cache">The host-owned imagery cache with configured size limits.</param>
/// <param name="logger">The imagery availability and source-selection logger.</param>
public sealed partial class GibsClient(HttpClient httpClient, IMemoryCache cache, ILogger<GibsClient> logger)
{
    private const string LandCoverLayer = "MODIS_Combined_L3_IGBP_Land_Cover_Type_Annual";
    private const int LandCoverMatrixHeight = 80;
    private const int LandCoverMatrixWidth = 160;
    private const double LandCoverPixelDegrees = 360d / LandCoverTotalPixelWidth;
    private const int LandCoverTileMatrix = 7;
    private const string LandCoverTileMatrixSet = "500m";
    private const int LandCoverTileSize = 512;
    private const int LandCoverTotalPixelHeight = LandCoverTileSize * LandCoverMatrixHeight;
    private const int LandCoverTotalPixelWidth = LandCoverTileSize * LandCoverMatrixWidth;
    private const int MaximumLandCoverDomainCharacters = 1024 * 1024;
    private const int MaximumLandCoverPixels = 1_000_000;
    private const int MaximumLandCoverTileBytes = 1024 * 1024;
    private const double MaximumLatitudeDegrees = 90;
    private const int MaximumPreviewBytes = 10 * 1024 * 1024;
    private const int MaximumPreviewProbeBytes = 256 * 1024;
    private const double MaximumTrigonometricRatio = 1;
    private const double MinimumDistanceDegrees = 0;
    private const int MinimumLandCoverPixelIndex = 0;
    private const string PreviewCompositeStyles = "default,size25";
    private const byte PreviewNoDataMaximumChannel = 8;
    private const int PreviewProbePixelSize = 64;
    private const byte UnknownLandCoverClass = 254;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly IReadOnlyDictionary<int, byte> LandCoverClassesByRgb =
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

    /// <summary>Samples cluster members and their proximity using the newest year common to every required tile.</summary>
    /// <typeparam name="TAnomalies">The indexed observation collection, kept unboxed during asynchronous acquisition.</typeparam>
    /// <param name="anomalies">The cluster members.</param>
    /// <param name="builtUpProximityKilometers">The surrounding distance to sample for urban/built-up land cover.</param>
    /// <param name="cancellationToken">Cancels provider requests and reads.</param>
    public async Task<GibsLandCoverResult> GetLandCoverAsync<TAnomalies>(TAnomalies anomalies, double builtUpProximityKilometers, CancellationToken cancellationToken)
        where TAnomalies : IReadOnlyList<Anomaly>
    {
        if (anomalies is null)
            throw new ArgumentNullException(nameof(anomalies));

        if (anomalies.Count == 0)
            return GibsLandCoverResult.Unavailable();

        try
        {
            HashSet<LandCoverPixel>? requiredPixels = CollectRequiredPixels(anomalies, builtUpProximityKilometers);

            if (requiredPixels is null)
                return GibsLandCoverResult.Unavailable();

            var requiredTiles = requiredPixels
                .Select(ToLandCoverTile)
                .Distinct()
                .ToImmutableArray();

            LandCoverDatesResult[] dateResults = await Task.WhenAll(requiredTiles.Select(tile => GetLandCoverDatesAsync(tile, cancellationToken))).ConfigureAwait(continueOnCapturedContext: false);

            if (dateResults.Any(result => !result.IsAvailable))
                return GibsLandCoverResult.Unavailable();

            HashSet<DateOnly> commonDates = [.. dateResults[0].Dates];

            foreach (LandCoverDatesResult result in dateResults.Skip(count: 1))
                commonDates.IntersectWith([.. result.Dates]);

            if (commonDates.Count == 0)
                return GibsLandCoverResult.Unavailable();

            DateOnly selectedDate = commonDates.Max();
            int year = selectedDate.Year;

            LoadedLandCoverTile[] tileResults = await Task.WhenAll(
                requiredTiles.Select(
                    async tile =>
                new LoadedLandCoverTile(tile, await GetLandCoverTileAsync(tile, selectedDate, cancellationToken).ConfigureAwait(continueOnCapturedContext: false))
                )
            ).ConfigureAwait(continueOnCapturedContext: false);

            return tileResults.Any(item => !item.Result.IsAvailable)
                ? GibsLandCoverResult.Unavailable(year)
                : CreateLandCoverResult(requiredPixels, tileResults, year);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            return GibsLandCoverResult.Unavailable();
        }
    }

    /// <summary>Returns a pass-matched, exact-acquisition-date preview, or a retryable unavailable result.</summary>
    /// <param name="anomaly">The representative observation and authoritative thermal-overlay source.</param>
    /// <param name="dimensions">The requested physical and pixel coverage.</param>
    /// <param name="cancellationToken">Cancels provider requests and reads.</param>
    public async Task<GibsPreview> GetPreviewAsync(Anomaly anomaly, GibsPreviewDimensions dimensions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(anomaly);

        ImmutableArray<GibsLayerCandidate> layerCandidates = GibsLayers.GetCandidates(anomaly);

        if (layerCandidates.IsDefaultOrEmpty)
            return GibsPreview.Unavailable;

        GeographicBounds? bounds = Geography.CreatePreviewBounds(anomaly.Latitude, anomaly.Longitude, dimensions.WidthKilometers, dimensions.HeightKilometers);

        if (bounds is null)
            return GibsPreview.Unavailable;

        var date = DateOnly.FromDateTime(anomaly.AcquiredAtUtc.UtcDateTime);

        var previewCacheKey = new PreviewCacheKey(anomaly.Id, dimensions);

        if (cache.TryGetValue(previewCacheKey, out GibsPreview? cachedPreview) && cachedPreview is not null)
            return cachedPreview;

        try
        {
            return await CreatePreviewAsync(layerCandidates, bounds.Value, date, dimensions, previewCacheKey, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            LogPreviewUnavailable(logger, anomaly.Satellite, anomaly.AcquiredAtUtc);

            return GibsPreview.Unavailable;
        }
    }

    private static Uri BuildBaseProbeWmsUri(GibsLayerCandidate layers, DateOnly date, GeographicBounds bounds, GibsPreviewDimensions dimensions)
    {
        return BuildWmsUri(layers.BaseLayer, styles: "default", date, bounds, dimensions);
    }

    private static Uri BuildWmsUri(GibsLayerCandidate layers, DateOnly date, GeographicBounds bounds, GibsPreviewDimensions dimensions)
    {
        return BuildWmsUri($"{layers.BaseLayer},{layers.OverlayLayer}", PreviewCompositeStyles, date, bounds, dimensions);
    }

    private static Uri BuildWmsUri(string layerNames, string styles, DateOnly date, GeographicBounds bounds, GibsPreviewDimensions dimensions)
    {
        KeyValuePair<string, string>[] parameters =
        [
            new(key: "SERVICE", value: "WMS"),
            new(key: "REQUEST", value: "GetMap"),
            new(key: "VERSION", value: "1.1.1"),
            new(key: "SRS", value: "EPSG:4326"),
            new(key: "FORMAT", value: "image/png"),
            new(key: "WIDTH", dimensions.PixelWidth.ToString(CultureInfo.InvariantCulture)),
            new(key: "HEIGHT", dimensions.PixelHeight.ToString(CultureInfo.InvariantCulture)),
            new(key: "TIME", date.ToString(format: "yyyy-MM-dd", CultureInfo.InvariantCulture)),
            new(key: "BBOX", bounds.ToInvariantString()),
            new(key: "LAYERS", layerNames),
            new(key: "STYLES", styles)
        ];

        string query = string.Join(separator: '&', parameters.Select(static pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));

        return new($"https://gibs.earthdata.nasa.gov/wms/epsg4326/best/wms.cgi?{query}");
    }

    private static HashSet<LandCoverPixel>? CollectRequiredPixels<TAnomalies>(TAnomalies anomalies, double builtUpProximityKilometers)
        where TAnomalies : IReadOnlyList<Anomaly>
    {
        HashSet<LandCoverPixel> requiredPixels =
        [
            .. Enumerable.Range(start: 0, anomalies.Count)
                .Select(index => ToLandCoverPixel(anomalies[index].Latitude, anomalies[index].Longitude))
        ];

        if (requiredPixels.Count > MaximumLandCoverPixels)
            return null;

        for (int index = 0; index < anomalies.Count; index++)
        {
            Anomaly anomaly = anomalies[index];

            foreach (LandCoverPixel pixel in PixelsWithinProximity(anomaly.Latitude, anomaly.Longitude, builtUpProximityKilometers))
            {
                bool addedPixel = requiredPixels.Add(pixel);

                if (addedPixel && requiredPixels.Count > MaximumLandCoverPixels)
                    return null;
            }
        }

        return requiredPixels;
    }

    private static GibsLandCoverResult CreateLandCoverResult(HashSet<LandCoverPixel> requiredPixels, LoadedLandCoverTile[] tileResults, int year)
    {
        var tiles = new Dictionary<LandCoverTile, byte[]>();

        foreach (LoadedLandCoverTile tile in tileResults)
        {
            if (tile.Result.Classes is not { } classes)
                return GibsLandCoverResult.Unavailable(year);

            tiles.Add(tile.Tile, classes);
        }

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

        return new(IsAvailable: true, year, sampledClasses.MoveToImmutable(), hasBuiltUpWithinProximity);
    }

    private static double DistanceToLandCoverPixel(double latitude, double longitude, LandCoverPixel pixel)
    {
        double north = 90 - (pixel.Row * LandCoverPixelDegrees);
        double south = north - LandCoverPixelDegrees;
        double centerLongitude = -180 + ((pixel.Column + 0.5) * LandCoverPixelDegrees);
        double longitudeDelta = NormalizeLongitudeDelta(centerLongitude - longitude);

        double nearestLongitudeDelta = Math.CopySign(Math.Max(MinimumDistanceDegrees, Math.Abs(longitudeDelta) - (LandCoverPixelDegrees / 2)), longitudeDelta);

        double nearestLatitude = Math.Clamp(latitude, south, north);
        double nearestLongitude = longitude + nearestLongitudeDelta;

        return Geography.HaversineKilometers(latitude, longitude, nearestLatitude, nearestLongitude);
    }

    private static bool DomainContainsDate(string xml, DateOnly requestedDate)
    {
        if (!TryReadDomain(xml, out string domain))
            return false;

        foreach (string period in domain.Split(separator: ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] values = period.Split(separator: '/', StringSplitOptions.TrimEntries);

            if (!TryReadDate(values[0], out DateOnly start) || requestedDate < start)
                continue;

            if (values.Length == 1)
                return true;

            if (TryReadDate(values[1], out DateOnly end) && requestedDate <= end)
                return true;
        }

        return false;
    }

    private static byte GetLandCoverClass(IReadOnlyDictionary<LandCoverTile, byte[]> tiles, LandCoverPixel pixel)
    {
        LandCoverTile tile = ToLandCoverTile(pixel);
        byte[] classes = tiles[tile];
        int localX = pixel.Column % LandCoverTileSize;
        int localY = pixel.Row % LandCoverTileSize;

        return classes[(localY * LandCoverTileSize) + localX];
    }

    private static GeographicBounds GetLandCoverTileBounds(LandCoverTile tile)
    {
        double tileDegrees = LandCoverTileSize * LandCoverPixelDegrees;
        double west = -180 + (tile.Column * tileDegrees);
        double east = west + tileDegrees;
        double north = 90 - (tile.Row * tileDegrees);
        double south = north - tileDegrees;

        return new(west, south, east, north);
    }

    private static bool HasCompletePngStructure(byte[] pngBytes)
    {
        if (!pngBytes.AsSpan().StartsWith(PngSignature))
            return false;

        int offset = PngSignature.Length;
        bool hasImageData = false;

        while (offset <= pngBytes.Length - 12)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(pngBytes.AsSpan(offset, length: 4));

            if (length > int.MaxValue || offset + 12L + length > pngBytes.Length)
                return false;

            int chunkLength = (int)length;
            Span<byte> type = pngBytes.AsSpan(offset + 4, length: 4);
            hasImageData |= type.SequenceEqual(other: "IDAT"u8) && chunkLength > 0;
            offset += chunkLength + 12;

            if (type.SequenceEqual(other: "IEND"u8))
                return chunkLength == 0 && hasImageData && offset == pngBytes.Length;
        }

        return false;
    }

    private static bool HasExpectedPreviewPng(byte[] pngBytes, int expectedWidth, int expectedHeight)
    {
        return expectedWidth > 0
            && expectedHeight > 0
            && TryReadPngHeader(pngBytes, out int width, out int height, out byte colorType)
            && colorType is 2 or 6 && width == expectedWidth && height == expectedHeight && HasCompletePngStructure(pngBytes);
    }

    private static bool HasUsablePreviewCoverage(byte[] pngBytes)
    {
        if (!HasExpectedPreviewPng(pngBytes, PreviewProbePixelSize, PreviewProbePixelSize))
            return false;

        if (!TryDecodeRgba(pngBytes, PreviewProbePixelSize, PreviewProbePixelSize, out byte[] pixels))
            return false;

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

    private static bool IsUnavailableClass(byte landCoverClass)
    {
        return landCoverClass is UnknownLandCoverClass or 255;
    }

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Selected fallback GIBS base imagery from {BaseSatellite} for {OverlaySatellite} thermal overlay on {Date}"
    )]
    private static partial void LogFallbackBaseSelected(ILogger logger, string baseSatellite, string overlaySatellite, DateOnly date);

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Debug,
        Message = "GIBS base imagery has insufficient usable coverage for {BaseSatellite} on {Date}"
    )]
    private static partial void LogInsufficientBaseCoverage(ILogger logger, string baseSatellite, DateOnly date);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "GIBS preview is temporarily unavailable for {Satellite} at {AcquiredAtUtc}")]
    private static partial void LogPreviewUnavailable(ILogger logger, string satellite, DateTimeOffset acquiredAtUtc);

    private static int Modulo(int value, int divisor)
    {
        return ((value % divisor) + divisor) % divisor;
    }

    private static double NormalizeLongitudeDelta(double value)
    {
        return ((value + 540) % 360) - 180;
    }

    private static IEnumerable<LandCoverPixel> PixelsWithinProximity(double latitude, double longitude, double proximityKilometers)
    {
        double angularRadius = proximityKilometers / Geography.EarthRadiusKilometers;
        double latitudeRadius = angularRadius * 180 / Math.PI;
        bool reachesPole = Math.Abs(latitude) + latitudeRadius >= 90;

        double longitudeRadius = reachesPole
            ? 180
            : Math.Asin(Math.Min(MaximumTrigonometricRatio, Math.Sin(angularRadius) / Math.Cos(latitude * Math.PI / 180)))
                * 180 / Math.PI;

        double north = Math.Min(MaximumLatitudeDegrees, latitude + latitudeRadius);
        double south = Math.Max(val1: -90, latitude - latitudeRadius);
        int firstRow = ToLandCoverPixel(north, longitude).Row;
        int lastRow = ToLandCoverPixel(south, longitude).Row;
        int centerColumn = ToLandCoverPixel(latitude, longitude).Column;

        int columnRadius = longitudeRadius >= 180
            ? LandCoverTotalPixelWidth / 2
            : (int)Math.Ceiling(longitudeRadius / LandCoverPixelDegrees) + 1;

        int firstSamplingRow = Math.Max(MinimumLandCoverPixelIndex, firstRow - 1);
        int lastSamplingRow = Math.Min(LandCoverTotalPixelHeight - 1, lastRow + 1);

        for (int row = firstSamplingRow; row <= lastSamplingRow; row++)
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

    private static int Rgb(byte red, byte green, byte blue)
    {
        return (red << 16) | (green << 8) | blue;
    }

    private static LandCoverPixel ToLandCoverPixel(double latitude, double longitude)
    {
        int column = Math.Clamp((int)Math.Floor((longitude + 180) / LandCoverPixelDegrees), min: 0, LandCoverTotalPixelWidth - 1);

        int row = Math.Clamp((int)Math.Floor((90 - latitude) / LandCoverPixelDegrees), min: 0, LandCoverTotalPixelHeight - 1);

        return new(column, row);
    }

    private static LandCoverTile ToLandCoverTile(LandCoverPixel pixel)
    {
        return new(pixel.Row / LandCoverTileSize, pixel.Column / LandCoverTileSize);
    }

    private static bool TryDecodeLandCoverPng(byte[] pngBytes, out byte[] classes)
    {
        classes = [];

        if (!TryReadPngHeader(pngBytes, out int width, out int height, out byte colorType))
            return false;

        if (width != LandCoverTileSize || height != LandCoverTileSize || colorType != 3 || !HasCompletePngStructure(pngBytes))
            return false;

        if (!TryDecodeRgba(pngBytes, LandCoverTileSize, LandCoverTileSize, out byte[] pixels))
            return false;

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
            classes[pixel] = LandCoverClassesByRgb.GetValueOrDefault(rgb, UnknownLandCoverClass);
        }

        return true;
    }

    private static bool TryDecodeRgba(byte[] pngBytes, int expectedWidth, int expectedHeight, out byte[] pixels)
    {
        pixels = [];

        try
        {
            var image = ImageResult.FromMemory(pngBytes, ColorComponents.RedGreenBlueAlpha);

            bool hasExpectedDimensions = image.Width == expectedWidth && image.Height == expectedHeight;
            bool hasExpectedPixelCount = image.Data.Length == checked(expectedWidth * expectedHeight * 4);

            if (!hasExpectedDimensions || !hasExpectedPixelCount)
                return false;

            pixels = image.Data;

            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or IOException or OverflowException)
        {
            return false;
        }
    }

    private static bool TryParseAnnualDates(string xml, out ImmutableArray<DateOnly> dates)
    {
        dates = [];

        if (!TryReadDomain(xml, out string domain))
            return false;

        var parsedDates = new List<DateOnly>();

        foreach (string period in domain.Split(separator: ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] values = period.Split(separator: '/', StringSplitOptions.TrimEntries);

            if (!TryReadDate(values[0], out DateOnly start))
                return false;

            if (values.Length == 1)
            {
                parsedDates.Add(start);

                continue;
            }

            if (values.Length != 3 || !"P1Y".Equals(values[2], StringComparison.Ordinal))
                return false;

            if (!TryReadDate(values[1], out DateOnly end) || end < start)
                return false;

            for (DateOnly date = start; date <= end; date = date.AddYears(value: 1))
                parsedDates.Add(date);
        }

        dates = [.. parsedDates.Distinct().Order()];

        return dates.Length > 0;
    }

    private static bool TryReadDate(string value, out DateOnly date)
    {
        if (value.Length >= 10)
            value = value[..10];

        return DateOnly.TryParseExact(value, format: "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static bool TryReadDomain(string xml, out string domain)
    {
        domain = string.Empty;

        try
        {
            string? value = XDocument.Parse(xml, LoadOptions.None)
                .Descendants()
                .FirstOrDefault(static element => "Domain".Equals(element.Name.LocalName, StringComparison.Ordinal))
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

    private static bool TryReadPngHeader(byte[] pngBytes, out int width, out int height, out byte colorType)
    {
        width = 0;
        height = 0;
        colorType = 0;

        if (!pngBytes.AsSpan().StartsWith(PngSignature) || pngBytes.Length < PngSignature.Length + 25)
            return false;

        uint headerLength = BinaryPrimitives.ReadUInt32BigEndian(pngBytes.AsSpan(PngSignature.Length, length: 4));

        Span<byte> headerType = pngBytes.AsSpan(PngSignature.Length + 4, length: 4);
        Span<byte> header = pngBytes.AsSpan(PngSignature.Length + 8, length: 13);

        bool hasSupportedEncoding = headerLength == 13
            && headerType.SequenceEqual(other: "IHDR"u8)
            && header[index: 8] == 8
            && header[index: 10] == 0
            && header[index: 11] == 0
            && header[index: 12] == 0;

        if (!hasSupportedEncoding)
            return false;

        uint unsignedWidth = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
        uint unsignedHeight = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(start: 4, length: 4));

        if (unsignedWidth is 0 or > int.MaxValue || unsignedHeight is 0 or > int.MaxValue)
            return false;

        width = (int)unsignedWidth;
        height = (int)unsignedHeight;
        colorType = header[index: 9];

        return true;
    }

    private async Task<GibsPreview> CreatePreviewAsync(
        ImmutableArray<GibsLayerCandidate> layerCandidates,
        GeographicBounds bounds,
        DateOnly date,
        GibsPreviewDimensions dimensions,
        PreviewCacheKey previewCacheKey,
        CancellationToken cancellationToken
    )
    {
        GibsLayerCandidate representativeLayers = layerCandidates[index: 0];

        bool overlayAvailable = await IsLayerAvailableAsync(representativeLayers.OverlayLayer, representativeLayers.OverlayTileMatrixSet, date, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        if (!overlayAvailable)
            return GibsPreview.Unavailable;

        GibsLayerCandidate? selectedLayers = await SelectBaseLayersAsync(layerCandidates, date, bounds, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        if (selectedLayers is not { } selected)
            return GibsPreview.Unavailable;

        if (selected.BaseSource != representativeLayers.BaseSource)
            LogFallbackBaseSelected(logger, selected.BaseSource.Satellite, representativeLayers.BaseSource.Satellite, date);

        Uri requestUri = BuildWmsUri(selected, date, bounds, dimensions);
        byte[]? bytes = await GetPngAsync(requestUri, MaximumPreviewBytes, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        if (bytes is null || !HasExpectedPreviewPng(bytes, dimensions.PixelWidth, dimensions.PixelHeight))
            return GibsPreview.Unavailable;

        bytes = GibsPreviewRuler.Render(bytes, dimensions);

        if (bytes is null)
            return GibsPreview.Unavailable;

        var preview = new GibsPreview(bytes, selected.BaseSource);

        return cache.Set(
            previewCacheKey,
            preview,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(hours: 2),
                Size = bytes.Length
            }
        );
    }

    private async Task<LandCoverDatesResult> GetLandCoverDatesAsync(LandCoverTile tile, CancellationToken cancellationToken)
    {
        var cacheKey = new LandCoverDatesCacheKey(tile);

        bool hasCachedDates = cache.TryGetValue(cacheKey, out LandCoverDatesResult? cachedResult);

        if (hasCachedDates && cachedResult is not null)
            return cachedResult;

        LandCoverDatesResult result;

        try
        {
            GeographicBounds bounds = GetLandCoverTileBounds(tile);

            Uri baseAddress = httpClient.BaseAddress
                ?? throw new InvalidOperationException(message: "The GIBS HTTP client requires a base address.");

            var requestUri = new Uri(baseAddress, $"wmts/epsg4326/best/1.0.0/{LandCoverLayer}/default/{LandCoverTileMatrixSet}/{bounds.ToInvariantString()}/all.xml");

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

            using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            result = LandCoverDatesResult.Unavailable;

            bool hasValidDomainResponse = response.IsSuccessStatusCode
                && response.Content.Headers.ContentType?.MediaType is { } mediaType
                && mediaType.Contains(value: "xml", StringComparison.OrdinalIgnoreCase)
                && response.Content.Headers.ContentLength <= MaximumLandCoverDomainCharacters;

            if (hasValidDomainResponse)
            {
                string xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

                if (xml.Length <= MaximumLandCoverDomainCharacters && TryParseAnnualDates(xml, out ImmutableArray<DateOnly> dates))
                    result = new(IsAvailable: true, dates);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            result = LandCoverDatesResult.Unavailable;
        }

        return cache.Set(
            cacheKey,
            result,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = result.IsAvailable
                    ? TimeSpan.FromHours(hours: 12)
                    : TimeSpan.FromMinutes(minutes: 5),
                Size = 1
            }
        );
    }

    private async Task<LandCoverTileResult> GetLandCoverTileAsync(LandCoverTile tile, DateOnly date, CancellationToken cancellationToken)
    {
        var cacheKey = new LandCoverTileCacheKey(tile, date);

        if (cache.TryGetValue(cacheKey, out LandCoverTileResult? cachedResult) && cachedResult is not null)
            return cachedResult;

        LandCoverTileResult result;

        try
        {
            Uri baseAddress = httpClient.BaseAddress
                ?? throw new InvalidOperationException(message: "The GIBS HTTP client requires a base address.");

            var requestUri = new Uri(
                baseAddress,
                $"wmts/epsg4326/best/{LandCoverLayer}/default/{date:yyyy-MM-dd}/{LandCoverTileMatrixSet}/{LandCoverTileMatrix}/{tile.Row}/{tile.Column}.png"
            );

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

            using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            result = LandCoverTileResult.Unavailable;

            bool hasValidTileResponse = response.IsSuccessStatusCode
                && response.Content.Headers.ContentType?.MediaType is { } mediaType
                && mediaType.Equals(value: "image/png", StringComparison.OrdinalIgnoreCase)
                && response.Content.Headers.ContentLength <= MaximumLandCoverTileBytes;

            if (hasValidTileResponse)
            {
                byte[]? pngBytes = await HttpContentReader.ReadLimitedBytesAsync(response.Content, MaximumLandCoverTileBytes, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

                if (pngBytes is not null && TryDecodeLandCoverPng(pngBytes, out byte[] classes))
                    result = new(IsAvailable: true, classes);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            result = LandCoverTileResult.Unavailable;
        }

        return cache.Set(
            cacheKey,
            result,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = result.IsAvailable
                    ? TimeSpan.FromHours(hours: 24)
                    : TimeSpan.FromMinutes(minutes: 5),
                Size = result.Classes?.Length ?? 1
            }
        );
    }

    private async Task<byte[]?> GetPngAsync(Uri requestUri, int maximumBytes, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

        using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        bool hasValidPngResponse = response.IsSuccessStatusCode
            && response.Content.Headers.ContentType?.MediaType is { } mediaType
            && mediaType.Equals(value: "image/png", StringComparison.OrdinalIgnoreCase)
            && !(response.Content.Headers.ContentLength > maximumBytes);

        return !hasValidPngResponse
            ? null
            : await HttpContentReader.ReadLimitedBytesAsync(response.Content, maximumBytes, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
    }

    private async Task<bool> IsBaseLayerUsableAsync(GibsLayerCandidate layers, DateOnly date, GeographicBounds bounds, CancellationToken cancellationToken)
    {
        var dimensions = new GibsPreviewDimensions(WidthKilometers: 0, HeightKilometers: 0, PreviewProbePixelSize, PreviewProbePixelSize);

        Uri requestUri = BuildBaseProbeWmsUri(layers, date, bounds, dimensions);
        byte[]? bytes = await GetPngAsync(requestUri, MaximumPreviewProbeBytes, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        return bytes is not null && HasUsablePreviewCoverage(bytes);
    }

    private async Task<bool> IsLayerAvailableAsync(string layer, string tileMatrixSet, DateOnly date, CancellationToken cancellationToken)
    {
        string cacheKey = $"gibs:availability:{layer}:{date:yyyy-MM-dd}";

        if (cache.TryGetValue(cacheKey, out LayerAvailability? cachedAvailability))
        {
            if (cachedAvailability is not null)
                return cachedAvailability.IsAvailable;
        }

        string dateText = date.ToString(format: "yyyy-MM-dd", CultureInfo.InvariantCulture);

        Uri baseAddress = httpClient.BaseAddress
            ?? throw new InvalidOperationException(message: "The GIBS HTTP client requires a base address.");

        var requestUri = new Uri(baseAddress, $"wmts/epsg4326/best/1.0.0/{Uri.EscapeDataString(layer)}/default/{tileMatrixSet}/all/{dateText}--{dateText}.xml");

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

        using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        bool available = false;

        bool hasValidDomainResponse = response.IsSuccessStatusCode
            && response.Content.Headers.ContentType?.MediaType is { } mediaType
            && mediaType.Contains(value: "xml", StringComparison.OrdinalIgnoreCase);

        if (hasValidDomainResponse)
        {
            string xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            if (xml.Length <= 1_000_000)
                available = DomainContainsDate(xml, date);
        }

        LayerAvailability availability = cache.Set(
            cacheKey,
            new LayerAvailability(available),
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = available
                    ? TimeSpan.FromHours(hours: 12)
                    : TimeSpan.FromMinutes(minutes: 5),
                Size = 1
            }
        );

        return availability.IsAvailable;
    }

    private async Task<GibsLayerCandidate?> SelectBaseLayersAsync(ImmutableArray<GibsLayerCandidate> layerCandidates, DateOnly date, GeographicBounds bounds, CancellationToken cancellationToken)
    {
        foreach (GibsLayerCandidate candidate in layerCandidates)
        {
            bool baseAvailable = await IsLayerAvailableAsync(candidate.BaseLayer, candidate.BaseTileMatrixSet, date, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            if (!baseAvailable)
                continue;

            bool hasUsableCoverage = await IsBaseLayerUsableAsync(candidate, date, bounds, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            if (hasUsableCoverage)
                return candidate;

            LogInsufficientBaseCoverage(logger, candidate.BaseSource.Satellite, date);
        }

        return null;
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct LandCoverPixel(int Column, int Row);

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct LandCoverTile(int Row, int Column);

    private sealed record LandCoverDatesCacheKey(LandCoverTile Tile);

    private sealed record LandCoverDatesResult(bool IsAvailable, ImmutableArray<DateOnly> Dates)
    {
        public static LandCoverDatesResult Unavailable { get; } = new(IsAvailable: false, []);
    }

    private sealed record LandCoverTileCacheKey(LandCoverTile Tile, DateOnly Date);

    private sealed record LandCoverTileResult(bool IsAvailable, byte[]? Classes)
    {
        public static LandCoverTileResult Unavailable { get; } = new(IsAvailable: false, Classes: null);
    }

    private sealed record LayerAvailability(bool IsAvailable);

    private sealed record LoadedLandCoverTile(LandCoverTile Tile, LandCoverTileResult Result);

    private sealed record PreviewCacheKey(string AnomalyId, GibsPreviewDimensions Dimensions);
}
