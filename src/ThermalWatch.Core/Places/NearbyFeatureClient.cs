using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

using Nito.AsyncEx;

namespace ThermalWatch.Core;

/// <summary>Retrieves ranked nearby OpenStreetMap features and a containing settlement for presentation context.</summary>
/// <param name="httpClient">The host-owned Overpass HTTP client.</param>
/// <param name="cache">The bounded coordinate-context cache.</param>
/// <param name="logger">The provider availability logger.</param>
public sealed partial class NearbyFeatureClient(HttpClient httpClient, IMemoryCache cache, ILogger<NearbyFeatureClient> logger)
{
    private const double DistanceToleranceKilometers = 0.000001;
    private const int MaximumCachedResults = 25;
    private const int MaximumResponseBytes = 10 * 1024 * 1024;
    private const double RadiusKilometers = RadiusMeters / 1000d;
    private const int RadiusMeters = 2000;
    private static readonly ImmutableArray<(string Key, string? Value)> TagBlacklist =
    [
        (Key: "route", Value: "bus"),
        (Key: "waterway", Value: null)
    ];
    private static readonly TimeSpan SuccessCacheDuration = TimeSpan.FromHours(hours: 1);
    private static readonly TimeSpan FailureCacheDuration = TimeSpan.FromMinutes(minutes: 1);
    private readonly AsyncSemaphore _requestGate = new(initialCount: 1);

    /// <summary>Returns nearby features in tag-count, distance, and stable-identity order.</summary>
    /// <param name="anomaly">The selected observation's coordinates.</param>
    /// <param name="maximumResults">The number of ranked results to retain, up to the cached limit.</param>
    /// <param name="cancellationToken">Cancels waiting, acquisition, and parsing.</param>
    public async Task<ImmutableArray<NearbyFeature>> FindNearbyAsync(Anomaly anomaly, int maximumResults, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(anomaly);

        return (await FindContextAsync(anomaly, maximumResults, cancellationToken).ConfigureAwait(continueOnCapturedContext: false))
            .NearbyFeatures;
    }

