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

public sealed partial class FirmsClient(
    HttpClient httpClient,
    FirmsOptions options,
    CountryBoundaryCatalog boundaries,
    TimeProvider timeProvider,
    ILogger<FirmsClient> logger) : IDisposable
{
    private const int MaximumResponseCharacters = 50_000_000;
    private const int MaximumErrorBodyCharacters = 4096;
    private static readonly TimeSpan s_countryProbeInterval = TimeSpan.FromHours(hours: 1);
    private readonly SemaphoreSlim _capabilityGate = new(initialCount: 1, maxCount: 1);
    private readonly SemaphoreSlim _requestGate = new(options.MaxConcurrency, options.MaxConcurrency);
    private readonly Lock _capabilitySync = new();
    private CountryApiCapability _countryCapability;
    private DateTimeOffset _nextCountryProbeUtc = DateTimeOffset.MinValue;

    public async Task<FirmsSegmentResult> GetSegmentAsync(
        string countryCode,
        string source,
        CancellationToken cancellationToken)
    {
        if (!options.Countries.Contains(countryCode, StringComparer.Ordinal)
            || !FirmsSources.All.Contains(source, StringComparer.Ordinal))
        {
            throw new FirmsRequestException(safeMessage: "FIRMS request parameters are invalid.");
        }

        CountryApiCapability capability = GetCountryCapability();
        if (capability == CountryApiCapability.Available)
            return await GetUsingCountryAsync(countryCode, source, cancellationToken).ConfigureAwait(false);

        if (capability == CountryApiCapability.TemporarilyUnavailable
            && timeProvider.GetUtcNow() < GetNextCountryProbeUtc())
        {
            return await GetUsingAreaAsync(countryCode, source, cancellationToken).ConfigureAwait(false);
        }

        await _capabilityGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            capability = GetCountryCapability();
            if (capability == CountryApiCapability.Available)
                return await GetUsingCountryAsync(countryCode, source, cancellationToken).ConfigureAwait(false);

            if (capability == CountryApiCapability.TemporarilyUnavailable
                && timeProvider.GetUtcNow() < GetNextCountryProbeUtc())
            {
                return await GetUsingAreaAsync(countryCode, source, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                ImmutableArray<Anomaly> detections = await GetCountryDetectionsAsync(
                    countryCode,
                    source,
                    cancellationToken).ConfigureAwait(false);
                MarkCountryAvailable(capability);
                return new(detections, IngestionModes.Country);
            }
            catch (CountryFeatureUnavailableException)
            {
                MarkCountryUnavailable();
                return await GetUsingAreaAsync(countryCode, source, cancellationToken).ConfigureAwait(false);
            }
            catch (FirmsRequestException exception)
                when (capability == CountryApiCapability.TemporarilyUnavailable)
            {
                ScheduleNextCountryProbe();
                LogCountryProbeFailure(logger, exception.SafeMessage);
                return await GetUsingAreaAsync(countryCode, source, cancellationToken).ConfigureAwait(false);
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
            ImmutableArray<Anomaly> detections = await GetCountryDetectionsAsync(countryCode, source, cancellationToken).ConfigureAwait(false);
            return new(detections, IngestionModes.Country);
        }
        catch (CountryFeatureUnavailableException)
        {
            MarkCountryUnavailable();
            return await GetUsingAreaAsync(countryCode, source, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<FirmsSegmentResult> GetUsingAreaAsync(
        string countryCode,
        string source,
        CancellationToken cancellationToken)
    {
        CountryBoundary boundary = boundaries.Get(countryCode);
        LogAreaFallbackRefresh(
            logger,
            countryCode,
            source,
            boundary.Tiles.Length);

        ImmutableArray<Anomaly>[] tileDetections;
        try
        {
            tileDetections = await Task.WhenAll(boundary.Tiles.Select(tile =>
                GetAreaDetectionsAsync(countryCode, source, tile, cancellationToken))).ConfigureAwait(false);
        }
        catch (FirmsRequestException exception)
        {
            throw new FirmsRequestException(
                safeMessage: $"FIRMS area fallback tile failed: {exception.SafeMessage}");
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
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                requestUri: $"api/country/csv/{Uri.EscapeDataString(options.MapKey)}/{source}/{countryCode}/1");
            using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode
                && await IsCountryFeatureUnavailableAsync(response, cancellationToken).ConfigureAwait(false))
            {
                throw new CountryFeatureUnavailableException();
            }

            return await ReadCsvResponseAsync(response, countryCode, source, cancellationToken).ConfigureAwait(false);
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
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                requestUri: $"api/area/csv/{Uri.EscapeDataString(options.MapKey)}/{source}/{bounds.ToInvariantString()}/1");
            using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            return await ReadCsvResponseAsync(response, countryCode, source, cancellationToken).ConfigureAwait(false);
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
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FirmsRequestException(safeMessage: "FIRMS request timed out.");
        }
        catch (HttpRequestException)
        {
            throw new FirmsRequestException(safeMessage: "FIRMS is unavailable.");
        }
        catch (Exception exception) when (exception.GetType().Name.Equals(value: "TimeoutRejectedException", StringComparison.Ordinal))
        {
            throw new FirmsRequestException(safeMessage: "FIRMS request timed out.");
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
            throw new FirmsRequestException(safeMessage: "FIRMS rejected the MAP_KEY.");

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new FirmsRequestException(safeMessage: "FIRMS rate limit was reached.");

        if (!response.IsSuccessStatusCode)
            throw new FirmsRequestException(safeMessage: "FIRMS returned an upstream error.");

        if (response.Content.Headers.ContentLength > MaximumResponseCharacters)
            throw new FirmsRequestException(safeMessage: "FIRMS response was too large.");

        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null
            && (mediaType.Contains(value: "html", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains(value: "xml", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains(value: "json", StringComparison.OrdinalIgnoreCase)))
        {
            throw new FirmsRequestException(safeMessage: "FIRMS returned an invalid dataset.");
        }

        string content = await ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        return ParseCsv(content, countryCode, source);
    }

    private async Task<bool> IsCountryFeatureUnavailableAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!CouldIndicateCountryFeatureOutage(response.StatusCode)
            || !HasReadableErrorBody(response.Content.Headers.ContentType?.MediaType))
        {
            return false;
        }

        string body = NormalizeErrorBody(await ReadPrefixAsync(
            response.Content,
            MaximumErrorBodyCharacters,
            cancellationToken).ConfigureAwait(false));
        if (IdentifiesRequestFailure(body))
            return false;

        if (IdentifiesCountryFeatureOutage(body))
            return true;

        bool isAmbiguousDisabledEndpointResponse = response.StatusCode == HttpStatusCode.BadRequest
            && ContainsAny(body, "invalid api", "api call is invalid", "invalid endpoint call");
        return isAmbiguousDisabledEndpointResponse
            && await IsMapKeyUsableAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool CouldIndicateCountryFeatureOutage(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.BadRequest
            or HttpStatusCode.NotFound
            or HttpStatusCode.Gone
            or HttpStatusCode.NotImplemented
        || (int)statusCode is >= 500 and <= 599;

    private static bool HasReadableErrorBody(string? mediaType) =>
        mediaType is null
        || mediaType.StartsWith(value: "text/", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains(value: "json", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains(value: "xml", StringComparison.OrdinalIgnoreCase);

    private static bool IdentifiesRequestFailure(string body) => ContainsAny(
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
        "invalid parameter");

    private static bool IdentifiesCountryFeatureOutage(string body) =>
        ContainsAny(
            body,
            "country api",
            "country endpoint",
            "country feature",
            "country service",
            "api country")
        && ContainsAny(
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

    private async Task<bool> IsMapKeyUsableAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            requestUri: $"mapserver/mapkey_status/?MAP_KEY={Uri.EscapeDataString(options.MapKey)}");
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return false;

        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null
            && !mediaType.Contains(value: "json", StringComparison.OrdinalIgnoreCase))
        {
            throw new FirmsRequestException(safeMessage: "FIRMS returned an invalid MAP_KEY status.");
        }

        try
        {
            string content = await ReadPrefixAsync(
                response.Content,
                MaximumErrorBodyCharacters,
                cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(content);
            return document.RootElement.TryGetProperty(propertyName: "transaction_limit", out JsonElement limitProperty)
                && limitProperty.TryGetInt32(out int limit)
                && document.RootElement.TryGetProperty(
                    propertyName: "current_transactions",
                    out JsonElement currentProperty)
                && currentProperty.TryGetInt32(out int current)
                && limit > 0
                && current < limit;
        }
        catch (JsonException)
        {
            throw new FirmsRequestException(safeMessage: "FIRMS returned an invalid MAP_KEY status.");
        }
    }

    private static string NormalizeErrorBody(string body)
    {
        var normalized = new StringBuilder(body.Length);
        bool previousWasSpace = true;

        foreach (char character in body)
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
        foreach (string candidate in candidates)
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

        if (previous == CountryApiCapability.TemporarilyUnavailable)
            LogCountryApiRestored(logger);
        else
            LogCountryApiAvailable(logger);
    }

    private void MarkCountryUnavailable()
    {
        bool changed = false;
        lock (_capabilitySync)
        {
            if (_countryCapability != CountryApiCapability.TemporarilyUnavailable)
                changed = true;

            _countryCapability = CountryApiCapability.TemporarilyUnavailable;
            _nextCountryProbeUtc = timeProvider.GetUtcNow() + s_countryProbeInterval;
        }

        if (changed)
            LogCountryApiUnavailable(logger);
    }

    private void ScheduleNextCountryProbe()
    {
        lock (_capabilitySync)
            _nextCountryProbeUtc = timeProvider.GetUtcNow() + s_countryProbeInterval;
    }

    public void Dispose()
    {
        _capabilityGate.Dispose();
        _requestGate.Dispose();
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Country API probe failed; continuing area fallback: {SafeError}")]
    private static partial void LogCountryProbeFailure(ILogger logger, string safeError);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Debug,
        Message = "Refreshing FIRMS segment {Country} {Source} with {TileCount} area fallback tiles")]
    private static partial void LogAreaFallbackRefresh(
        ILogger logger,
        string country,
        string source,
        int tileCount);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "Country API restored; disabling area fallback")]
    private static partial void LogCountryApiRestored(ILogger logger);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Information,
        Message = "Country API available")]
    private static partial void LogCountryApiAvailable(ILogger logger);

    [LoggerMessage(
        EventId = 5,
        Level = LogLevel.Warning,
        Message = "Country API unavailable; using area fallback")]
    private static partial void LogCountryApiUnavailable(ILogger logger);

    [LoggerMessage(
        EventId = 6,
        Level = LogLevel.Warning,
        Message = "Skipped malformed FIRMS row {RowNumber} for {Country} {Source}")]
    private static partial void LogMalformedRowSkipped(
        ILogger logger,
        int rowNumber,
        string country,
        string source);

    [LoggerMessage(
        EventId = 7,
        Level = LogLevel.Warning,
        Message = "Skipped {SkippedRowCount} malformed FIRMS rows for {Country} {Source}")]
    private static partial void LogMalformedRowsSkipped(
        ILogger logger,
        int skippedRowCount,
        string country,
        string source);

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
            throw new FirmsRequestException(safeMessage: "FIRMS returned an invalid dataset.");

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
                throw new FirmsRequestException(safeMessage: "FIRMS returned an invalid CSV structure.");

            ValidateHeaders(headers, source);
            return ParseRows(csv, countryCode, source);
        }
        catch (FirmsRequestException)
        {
            throw;
        }
        catch (Exception exception) when (exception is CsvHelperException or IOException)
        {
            throw new FirmsRequestException(safeMessage: "FIRMS returned an invalid CSV structure.");
        }
    }

    private ImmutableArray<Anomaly> ParseRows(CsvReader csv, string countryCode, string source)
    {
        ImmutableArray<Anomaly>.Builder detections = ImmutableArray.CreateBuilder<Anomaly>();
        int dataRowCount = 0;
        int skippedRowCount = 0;

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
                LogMalformedRowSkipped(logger, csv.Parser.Row, countryCode, source);
            }
        }

        if (dataRowCount > 0 && detections.Count == 0)
            throw new FirmsRequestException(safeMessage: "FIRMS returned no usable data rows.");

        if (skippedRowCount > 0)
            LogMalformedRowsSkipped(logger, skippedRowCount, countryCode, source);

        return [.. detections.DistinctBy(detection => detection.Id)];
    }

    private static Anomaly ParseRow(CsvReader csv, string countryCode, string source)
    {
        double latitude = ParseRequiredDouble(csv, name: "latitude");
        double longitude = ParseRequiredDouble(csv, name: "longitude");

        if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
            throw new FormatException(message: "Coordinates are outside their valid ranges.");

        string satellite = GetRequired(csv, name: "satellite");
        string instrument = GetRequired(csv, name: "instrument");
        string dayNight = GetRequired(csv, name: "daynight").ToUpperInvariant();

        if (dayNight is not ("D" or "N"))
            throw new FormatException(message: "daynight is invalid.");

        DateTimeOffset acquiredAtUtc = ParseAcquisitionTime(
            GetRequired(csv, name: "acq_date"),
            GetRequired(csv, name: "acq_time"));

        bool isModis = source.Equals(value: "MODIS_NRT", StringComparison.Ordinal);
        string? confidenceRaw = GetOptional(csv, name: "confidence");
        double? confidencePercent = isModis && confidenceRaw is not null
            ? ParseDouble(confidenceRaw)
            : null;
        string? confidenceCategory = isModis ? null : NormalizeConfidenceCategory(confidenceRaw);
        string googleMapsUrl = string.Create(
            CultureInfo.InvariantCulture,
            handler: $"https://www.google.com/maps/search/?api=1&query={latitude:0.######},{longitude:0.######}");

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
            ParseOptionalDouble(csv, name: "frp"),
            ParseOptionalDouble(csv, name: "scan"),
            ParseOptionalDouble(csv, name: "track"),
            confidenceRaw,
            confidencePercent,
            confidenceCategory,
            GetOptional(csv, name: "version"),
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
        string[] sourceSpecific = source.Equals(value: "MODIS_NRT", StringComparison.Ordinal)
            ? ["brightness", "bright_t31"]
            : ["bright_ti4", "bright_ti5"];

        if (common.Concat(sourceSpecific).Any(header => !available.Contains(header)))
            throw new FirmsRequestException(safeMessage: "FIRMS CSV is missing required headers.");
    }

    private static DateTimeOffset ParseAcquisitionTime(string date, string time)
    {
        string paddedTime = time.PadLeft(totalWidth: 4, '0');

        if (paddedTime.Length != 4
            || !DateTimeOffset.TryParseExact(
                date + paddedTime,
                format: "yyyy-MM-ddHHmm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset acquiredAtUtc))
        {
            throw new FormatException(message: "Acquisition timestamp is invalid.");
        }

        return acquiredAtUtc;
    }

    private static string GetRequired(CsvReader csv, string name) =>
        GetOptional(csv, name) is { } value
            ? value
            : throw new FormatException(message: $"{name} is required.");

    private static string? GetOptional(CsvReader csv, string name)
    {
        string? value = csv.GetField(name)?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static double ParseRequiredDouble(CsvReader csv, string name) =>
        ParseDouble(GetRequired(csv, name));

    private static double? ParseOptionalDouble(CsvReader csv, string name) =>
        GetOptional(csv, name) is { } value ? ParseDouble(value) : null;

    private static double ParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            && double.IsFinite(parsed)
                ? parsed
                : throw new FormatException(message: "Numeric FIRMS value is invalid.");

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
        Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
            var result = new StringBuilder();
            char[] buffer = new char[8192];

            while (true)
            {
                int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    return result.ToString();

                result.Append(buffer, startIndex: 0, read);
                if (result.Length > MaximumResponseCharacters)
                    throw new FirmsRequestException(safeMessage: "FIRMS response was too large.");
            }
        }
    }

    private static async Task<string> ReadPrefixAsync(
        HttpContent content,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
            var result = new StringBuilder(maximumCharacters);
            const int maximumBufferSize = 1024;
            char[] buffer = new char[Math.Min(maximumBufferSize, maximumCharacters)];

            while (result.Length < maximumCharacters)
            {
                int remaining = Math.Min(buffer.Length, maximumCharacters - result.Length);
                int read = await reader.ReadAsync(buffer.AsMemory(start: 0, remaining), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                result.Append(buffer, startIndex: 0, read);
            }

            return result.ToString();
        }
    }
}
