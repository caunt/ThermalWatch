using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;

namespace ThermalWatch.Core;

public sealed class FirmsClient(
    HttpClient httpClient,
    FirmsOptions options,
    CountryBoundaryCatalog boundaries,
    TimeProvider timeProvider,
    ILogger<FirmsClient> logger)
{
    private const int MaximumResponseCharacters = 50_000_000;
    private const int MaximumErrorBodyCharacters = 4096;
    private static readonly TimeSpan CountryProbeInterval = TimeSpan.FromHours(1);
    private readonly SemaphoreSlim _capabilityGate = new(1, 1);
    private readonly SemaphoreSlim _requestGate = new(options.MaxConcurrency, options.MaxConcurrency);
    private readonly Lock _capabilitySync = new();
    private CountryApiCapability _countryCapability;
    private DateTimeOffset _nextCountryProbeUtc = DateTimeOffset.MinValue;

    public async Task<ImmutableArray<Anomaly>> GetDetectionsAsync(
        string countryCode,
        string source,
        CancellationToken cancellationToken) =>
        (await GetSegmentAsync(countryCode, source, cancellationToken)).Detections;

    public async Task<FirmsSegmentResult> GetSegmentAsync(
        string countryCode,
        string source,
        CancellationToken cancellationToken)
    {
        if (!options.Countries.Contains(countryCode)
            || !FirmsSources.All.Contains(source))
        {
            throw new FirmsRequestException("FIRMS request parameters are invalid.");
        }

        var capability = GetCountryCapability();
        if (capability == CountryApiCapability.Available)
            return await GetUsingCountryAsync(countryCode, source, cancellationToken);

        if (capability == CountryApiCapability.TemporarilyUnavailable
            && timeProvider.GetUtcNow() < GetNextCountryProbeUtc())
        {
            return await GetUsingAreaAsync(countryCode, source, cancellationToken);
        }

        await _capabilityGate.WaitAsync(cancellationToken);
        try
        {
            capability = GetCountryCapability();
            if (capability == CountryApiCapability.Available)
                return await GetUsingCountryAsync(countryCode, source, cancellationToken);

            if (capability == CountryApiCapability.TemporarilyUnavailable
                && timeProvider.GetUtcNow() < GetNextCountryProbeUtc())
            {
                return await GetUsingAreaAsync(countryCode, source, cancellationToken);
            }

            try
            {
                var detections = await GetCountryDetectionsAsync(
                    countryCode,
                    source,
                    cancellationToken);
                MarkCountryAvailable(capability);
                return new(detections, IngestionModes.Country);
            }
            catch (CountryFeatureUnavailableException)
            {
                MarkCountryUnavailable();
                return await GetUsingAreaAsync(countryCode, source, cancellationToken);
            }
            catch (FirmsRequestException exception)
                when (capability == CountryApiCapability.TemporarilyUnavailable)
            {
                ScheduleNextCountryProbe();
                logger.LogWarning(
                    "Country API probe failed; continuing area fallback: {SafeError}",
                    exception.SafeMessage);
                return await GetUsingAreaAsync(countryCode, source, cancellationToken);
            }
        }
        finally
        {
            _capabilityGate.Release();
        }
    }

    private async Task<FirmsSegmentResult> GetUsingCountryAsync(
        string countryCode,
        string source,
        CancellationToken cancellationToken)
    {
        try
        {
            var detections = await GetCountryDetectionsAsync(countryCode, source, cancellationToken);
            return new(detections, IngestionModes.Country);
        }
        catch (CountryFeatureUnavailableException)
        {
            MarkCountryUnavailable();
            return await GetUsingAreaAsync(countryCode, source, cancellationToken);
        }
    }

    private async Task<FirmsSegmentResult> GetUsingAreaAsync(
        string countryCode,
        string source,
        CancellationToken cancellationToken)
    {
        var boundary = boundaries.Get(countryCode);
        logger.LogDebug(
            "Refreshing FIRMS segment {Country} {Source} with {TileCount} area fallback tiles",
            countryCode,
            source,
            boundary.Tiles.Length);

        ImmutableArray<Anomaly>[] tileDetections;
        try
        {
            tileDetections = await Task.WhenAll(boundary.Tiles.Select(tile =>
                GetAreaDetectionsAsync(countryCode, source, tile, cancellationToken)));
        }
        catch (FirmsRequestException exception)
        {
            throw new FirmsRequestException(
                $"FIRMS area fallback tile failed: {exception.SafeMessage}");
        }

        var detections = tileDetections
            .SelectMany(tile => tile)
            .Where(detection => boundary.Prepared.Covers(
                boundary.Geometry.Factory.CreatePoint(
                    new Coordinate(detection.Longitude, detection.Latitude))))
            .DistinctBy(detection => detection.Id)
            .ToImmutableArray();
        return new(detections, IngestionModes.AreaFallback);
    }

    private async Task<ImmutableArray<Anomaly>> GetCountryDetectionsAsync(
        string countryCode,
        string source,
        CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"api/country/csv/{Uri.EscapeDataString(options.MapKey)}/{source}/{countryCode}/1");
            using var response = await SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode
                && await IsCountryFeatureUnavailableAsync(response, cancellationToken))
            {
                throw new CountryFeatureUnavailableException();
            }

            return await ReadCsvResponseAsync(response, countryCode, source, cancellationToken);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task<ImmutableArray<Anomaly>> GetAreaDetectionsAsync(
        string countryCode,
        string source,
        GeographicBounds bounds,
        CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"api/area/csv/{Uri.EscapeDataString(options.MapKey)}/{source}/{bounds.ToInvariantString()}/1");
            using var response = await SendAsync(request, cancellationToken);
            return await ReadCsvResponseAsync(response, countryCode, source, cancellationToken);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;

        try
        {
            response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FirmsRequestException("FIRMS request timed out.");
        }
        catch (HttpRequestException)
        {
            throw new FirmsRequestException("FIRMS is unavailable.");
        }
        catch (Exception exception) when (exception.GetType().Name == "TimeoutRejectedException")
        {
            throw new FirmsRequestException("FIRMS request timed out.");
        }
        return response;
    }

    private async Task<ImmutableArray<Anomaly>> ReadCsvResponseAsync(
        HttpResponseMessage response,
        string countryCode,
        string source,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new FirmsRequestException("FIRMS rejected the MAP_KEY.");

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new FirmsRequestException("FIRMS rate limit was reached.");

        if (!response.IsSuccessStatusCode)
            throw new FirmsRequestException("FIRMS returned an upstream error.");

        if (response.Content.Headers.ContentLength > MaximumResponseCharacters)
            throw new FirmsRequestException("FIRMS response was too large.");

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null
            && (mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)))
        {
            throw new FirmsRequestException("FIRMS returned an invalid dataset.");
        }

        var content = await ReadLimitedAsync(response.Content, cancellationToken);
        return ParseCsv(content, countryCode, source);
    }

    private async Task<bool> IsCountryFeatureUnavailableAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var statusCode = (int)response.StatusCode;
        if (response.StatusCode is not (HttpStatusCode.BadRequest
                or HttpStatusCode.NotFound
                or HttpStatusCode.Gone
                or HttpStatusCode.NotImplemented)
            && statusCode is not (>= 500 and <= 599))
        {
            return false;
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null
            && !mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            && !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
            && !mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var body = NormalizeErrorBody(await ReadPrefixAsync(
            response.Content,
            MaximumErrorBodyCharacters,
            cancellationToken));
        if (ContainsAny(
                body,
                "map key",
                "mapkey",
                "api key",
                "credential",
                "authentication",
                "unauthorized",
                "forbidden",
                "invalid request",
                "malformed request",
                "invalid country",
                "country code",
                "invalid parameter"))
        {
            return false;
        }

        var identifiesCountryFeature = ContainsAny(
            body,
            "country api",
            "country endpoint",
            "country feature",
            "country service",
            "api country");
        var identifiesUnavailableState = ContainsAny(
            body,
            "unavailable",
            "not available",
            "disabled",
            "unsupported",
            "not supported",
            "not implemented",
            "not found",
            "no longer available",
            "retired",
            "decommissioned",
            "gone");
        if (identifiesCountryFeature && identifiesUnavailableState)
            return true;

        var isAmbiguousDisabledEndpointResponse = response.StatusCode == HttpStatusCode.BadRequest
            && ContainsAny(body, "invalid api", "api call is invalid", "invalid endpoint call");
        return isAmbiguousDisabledEndpointResponse
            && await IsMapKeyUsableAsync(cancellationToken);
    }

    private async Task<bool> IsMapKeyUsableAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"mapserver/mapkey_status/?MAP_KEY={Uri.EscapeDataString(options.MapKey)}");
        using var response = await SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
            return false;

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null
            && !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            throw new FirmsRequestException("FIRMS returned an invalid MAP_KEY status.");
        }

        try
        {
            var content = await ReadPrefixAsync(
                response.Content,
                MaximumErrorBodyCharacters,
                cancellationToken);
            using var document = JsonDocument.Parse(content);
            return document.RootElement.TryGetProperty("transaction_limit", out var limitProperty)
                && limitProperty.TryGetInt32(out var limit)
                && document.RootElement.TryGetProperty(
                    "current_transactions",
                    out var currentProperty)
                && currentProperty.TryGetInt32(out var current)
                && limit > 0
                && current < limit;
        }
        catch (JsonException)
        {
            throw new FirmsRequestException("FIRMS returned an invalid MAP_KEY status.");
        }
    }

    private static string NormalizeErrorBody(string body)
    {
        var normalized = new StringBuilder(body.Length);
        var previousWasSpace = true;

        foreach (var character in body)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                normalized.Append(char.ToLowerInvariant(character));
                previousWasSpace = false;
            }
            else if (!previousWasSpace)
            {
                normalized.Append(' ');
                previousWasSpace = true;
            }
        }

        return normalized.ToString();
    }

    private static bool ContainsAny(string value, params ReadOnlySpan<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (value.Contains(candidate, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private CountryApiCapability GetCountryCapability()
    {
        lock (_capabilitySync)
            return _countryCapability;
    }

    private DateTimeOffset GetNextCountryProbeUtc()
    {
        lock (_capabilitySync)
            return _nextCountryProbeUtc;
    }

    private void MarkCountryAvailable(CountryApiCapability previous)
    {
        lock (_capabilitySync)
        {
            _countryCapability = CountryApiCapability.Available;
            _nextCountryProbeUtc = DateTimeOffset.MinValue;
        }

        logger.LogInformation(previous == CountryApiCapability.TemporarilyUnavailable
            ? "Country API restored; disabling area fallback"
            : "Country API available");
    }

    private void MarkCountryUnavailable()
    {
        var changed = false;
        lock (_capabilitySync)
        {
            if (_countryCapability != CountryApiCapability.TemporarilyUnavailable)
                changed = true;

            _countryCapability = CountryApiCapability.TemporarilyUnavailable;
            _nextCountryProbeUtc = timeProvider.GetUtcNow() + CountryProbeInterval;
        }

        if (changed)
            logger.LogWarning("Country API unavailable; using area fallback");
    }

    private void ScheduleNextCountryProbe()
    {
        lock (_capabilitySync)
            _nextCountryProbeUtc = timeProvider.GetUtcNow() + CountryProbeInterval;
    }

    private enum CountryApiCapability
    {
        Unknown,
        Available,
        TemporarilyUnavailable
    }

    private sealed class CountryFeatureUnavailableException : Exception;

    private ImmutableArray<Anomaly> ParseCsv(string content, string countryCode, string source)
    {
        if (string.IsNullOrWhiteSpace(content) || content.AsSpan().TrimStart().StartsWith('<'))
            throw new FirmsRequestException("FIRMS returned an invalid dataset.");

        var configuration = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            BadDataFound = null,
            HeaderValidated = null,
            MissingFieldFound = null,
            PrepareHeaderForMatch = args => args.Header.Trim().TrimStart('\uFEFF'),
            TrimOptions = TrimOptions.Trim
        };

        try
        {
            using var reader = new StringReader(content);
            using var csv = new CsvReader(reader, configuration);

            if (!csv.Read() || !csv.ReadHeader() || csv.HeaderRecord is not { Length: > 0 } headers)
                throw new FirmsRequestException("FIRMS returned an invalid CSV structure.");

            ValidateHeaders(headers, source);

            var detections = ImmutableArray.CreateBuilder<Anomaly>();
            var dataRowCount = 0;
            var skippedRowCount = 0;

            while (csv.Read())
            {
                dataRowCount++;

                try
                {
                    detections.Add(ParseRow(csv, countryCode, source));
                }
                catch (Exception exception) when (exception is FormatException or CsvHelperException)
                {
                    skippedRowCount++;
                    logger.LogWarning(
                        "Skipped malformed FIRMS row {RowNumber} for {Country} {Source}",
                        csv.Parser.Row,
                        countryCode,
                        source);
                }
            }

            if (dataRowCount > 0 && detections.Count == 0)
                throw new FirmsRequestException("FIRMS returned no usable data rows.");

            if (skippedRowCount > 0)
            {
                logger.LogWarning(
                    "Skipped {SkippedRowCount} malformed FIRMS rows for {Country} {Source}",
                    skippedRowCount,
                    countryCode,
                    source);
            }

            return [.. detections.DistinctBy(detection => detection.Id)];
        }
        catch (FirmsRequestException)
        {
            throw;
        }
        catch (Exception exception) when (exception is CsvHelperException or IOException)
        {
            throw new FirmsRequestException("FIRMS returned an invalid CSV structure.");
        }
    }

    private static Anomaly ParseRow(CsvReader csv, string countryCode, string source)
    {
        var latitude = ParseRequiredDouble(csv, "latitude");
        var longitude = ParseRequiredDouble(csv, "longitude");

        if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
            throw new FormatException("Coordinates are outside their valid ranges.");

        var satellite = GetRequired(csv, "satellite");
        var instrument = GetRequired(csv, "instrument");
        var dayNight = GetRequired(csv, "daynight").ToUpperInvariant();

        if (dayNight is not ("D" or "N"))
            throw new FormatException("daynight is invalid.");

        var acquiredAtUtc = ParseAcquisitionTime(
            GetRequired(csv, "acq_date"),
            GetRequired(csv, "acq_time"));

        var isModis = source == "MODIS_NRT";
        var confidenceRaw = GetOptional(csv, "confidence");
        double? confidencePercent = isModis && confidenceRaw is not null
            ? ParseDouble(confidenceRaw)
            : null;
        var confidenceCategory = isModis ? null : NormalizeConfidenceCategory(confidenceRaw);
        var googleMapsUrl = string.Create(
            CultureInfo.InvariantCulture,
            $"https://www.google.com/maps/search/?api=1&query={latitude:0.######},{longitude:0.######}");

        return new(
            AnomalyId.Create(countryCode, source, satellite, acquiredAtUtc, latitude, longitude),
            countryCode,
            source,
            satellite,
            instrument,
            latitude,
            longitude,
            acquiredAtUtc,
            dayNight,
            ParseOptionalDouble(csv, isModis ? "brightness" : "bright_ti4"),
            ParseOptionalDouble(csv, isModis ? "bright_t31" : "bright_ti5"),
            ParseOptionalDouble(csv, "frp"),
            ParseOptionalDouble(csv, "scan"),
            ParseOptionalDouble(csv, "track"),
            confidenceRaw,
            confidencePercent,
            confidenceCategory,
            GetOptional(csv, "version"),
            googleMapsUrl);
    }

    private static void ValidateHeaders(IEnumerable<string> headers, string source)
    {
        var available = headers
            .Select(header => header.Trim().TrimStart('\uFEFF'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string[] common =
        [
            "latitude", "longitude", "scan", "track", "acq_date", "acq_time",
            "satellite", "instrument", "confidence", "version", "frp", "daynight"
        ];
        string[] sourceSpecific = source == "MODIS_NRT"
            ? ["brightness", "bright_t31"]
            : ["bright_ti4", "bright_ti5"];

        if (common.Concat(sourceSpecific).Any(header => !available.Contains(header)))
            throw new FirmsRequestException("FIRMS CSV is missing required headers.");
    }

    private static DateTimeOffset ParseAcquisitionTime(string date, string time)
    {
        var paddedTime = time.PadLeft(4, '0');

        if (paddedTime.Length != 4
            || !DateTimeOffset.TryParseExact(
                date + paddedTime,
                "yyyy-MM-ddHHmm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var acquiredAtUtc))
        {
            throw new FormatException("Acquisition timestamp is invalid.");
        }

        return acquiredAtUtc;
    }

    private static string GetRequired(CsvReader csv, string name) =>
        GetOptional(csv, name) is { } value
            ? value
            : throw new FormatException($"{name} is required.");

    private static string? GetOptional(CsvReader csv, string name)
    {
        var value = csv.GetField(name)?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static double ParseRequiredDouble(CsvReader csv, string name) =>
        ParseDouble(GetRequired(csv, name));

    private static double? ParseOptionalDouble(CsvReader csv, string name) =>
        GetOptional(csv, name) is { } value ? ParseDouble(value) : null;

    private static double ParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && double.IsFinite(parsed)
                ? parsed
                : throw new FormatException("Numeric FIRMS value is invalid.");

    private static string? NormalizeConfidenceCategory(string? value) => value?.ToLowerInvariant() switch
    {
        "l" or "low" => "low",
        "n" or "nominal" => "nominal",
        "h" or "high" => "high",
        null => null,
        var category => category
    };

    private static async Task<string> ReadLimitedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, leaveOpen: false);
        var result = new StringBuilder();
        var buffer = new char[8192];

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                return result.ToString();

            result.Append(buffer, 0, read);
            if (result.Length > MaximumResponseCharacters)
                throw new FirmsRequestException("FIRMS response was too large.");
        }
    }

    private static async Task<string> ReadPrefixAsync(
        HttpContent content,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, leaveOpen: false);
        var result = new StringBuilder(maximumCharacters);
        var buffer = new char[Math.Min(1024, maximumCharacters)];

        while (result.Length < maximumCharacters)
        {
            var remaining = Math.Min(buffer.Length, maximumCharacters - result.Length);
            var read = await reader.ReadAsync(buffer.AsMemory(0, remaining), cancellationToken);
            if (read == 0)
                break;

            result.Append(buffer, 0, read);
        }

        return result.ToString();
    }
}

public sealed class FirmsRequestException(string safeMessage) : Exception(safeMessage)
{
    public string SafeMessage { get; } = safeMessage;
}