    internal async Task<NearbyMappedContext> FindContextAsync(Anomaly anomaly, int maximumResults, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(anomaly);

        if (maximumResults is < 1 or > MaximumCachedResults)
            throw new ArgumentOutOfRangeException(nameof(maximumResults), $"Maximum results must be between 1 and {MaximumCachedResults}.");

        double latitude = Math.Round(anomaly.Latitude, digits: 6, MidpointRounding.AwayFromZero);
        double longitude = Math.Round(anomaly.Longitude, digits: 6, MidpointRounding.AwayFromZero);

        var cacheKey = new ContextCacheKey(latitude, longitude);

        if (cache.TryGetValue(cacheKey, out NearbyMappedContext? cached) && cached is not null)
            return TakeResults(cached, maximumResults);

        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        try
        {
            if (cache.TryGetValue(cacheKey, out cached) && cached is not null)
                return TakeResults(cached, maximumResults);

            NearbyMappedContextLookup lookup;

            try
            {
                lookup = await FetchAsync(latitude, longitude, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or OperationCanceledException)
            {
                lookup = NearbyMappedContextLookup.Unavailable;
            }

            if (!lookup.IsAvailable)
                LogTemporarilyUnavailable(logger, latitude, longitude);

            NearbyMappedContext cachedContext = cache.Set(
                cacheKey,
                lookup.Context,
                new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = lookup.IsAvailable
                        ? SuccessCacheDuration
                        : FailureCacheDuration,
                    Size = Math.Max((lookup.Context.NearbyFeatures.Length * 256) + ((lookup.Context.SettlementName?.Length ?? 0) * sizeof(char)), val2: 1)
                }
            );

            return TakeResults(cachedContext, maximumResults);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private static int? GetPlaceSpecificity(string place)
    {
        return place switch
        {
            "village" => 3,
            "town" => 2,
            "city" => 1,
            _ => null
        };
    }

    private static bool HasBlacklistedTag(JsonElement element)
    {
        if (!element.TryGetProperty(propertyName: "tags", out JsonElement tags) || tags.ValueKind != JsonValueKind.Object)
            return false;

        foreach ((string key, string? value) in TagBlacklist)
        {
            if (!tags.TryGetProperty(key, out JsonElement tagValue) || tagValue.ValueKind != JsonValueKind.String)
                continue;

            if (value is null || tagValue.ValueEquals(value))
                return true;
        }

        return false;
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Overpass API is temporarily unavailable for mapped-context lookup at {Latitude}, {Longitude}"
    )]
    private static partial void LogTemporarilyUnavailable(ILogger logger, double latitude, double longitude);

    private static ImmutableArray<NearbyFeature> OrderNearbyFeatures(IEnumerable<(NearbyFeature Feature, int TagCount)> features)
    {
        return [
        .. features
            .OrderByDescending(static result => result.TagCount)
            .ThenBy(static result => result.Feature.DistanceKilometers)
            .ThenBy(static result => result.Feature.OsmType, StringComparer.Ordinal)
            .ThenBy(static result => result.Feature.OsmId)
            .Take(MaximumCachedResults)
            .Select(static result => result.Feature)
    ];
    }

    private static NearbyMappedContext Parse(byte[] content, double anomalyLatitude, double anomalyLongitude)
    {
        using var document = JsonDocument.Parse(content);

        if (!document.RootElement.TryGetProperty(propertyName: "elements", out JsonElement elements))
            throw new JsonException(message: "The Overpass response does not contain an elements array.");

        if (elements.ValueKind != JsonValueKind.Array)
            throw new JsonException(message: "The Overpass response does not contain an elements array.");

        var features = new List<(NearbyFeature Feature, int TagCount)>();
        var settlements = new List<SettlementCandidate>();
        var identities = new HashSet<(string Type, long Id)>();

        foreach (JsonElement element in elements.EnumerateArray())
        {
            if (TryReadSettlement(element, out SettlementCandidate settlement))
                settlements.Add(settlement);

            if (!TryReadIdentity(element, out string osmType, out long osmId) || HasBlacklistedTag(element))
                continue;

            if (!identities.Add((osmType, osmId)) || !TryReadName(element, out string name, out int tagCount))
                continue;

            if (!TryReadCoordinates(element, osmType, out double latitude, out double longitude))
                continue;

            double distanceKilometers = Geography.HaversineKilometers(anomalyLatitude, anomalyLongitude, latitude, longitude);

            if (distanceKilometers > RadiusKilometers + DistanceToleranceKilometers)
                continue;

            features.Add(
                (
                Feature: new(
                    osmType,
                    osmId,
                    name,
                    latitude,
                    longitude,
                    distanceKilometers,
                    new Uri($"https://www.openstreetmap.org/{osmType}/{osmId.ToString(CultureInfo.InvariantCulture)}")
                ),
                TagCount: tagCount)
            );
        }

        return new(SelectSettlementName(settlements), OrderNearbyFeatures(features));
    }

    private static string? SelectSettlementName(IEnumerable<SettlementCandidate> settlements)
    {
        return settlements
            .OrderByDescending(static settlement => settlement.AdminLevel)
            .ThenByDescending(static settlement => settlement.PlaceSpecificity)
            .ThenBy(static settlement => settlement.OsmAreaId)
            .ThenBy(static settlement => settlement.Name, StringComparer.Ordinal)
            .Select(static settlement => settlement.Name)
            .FirstOrDefault();
    }

    private static NearbyMappedContext TakeResults(NearbyMappedContext context, int maximumResults)
    {
        return context.NearbyFeatures.Length <= maximumResults
            ? context
            : context with { NearbyFeatures = context.NearbyFeatures[..maximumResults] };
    }

    private static bool TryReadCoordinates(JsonElement element, string osmType, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;
        JsonElement coordinates = element;

        return (osmType.Equals(value: "node", StringComparison.Ordinal)
            || (element.TryGetProperty(propertyName: "center", out coordinates)
                && coordinates.ValueKind == JsonValueKind.Object)) && coordinates.TryGetProperty(propertyName: "lat", out JsonElement latitudeElement)
            && latitudeElement.TryGetDouble(out latitude)
            && double.IsFinite(latitude)
            && latitude is >= -90 and <= 90
            && coordinates.TryGetProperty(propertyName: "lon", out JsonElement longitudeElement)
            && longitudeElement.TryGetDouble(out longitude)
            && double.IsFinite(longitude)
            && longitude is >= -180 and <= 180;
    }

    private static bool TryReadIdentity(JsonElement element, out string osmType, out long osmId)
    {
        osmType = string.Empty;
        osmId = 0;

        if (!element.TryGetProperty(propertyName: "type", out JsonElement typeElement))
            return false;

        if (typeElement.GetString() is not { } type || type is not ("node" or "way" or "relation"))
            return false;

        if (!element.TryGetProperty(propertyName: "id", out JsonElement idElement))
            return false;

        if (!idElement.TryGetInt64(out osmId) || osmId <= 0)
            return false;

        osmType = type;

        return true;
    }

    private static bool TryReadName(JsonElement element, out string name, out int tagCount)
    {
        name = string.Empty;
        tagCount = 0;

        if (!element.TryGetProperty(propertyName: "tags", out JsonElement tags) || tags.ValueKind != JsonValueKind.Object)
            return false;

        if (!tags.TryGetProperty(propertyName: "name", out JsonElement nameElement))
            return false;

        if (nameElement.ValueKind != JsonValueKind.String)
            return false;

        if (nameElement.GetString() is not { } rawName || string.IsNullOrWhiteSpace(rawName))
            return false;

        name = rawName.Trim();
        tagCount = tags.GetPropertyCount();

        return true;
    }

    private static bool TryReadSettlement(JsonElement element, out SettlementCandidate settlement)
    {
        settlement = default;

        if (!element.TryGetProperty(propertyName: "type", out JsonElement typeElement))
            return false;

        if (typeElement.GetString() is not "area" || !element.TryGetProperty(propertyName: "id", out JsonElement idElement))
            return false;

        if (!idElement.TryGetInt64(out long osmAreaId) || osmAreaId <= 0)
            return false;

        if (!element.TryGetProperty(propertyName: "tags", out JsonElement tags) || tags.ValueKind != JsonValueKind.Object)
            return false;

        if (!TryReadTrimmedTag(tags, key: "place", out string place))
            return false;

        if (GetPlaceSpecificity(place) is not { } placeSpecificity || !TryReadSettlementName(tags, out string name))
            return false;

        int adminLevel = -1;

        if (TryReadTrimmedTag(tags, key: "admin_level", out string value))
        {
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
                adminLevel = parsed;
        }

        settlement = new(osmAreaId, name, adminLevel, placeSpecificity);

        return true;
    }

    private static bool TryReadSettlementName(JsonElement tags, out string name)
    {
        return TryReadTrimmedTag(tags, key: "name:en", out name)
        || TryReadTrimmedTag(tags, key: "name", out name);
    }

    private static bool TryReadTrimmedTag(JsonElement tags, string key, out string value)
    {
        value = string.Empty;

        if (!tags.TryGetProperty(key, out JsonElement element) || element.ValueKind != JsonValueKind.String)
            return false;

        if (element.GetString() is not { } text || string.IsNullOrWhiteSpace(text))
            return false;

        value = text.Trim();

        return true;
    }

    private async Task<NearbyMappedContextLookup> FetchAsync(double latitude, double longitude, CancellationToken cancellationToken)
    {
        string query = string.Create(
            CultureInfo.InvariantCulture,
            $"[out:json][timeout:10];nwr(around:{RadiusMeters},{latitude:0.000000},{longitude:0.000000})[\"name\"][!\"highway\"][!\"railway\"][\"type\"!=\"public_transport\"];out center;is_in({latitude:0.000000},{longitude:0.000000})->.containing;area.containing[\"name\"][\"place\"~\"^(city|town|village)$\"];out tags;"
        );

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri: "interpreter")
        {
            Content = new FormUrlEncodedContent([new(key: "data", query)])
        };

        using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumResponseBytes)
            return NearbyMappedContextLookup.Unavailable;

        byte[]? content = await HttpContentReader.ReadLimitedBytesAsync(response.Content, MaximumResponseBytes, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        return content is null ? NearbyMappedContextLookup.Unavailable : new(IsAvailable: true, Parse(content, latitude, longitude));
    }

    private readonly record struct NearbyMappedContextLookup(bool IsAvailable, NearbyMappedContext Context)
    {
        public static NearbyMappedContextLookup Unavailable { get; } = new(IsAvailable: false, NearbyMappedContext.Empty);
    }

    private readonly record struct SettlementCandidate(long OsmAreaId, string Name, int AdminLevel, int PlaceSpecificity);

    private sealed record ContextCacheKey(double Latitude, double Longitude);
}
