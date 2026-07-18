using System.Globalization;
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
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

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
