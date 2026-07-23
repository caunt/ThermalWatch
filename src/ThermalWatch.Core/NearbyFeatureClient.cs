using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ThermalWatch.Core;

public sealed partial class NearbyFeatureClient(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<NearbyFeatureClient> logger) : IDisposable
{
    private const int MaximumResponseBytes = 10 * 1024 * 1024;
    private const int MaximumResults = 5;
    private const int RadiusMeters = 2000;
    private const double RadiusKilometers = RadiusMeters / 1000d;
    private const double DistanceToleranceKilometers = 0.000001;
    private static readonly TimeSpan s_successCacheDuration = TimeSpan.FromHours(hours: 1);
    private static readonly TimeSpan s_failureCacheDuration = TimeSpan.FromMinutes(minutes: 1);
    private readonly SemaphoreSlim _requestGate = new(initialCount: 1, maxCount: 1);

    public async Task<ImmutableArray<NearbyFeature>> FindNearbyAsync(
        Anomaly anomaly,
        CancellationToken cancellationToken)
    {
        double latitude = Math.Round(anomaly.Latitude, digits: 6, MidpointRounding.AwayFromZero);
        double longitude = Math.Round(anomaly.Longitude, digits: 6, MidpointRounding.AwayFromZero);
        (string Prefix, double Latitude, double Longitude) cacheKey = (
            Prefix: "overpass:nearby",
            latitude,
            longitude);
        if (cache.TryGetValue(cacheKey, out ImmutableArray<NearbyFeature> cached))
            return cached;

        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cache.TryGetValue(cacheKey, out cached))
                return cached;

            NearbyFeatureLookup lookup;
            try
            {
                lookup = await FetchAsync(latitude, longitude, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                lookup = NearbyFeatureLookup.Unavailable;
            }

            if (!lookup.IsAvailable)
                LogTemporarilyUnavailable(logger, latitude, longitude);

            cache.Set(
                cacheKey,
                lookup.Features,
                new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = lookup.IsAvailable
                        ? s_successCacheDuration
                        : s_failureCacheDuration,
                    Size = Math.Max(lookup.Features.Length * 256, val2: 1)
                });
            return lookup.Features;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task<NearbyFeatureLookup> FetchAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken)
    {
        string query = string.Create(
            CultureInfo.InvariantCulture,
            handler: $"[out:json][timeout:10];nwr(around:{RadiusMeters},{latitude:0.000000},{longitude:0.000000})[\"name\"];out center;");
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri: "interpreter")
        {
            Content = new FormUrlEncodedContent([new(key: "data", value: query)])
        };
        using HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode
            || response.Content.Headers.ContentLength > MaximumResponseBytes)
        {
            return NearbyFeatureLookup.Unavailable;
        }

        byte[]? content = await HttpContentReader.ReadLimitedBytesAsync(
            response.Content,
            MaximumResponseBytes,
            cancellationToken).ConfigureAwait(false);
        if (content is null)
            return NearbyFeatureLookup.Unavailable;

        return new(IsAvailable: true, Parse(content, latitude, longitude));
    }

    private static ImmutableArray<NearbyFeature> Parse(
        byte[] content,
        double anomalyLatitude,
        double anomalyLongitude)
    {
        using var document = JsonDocument.Parse(content);
        if (!document.RootElement.TryGetProperty(propertyName: "elements", out JsonElement elements)
            || elements.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException(message: "The Overpass response does not contain an elements array.");
        }

        var features = new List<NearbyFeature>();
        var identities = new HashSet<(string Type, long Id)>();
        foreach (JsonElement element in elements.EnumerateArray())
        {
            if (!TryReadIdentity(element, out string? osmType, out long osmId)
                || !identities.Add((osmType, osmId))
                || !TryReadName(element, out string? name)
                || !TryReadCoordinates(element, osmType, out double latitude, out double longitude))
            {
                continue;
            }

            double distanceKilometers = Geography.HaversineKilometers(
                anomalyLatitude,
                anomalyLongitude,
                latitude,
                longitude);
            if (distanceKilometers > RadiusKilometers + DistanceToleranceKilometers)
                continue;

            features.Add(new(
                osmType,
                osmId,
                name,
                latitude,
                longitude,
                distanceKilometers,
                OpenStreetMapUrl: $"https://www.openstreetmap.org/{osmType}/{osmId.ToString(CultureInfo.InvariantCulture)}"));
        }

        return
        [
            .. features
                .OrderBy(feature => feature.DistanceKilometers)
                .ThenBy(feature => feature.OsmType, StringComparer.Ordinal)
                .ThenBy(feature => feature.OsmId)
                .Take(MaximumResults)
        ];
    }

    private static bool TryReadIdentity(
        JsonElement element,
        out string osmType,
        out long osmId)
    {
        osmType = string.Empty;
        osmId = 0;
        if (!element.TryGetProperty(propertyName: "type", out JsonElement typeElement)
            || typeElement.GetString() is not { } type
            || type is not ("node" or "way" or "relation")
            || !element.TryGetProperty(propertyName: "id", out JsonElement idElement)
            || !idElement.TryGetInt64(out osmId)
            || osmId <= 0)
        {
            return false;
        }

        osmType = type;
        return true;
    }

    private static bool TryReadName(JsonElement element, out string name)
    {
        name = string.Empty;
        if (!element.TryGetProperty(propertyName: "tags", out JsonElement tags)
            || tags.ValueKind != JsonValueKind.Object
            || !tags.TryGetProperty(propertyName: "name", out JsonElement nameElement)
            || nameElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(nameElement.GetString()))
        {
            return false;
        }

        name = nameElement.GetString()!.Trim();
        return true;
    }

    private static bool TryReadCoordinates(
        JsonElement element,
        string osmType,
        out double latitude,
        out double longitude)
    {
        latitude = 0;
        longitude = 0;
        JsonElement coordinates = element;
        if (!osmType.Equals(value: "node", StringComparison.Ordinal)
            && (!element.TryGetProperty(propertyName: "center", out coordinates)
                || coordinates.ValueKind != JsonValueKind.Object))
        {
            return false;
        }

        return coordinates.TryGetProperty(propertyName: "lat", out JsonElement latitudeElement)
            && latitudeElement.TryGetDouble(out latitude)
            && double.IsFinite(latitude)
            && latitude is >= -90 and <= 90
            && coordinates.TryGetProperty(propertyName: "lon", out JsonElement longitudeElement)
            && longitudeElement.TryGetDouble(out longitude)
            && double.IsFinite(longitude)
            && longitude is >= -180 and <= 180;
    }

    public void Dispose()
    {
        _requestGate.Dispose();
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Overpass API is temporarily unavailable for nearby-feature lookup at {Latitude}, {Longitude}")]
    private static partial void LogTemporarilyUnavailable(
        ILogger logger,
        double latitude,
        double longitude);

    private readonly record struct NearbyFeatureLookup(
        bool IsAvailable,
        ImmutableArray<NearbyFeature> Features)
    {
        public static NearbyFeatureLookup Unavailable { get; } = new(
            IsAvailable: false,
            Features: []);
    }
}
