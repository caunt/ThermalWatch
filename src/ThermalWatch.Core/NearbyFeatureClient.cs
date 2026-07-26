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
    private const int MaximumCachedResults = 25;
    private const int RadiusMeters = 2000;
    private const double RadiusKilometers = RadiusMeters / 1000d;
    private const double DistanceToleranceKilometers = 0.000001;
    private static readonly ImmutableArray<(string Key, string? Value)> s_tagBlacklist =
    [
        (Key: "route", Value: "bus"),
        (Key: "waterway", Value: null)
    ];
    private static readonly TimeSpan s_successCacheDuration = TimeSpan.FromHours(hours: 1);
    private static readonly TimeSpan s_failureCacheDuration = TimeSpan.FromMinutes(minutes: 1);
    private readonly SemaphoreSlim _requestGate = new(initialCount: 1, maxCount: 1);

    public async Task<ImmutableArray<NearbyFeature>> FindNearbyAsync(
        Anomaly anomaly,
        int maximumResults,
        CancellationToken cancellationToken) =>
        (await FindContextAsync(anomaly, maximumResults, cancellationToken).ConfigureAwait(false))
            .NearbyFeatures;

    internal async Task<NearbyMappedContext> FindContextAsync(
        Anomaly anomaly,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        if (maximumResults is < 1 or > MaximumCachedResults)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumResults),
                maximumResults,
                message: $"Maximum results must be between 1 and {MaximumCachedResults}.");
        }

        double latitude = Math.Round(anomaly.Latitude, digits: 6, MidpointRounding.AwayFromZero);
        double longitude = Math.Round(anomaly.Longitude, digits: 6, MidpointRounding.AwayFromZero);
        (string Prefix, double Latitude, double Longitude) cacheKey = (
            Prefix: "overpass:context",
            latitude,
            longitude);
        if (cache.TryGetValue(cacheKey, out NearbyMappedContext cached))
            return TakeResults(cached, maximumResults);

        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (cache.TryGetValue(cacheKey, out cached))
                return TakeResults(cached, maximumResults);

            NearbyMappedContextLookup lookup;
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
                lookup = NearbyMappedContextLookup.Unavailable;
            }

            if (!lookup.IsAvailable)
                LogTemporarilyUnavailable(logger, latitude, longitude);

            cache.Set(
                cacheKey,
                lookup.Context,
                new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = lookup.IsAvailable
                        ? s_successCacheDuration
                        : s_failureCacheDuration,
                    Size = Math.Max(
                        lookup.Context.NearbyFeatures.Length * 256
                            + (lookup.Context.SettlementName?.Length ?? 0) * sizeof(char),
                        val2: 1)
                });
            return TakeResults(lookup.Context, maximumResults);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task<NearbyMappedContextLookup> FetchAsync(
        double latitude,
        double longitude,
        CancellationToken cancellationToken)
    {
        string query = string.Create(
            CultureInfo.InvariantCulture,
            handler: $"[out:json][timeout:10];nwr(around:{RadiusMeters},{latitude:0.000000},{longitude:0.000000})[\"name\"][!\"highway\"][!\"railway\"][\"type\"!=\"public_transport\"];out center;is_in({latitude:0.000000},{longitude:0.000000})->.containing;area.containing[\"name\"][\"place\"~\"^(city|town|village)$\"];out tags;");
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
            return NearbyMappedContextLookup.Unavailable;
        }

        byte[]? content = await HttpContentReader.ReadLimitedBytesAsync(
            response.Content,
            MaximumResponseBytes,
            cancellationToken).ConfigureAwait(false);
        if (content is null)
            return NearbyMappedContextLookup.Unavailable;

        return new(IsAvailable: true, Parse(content, latitude, longitude));
    }

    private static NearbyMappedContext Parse(
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

        var features = new List<(NearbyFeature Feature, int TagCount)>();
        var settlements = new List<SettlementCandidate>();
        var identities = new HashSet<(string Type, long Id)>();
        foreach (JsonElement element in elements.EnumerateArray())
        {
            if (TryReadSettlement(element, out SettlementCandidate settlement))
                settlements.Add(settlement);

            if (!TryReadIdentity(element, out string? osmType, out long osmId)
                || HasBlacklistedTag(element)
                || !identities.Add((osmType, osmId))
                || !TryReadName(element, out string? name, out int tagCount)
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

            features.Add((
                Feature: new(
                    osmType,
                    osmId,
                    name,
                    latitude,
                    longitude,
                    distanceKilometers,
                    OpenStreetMapUrl: $"https://www.openstreetmap.org/{osmType}/{osmId.ToString(CultureInfo.InvariantCulture)}"),
                TagCount: tagCount));
        }

        return new(SelectSettlementName(settlements), OrderNearbyFeatures(features));
    }

    private static string? SelectSettlementName(IEnumerable<SettlementCandidate> settlements) =>
        settlements
            .OrderByDescending(settlement => settlement.AdminLevel)
            .ThenByDescending(settlement => settlement.PlaceSpecificity)
            .ThenBy(settlement => settlement.OsmAreaId)
            .ThenBy(settlement => settlement.Name, StringComparer.Ordinal)
            .Select(settlement => settlement.Name)
            .FirstOrDefault();

    private static ImmutableArray<NearbyFeature> OrderNearbyFeatures(
        IEnumerable<(NearbyFeature Feature, int TagCount)> features) =>
    [
        .. features
            .OrderByDescending(result => result.TagCount)
            .ThenBy(result => result.Feature.DistanceKilometers)
            .ThenBy(result => result.Feature.OsmType, StringComparer.Ordinal)
            .ThenBy(result => result.Feature.OsmId)
            .Take(MaximumCachedResults)
            .Select(result => result.Feature)
    ];

    private static NearbyMappedContext TakeResults(
        NearbyMappedContext context,
        int maximumResults) =>
        context.NearbyFeatures.Length <= maximumResults
            ? context
            : context with { NearbyFeatures = [.. context.NearbyFeatures.Take(maximumResults)] };

    private static bool TryReadSettlement(
        JsonElement element,
        out SettlementCandidate settlement)
    {
        settlement = default;
        if (!element.TryGetProperty(propertyName: "type", out JsonElement typeElement)
            || typeElement.GetString() is not "area"
            || !element.TryGetProperty(propertyName: "id", out JsonElement idElement)
            || !idElement.TryGetInt64(out long osmAreaId)
            || osmAreaId <= 0
            || !element.TryGetProperty(propertyName: "tags", out JsonElement tags)
            || tags.ValueKind != JsonValueKind.Object
            || !TryReadTrimmedTag(tags, key: "place", out string? place)
            || GetPlaceSpecificity(place) is not { } placeSpecificity
            || !TryReadSettlementName(tags, out string? name))
        {
            return false;
        }

        int adminLevel = TryReadTrimmedTag(tags, key: "admin_level", out string? value)
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : -1;
        settlement = new(osmAreaId, name, adminLevel, placeSpecificity);
        return true;
    }

    private static bool TryReadSettlementName(JsonElement tags, out string name) =>
        TryReadTrimmedTag(tags, key: "name:en", out name)
        || TryReadTrimmedTag(tags, key: "name", out name);

    private static bool TryReadTrimmedTag(
        JsonElement tags,
        string key,
        out string value)
    {
        value = string.Empty;
        if (!tags.TryGetProperty(key, out JsonElement element)
            || element.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(element.GetString()))
        {
            return false;
        }

        value = element.GetString()!.Trim();
        return true;
    }

    private static int? GetPlaceSpecificity(string place) => place switch
    {
        "village" => 3,
        "town" => 2,
        "city" => 1,
        _ => null
    };

    private static bool HasBlacklistedTag(JsonElement element)
    {
        if (!element.TryGetProperty(propertyName: "tags", out JsonElement tags)
            || tags.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach ((string key, string? value) in s_tagBlacklist)
        {
            if (tags.TryGetProperty(propertyName: key, out JsonElement tagValue)
                && tagValue.ValueKind == JsonValueKind.String
                && (value is null || tagValue.ValueEquals(value)))
            {
                return true;
            }
        }

        return false;
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

    private static bool TryReadName(JsonElement element, out string name, out int tagCount)
    {
        name = string.Empty;
        tagCount = 0;
        if (!element.TryGetProperty(propertyName: "tags", out JsonElement tags)
            || tags.ValueKind != JsonValueKind.Object
            || !tags.TryGetProperty(propertyName: "name", out JsonElement nameElement)
            || nameElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(nameElement.GetString()))
        {
            return false;
        }

        name = nameElement.GetString()!.Trim();
        tagCount = tags.EnumerateObject().Count();
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
        Message = "Overpass API is temporarily unavailable for mapped-context lookup at {Latitude}, {Longitude}")]
    private static partial void LogTemporarilyUnavailable(
        ILogger logger,
        double latitude,
        double longitude);

    private readonly record struct NearbyMappedContextLookup(
        bool IsAvailable,
        NearbyMappedContext Context)
    {
        public static NearbyMappedContextLookup Unavailable { get; } = new(
            IsAvailable: false,
            NearbyMappedContext.Empty);
    }

    private readonly record struct SettlementCandidate(
        long OsmAreaId,
        string Name,
        int AdminLevel,
        int PlaceSpecificity);
}
