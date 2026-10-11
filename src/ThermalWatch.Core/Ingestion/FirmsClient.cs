using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

using CsvHelper;
using CsvHelper.Configuration;

using Microsoft.Extensions.Logging;

using NetTopologySuite.Geometries;

using Nito.AsyncEx;

namespace ThermalWatch.Core;

/// <summary>Acquires complete FIRMS segments with country-first ingestion and verified area fallback.</summary>
/// <param name="httpClient">The host-owned FIRMS HTTP client.</param>
/// <param name="options">The validated ingestion and request limits.</param>
/// <param name="boundaries">The configured country geometries for area fallback.</param>
/// <param name="timeProvider">The UTC clock for capability probes and elapsed-time diagnostics.</param>
/// <param name="logger">The credential-free ingestion logger.</param>
public sealed partial class FirmsClient(
    HttpClient httpClient,
    FirmsOptions options,
    CountryBoundaryCatalog boundaries,
    TimeProvider timeProvider,
    ILogger<FirmsClient> logger
)
{
    /// <summary>The maximum number of UTC dates supported by one FIRMS request.</summary>
    public const int MaximumDayRange = 5;

    private const int MaximumErrorBodyCharacters = 4096;
    private const int MaximumResponseCharacters = 50_000_000;
    private static readonly TimeSpan CountryProbeInterval = TimeSpan.FromHours(hours: 1);
    private readonly int _requestDayRange = checked((int)Math.Ceiling(options.ActiveWindow.TotalDays) + 1);
    private readonly AsyncSemaphore _capabilityGate = new(initialCount: 1);
    private readonly AsyncSemaphore _requestGate = new(options.MaximumConcurrency);
    private readonly AsyncLock _capabilitySync = new();
    private CountryApiCapability _countryCapability;
    private DateTimeOffset _nextCountryProbeUtc = DateTimeOffset.MinValue;

    private enum CountryApiCapability
    {
        Unknown,
        Available,
        TemporarilyUnavailable
    }

    /// <summary>Acquires the UTC-date range covering the configured active observation window.</summary>
    /// <param name="countryCode">The configured ingestion country.</param>
    /// <param name="source">The configured FIRMS dataset.</param>
    /// <param name="cancellationToken">Cancels acquisition and body reads.</param>
    public async Task<FirmsSegmentResult> GetSegmentAsync(string countryCode, string source, CancellationToken cancellationToken)
    {
        return await GetSegmentAsync(countryCode, source, _requestDayRange, startDate: null, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
    }

    /// <summary>Acquires a complete explicit range of UTC dates for history backfill.</summary>
    /// <param name="countryCode">The configured ingestion country.</param>
    /// <param name="source">The configured FIRMS dataset.</param>
    /// <param name="startDate">The first covered UTC date.</param>
    /// <param name="dayRange">The number of covered dates, from one through five.</param>
    /// <param name="cancellationToken">Cancels acquisition and body reads.</param>
    public async Task<FirmsSegmentResult> GetSegmentAsync(string countryCode, string source, DateOnly startDate, int dayRange, CancellationToken cancellationToken)
    {
        return await GetSegmentAsync(countryCode, source, dayRange, startDate, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
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

    private static bool CouldIndicateCountryFeatureOutage(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.BadRequest
            or HttpStatusCode.NotFound
            or HttpStatusCode.Gone
            or HttpStatusCode.NotImplemented
        || (int)statusCode is >= 500 and <= 599;
    }

    private static string FormatStartDate(DateOnly? startDate)
    {
        return startDate is { } date
            ? $"/{date.ToString(format: "yyyy-MM-dd", CultureInfo.InvariantCulture)}"
            : string.Empty;
    }

    private static string? GetOptional(CsvReader csvReader, string name)
    {
        string? value = csvReader.GetField(name)?.Trim();

        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string GetRequired(CsvReader csvReader, string name)
    {
        return GetOptional(csvReader, name) is { } value
            ? value
            : throw new FormatException($"{name} is required.");
    }

    private static bool HasReadableErrorBody(string? mediaType)
    {
        return mediaType is null
        || mediaType.StartsWith(value: "text/", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains(value: "json", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains(value: "xml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IdentifiesCountryFeatureOutage(string body)
    {
        return ContainsAny(body, candidates: ["country api", "country endpoint", "country feature", "country service", "api country"])
        && ContainsAny(
            body,
            candidates: ["unavailable", "not available", "disabled", "unsupported", "not supported", "not implemented", "not found", "no longer available", "retired", "decommissioned", "gone"]
        );
    }

    private static bool IdentifiesRequestFailure(string body)
    {
        return ContainsAny(
        body,
        candidates: ["map key", "mapkey", "api key", "credential", "authentication", "unauthorized", "forbidden", "invalid request", "malformed request", "invalid country", "country code", "invalid parameter"]
    );
    }

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Debug,
        Message = "Refreshing FIRMS segment {CountryCode} {Source} with area fallback bounds {West},{South},{East},{North}"
    )]
    private static partial void LogAreaFallbackRefresh(ILogger logger, string countryCode, string source, double west, double south, double east, double north);

    [LoggerMessage(
        EventId = 9,
        Level = LogLevel.Warning,
        Message = "FIRMS area fallback request failed for {CountryCode} {Source} within {West},{South},{East},{North} after {Elapsed}: {SafeError}"
    )]
    private static partial void LogAreaRequestFailed(
        ILogger logger,
        string countryCode,
        string source,
        double west,
        double south,
        double east,
        double north,
        TimeSpan elapsed,
        string safeError
    );

    [LoggerMessage(
        EventId = 8,
        Level = LogLevel.Debug,
        Message = "Refreshed FIRMS area fallback request for {CountryCode} {Source} within {West},{South},{East},{North} in {Elapsed}"
    )]
    private static partial void LogAreaRequestRefreshed(ILogger logger, string countryCode, string source, double west, double south, double east, double north, TimeSpan elapsed);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Country API available")]
    private static partial void LogCountryApiAvailable(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Country API restored; disabling area fallback")]
    private static partial void LogCountryApiRestored(ILogger logger);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Country API unavailable; using area fallback")]
    private static partial void LogCountryApiUnavailable(ILogger logger);

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Country API probe failed; continuing area fallback: {SafeError}")]
    private static partial void LogCountryProbeFailure(ILogger logger, string safeError);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Skipped malformed FIRMS row {RowNumber} for {CountryCode} {Source}")]
    private static partial void LogMalformedRowSkipped(ILogger logger, int rowNumber, string countryCode, string source);

    [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = "Skipped {SkippedRowCount} malformed FIRMS rows for {CountryCode} {Source}")]
    private static partial void LogMalformedRowsSkipped(ILogger logger, int skippedRowCount, string countryCode, string source);

    private static string? NormalizeConfidenceCategory(string? value)
    {
        return value?.ToUpperInvariant() switch
        {
            "L" or "LOW" => "low",
            "N" or "NOMINAL" => "nominal",
            "H" or "HIGH" => "high",
            null => null,
            _ => value
        };
    }

    private static string NormalizeErrorBody(string body)
    {
        var normalized = new StringBuilder(body.Length);
        bool previousWasSpace = true;

        foreach (char character in body)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                normalized = normalized.Append(char.ToLowerInvariant(character));
                previousWasSpace = false;
            }
            else if (!previousWasSpace)
            {
                normalized = normalized.Append(value: ' ');
                previousWasSpace = true;
            }
        }

        return normalized.ToString();
    }

    private static DateTimeOffset ParseAcquisitionTime(string date, string time)
    {
        string paddedTime = time.PadLeft(totalWidth: 4, paddingChar: '0');

        if (paddedTime.Length != 4)
            throw new FormatException(message: "Acquisition timestamp is invalid.");

        bool hasValidTimestamp = DateTimeOffset.TryParseExact(
                date + paddedTime,
                format: "yyyy-MM-ddHHmm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset acquiredAtUtc
            );

        return hasValidTimestamp ? acquiredAtUtc : throw new FormatException(message: "Acquisition timestamp is invalid.");
    }

    private static double ParseDouble(string value)
    {
        return !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? throw new FormatException(message: "Numeric FIRMS value is invalid.")
            : double.IsFinite(parsed) ? parsed : throw new FormatException(message: "Numeric FIRMS value is invalid.");
    }

    private static double? ParseOptionalDouble(CsvReader csvReader, string name)
    {
        return GetOptional(csvReader, name) is { } value ? ParseDouble(value) : null;
    }

    private static double ParseRequiredDouble(CsvReader csvReader, string name)
    {
        return ParseDouble(GetRequired(csvReader, name));
    }

    private static Anomaly ParseRow(CsvReader csvReader, string countryCode, string source)
    {
        double latitude = ParseRequiredDouble(csvReader, name: "latitude");
        double longitude = ParseRequiredDouble(csvReader, name: "longitude");

        if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
            throw new FormatException(message: "Coordinates are outside their valid ranges.");

        string satellite = GetRequired(csvReader, name: "satellite");
        string instrument = GetRequired(csvReader, name: "instrument");
        string dayNight = GetRequired(csvReader, name: "daynight").ToUpperInvariant();

        if (dayNight is not ("D" or "N"))
            throw new FormatException(message: "daynight is invalid.");

        DateTimeOffset acquiredAtUtc = ParseAcquisitionTime(GetRequired(csvReader, name: "acq_date"), GetRequired(csvReader, name: "acq_time"));

        bool isModis = source.Equals(value: "MODIS_NRT", StringComparison.Ordinal);
        string? confidenceRaw = GetOptional(csvReader, name: "confidence");

        double? confidencePercent = isModis && confidenceRaw is not null
            ? ParseDouble(confidenceRaw)
            : null;

        string? confidenceCategory = isModis ? null : NormalizeConfidenceCategory(confidenceRaw);

        string googleMapsUrl = string.Create(CultureInfo.InvariantCulture, $"https://www.google.com/maps/search/?api=1&query={latitude:0.######},{longitude:0.######}");

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
            ParseOptionalDouble(csvReader, isModis ? "brightness" : "bright_ti4"),
            ParseOptionalDouble(csvReader, isModis ? "bright_t31" : "bright_ti5"),
            ParseOptionalDouble(csvReader, name: "frp"),
            ParseOptionalDouble(csvReader, name: "scan"),
            ParseOptionalDouble(csvReader, name: "track"),
            confidenceRaw,
            confidencePercent,
            confidenceCategory,
            GetOptional(csvReader, name: "version"),
            new Uri(googleMapsUrl)
        );
    }

    private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        await using (stream.ConfigureAwait(continueOnCapturedContext: false))
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);

            var result = new StringBuilder();
            char[] buffer = new char[8192];

            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            while (read > 0)
            {
                result = result.Append(buffer, startIndex: 0, read);

                if (result.Length > MaximumResponseCharacters)
                    throw new FirmsRequestException(safeMessage: "FIRMS response was too large.");

                read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            }

            return result.ToString();
        }
    }

    private static async Task<string> ReadPrefixAsync(HttpContent content, int maximumCharacters, CancellationToken cancellationToken)
    {
        Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        await using (stream.ConfigureAwait(continueOnCapturedContext: false))
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);

            var result = new StringBuilder(maximumCharacters);
            const int maximumBufferSize = 1024;
            char[] buffer = new char[Math.Min(maximumBufferSize, maximumCharacters)];

            while (result.Length < maximumCharacters)
            {
                int remaining = Math.Min(buffer.Length, maximumCharacters - result.Length);
                int read = await reader.ReadAsync(buffer.AsMemory(start: 0, remaining), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

                if (read == 0)
                    break;

                result = result.Append(buffer, startIndex: 0, read);
            }

            return result.ToString();
        }
    }

    private static void ValidateHeaders(IEnumerable<string> headers, string source)
    {
        var available = headers
            .Select(header => header.Trim().TrimStart(trimChar: '\uFEFF'))
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

    private FirmsRequestException CreateAreaFallbackFailure(string countryCode, string source, GeographicBounds bounds, long startedTimestamp, string safeError)
    {
        LogAreaRequestFailed(
            logger,
            countryCode,
            source,
            bounds.West,
            bounds.South,
            bounds.East,
            bounds.North,
            timeProvider.GetElapsedTime(startedTimestamp),
            safeError
        );

        return new($"FIRMS area fallback request failed: {safeError}");
    }

    private async Task<TTResult> ExecuteRequestAsync<TTResult>(Func<CancellationToken, Task<TTResult>> operation, CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        try
        {
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            requestTimeout.CancelAfter(options.RequestTimeout);

            try
            {
                return await operation(requestTimeout.Token).ConfigureAwait(continueOnCapturedContext: false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && requestTimeout.IsCancellationRequested)
            {
                throw new FirmsRequestException(safeMessage: "FIRMS request timed out.");
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task<ImmutableArray<Anomaly>> GetAreaAnomaliesAsync(
        string countryCode,
        string source,
        GeographicBounds bounds,
        int dayRange,
        DateOnly? startDate,
        CancellationToken cancellationToken
    )
    {
        return await ExecuteRequestAsync(
            async requestToken =>
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"api/area/csv/{Uri.EscapeDataString(options.MapKey)}/{source}/{bounds.ToInvariantString()}/{dayRange}{FormatStartDate(startDate)}"
                );

                using HttpResponseMessage response = await SendAsync(request, requestToken).ConfigureAwait(continueOnCapturedContext: false);

                return await ReadCsvResponseAsync(response, countryCode, source, requestToken).ConfigureAwait(continueOnCapturedContext: false);
            },
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
    }

    private async Task<ImmutableArray<Anomaly>> GetAreaEnvelopeAnomaliesAsync(
        string countryCode,
        string source,
        GeographicBounds bounds,
        int dayRange,
        DateOnly? startDate,
        long startedTimestamp,
        CancellationToken cancellationToken
    )
    {
        try
        {
            ImmutableArray<Anomaly> anomalies = await GetAreaAnomaliesAsync(countryCode, source, bounds, dayRange, startDate, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            LogAreaRequestSucceeded(countryCode, source, bounds, startedTimestamp);

            return anomalies;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FirmsRequestException exception)
        {
            throw CreateAreaFallbackFailure(countryCode, source, bounds, startedTimestamp, exception.SafeMessage);
        }
        catch (Exception)
        {
            const string safeError = "Unexpected FIRMS client failure.";

            throw CreateAreaFallbackFailure(countryCode, source, bounds, startedTimestamp, safeError);
        }
    }

    private async Task<ImmutableArray<Anomaly>> GetCountryAnomaliesAsync(string countryCode, string source, int dayRange, DateOnly? startDate, CancellationToken cancellationToken)
    {
        return await ExecuteRequestAsync(
            async requestToken =>
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"api/country/csv/{Uri.EscapeDataString(options.MapKey)}/{source}/{countryCode}/{dayRange}{FormatStartDate(startDate)}"
                );

                using HttpResponseMessage response = await SendAsync(request, requestToken).ConfigureAwait(continueOnCapturedContext: false);

                if (!response.IsSuccessStatusCode)
                {
                    bool countryFeatureUnavailable = await IsCountryFeatureUnavailableAsync(response, requestToken)
                        .ConfigureAwait(continueOnCapturedContext: false);

                    if (countryFeatureUnavailable)
                        throw new CountryFeatureUnavailableException();
                }

                return await ReadCsvResponseAsync(response, countryCode, source, requestToken)
                    .ConfigureAwait(continueOnCapturedContext: false);
            },
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
    }

    private CountryApiCapability GetCountryCapability()
    {
        using (_capabilitySync.Lock())
            return _countryCapability;
    }

    private DateTimeOffset GetNextCountryProbeUtc()
    {
        using (_capabilitySync.Lock())
            return _nextCountryProbeUtc;
    }

    private async Task<FirmsSegmentResult> GetSegmentAsync(string countryCode, string source, int dayRange, DateOnly? startDate, CancellationToken cancellationToken)
    {
        bool invalidRequest = !options.CountryCodes.Contains(countryCode, StringComparer.Ordinal)
            || !FirmsSources.All.Contains(source, StringComparer.Ordinal)
            || dayRange is < 1 or > MaximumDayRange;

        if (invalidRequest)
            throw new FirmsRequestException(safeMessage: "FIRMS request parameters are invalid.");

        CountryApiCapability capability = GetCountryCapability();

        if (capability == CountryApiCapability.Available)
            return await GetUsingCountryAsync(countryCode, source, dayRange, startDate, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        if (capability == CountryApiCapability.TemporarilyUnavailable && timeProvider.GetUtcNow() < GetNextCountryProbeUtc())
            return await GetUsingAreaAsync(countryCode, source, dayRange, startDate, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        await _capabilityGate.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        try
        {
            capability = GetCountryCapability();

            if (capability == CountryApiCapability.Available)
                return await GetUsingCountryAsync(countryCode, source, dayRange, startDate, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            if (capability == CountryApiCapability.TemporarilyUnavailable && timeProvider.GetUtcNow() < GetNextCountryProbeUtc())
                return await GetUsingAreaAsync(countryCode, source, dayRange, startDate, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            try
            {
                ImmutableArray<Anomaly> anomalies = await GetCountryAnomaliesAsync(countryCode, source, dayRange, startDate, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

                MarkCountryAvailable(capability);

                return new(anomalies, IngestionModes.Country);
            }
            catch (CountryFeatureUnavailableException)
            {
                MarkCountryUnavailable();

                return await GetUsingAreaAsync(countryCode, source, dayRange, startDate, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            }
            catch (FirmsRequestException exception)
                when (capability == CountryApiCapability.TemporarilyUnavailable)
            {
                ScheduleNextCountryProbe();
                LogCountryProbeFailure(logger, exception.SafeMessage);

                return await GetUsingAreaAsync(countryCode, source, dayRange, startDate, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            }
        }
        finally
        {
            _capabilityGate.Release();
        }
    }

    private async Task<FirmsSegmentResult> GetUsingAreaAsync(string countryCode, string source, int dayRange, DateOnly? startDate, CancellationToken cancellationToken)
    {
        CountryBoundary boundary = boundaries.Get(countryCode);
        GeographicBounds bounds = boundary.AreaBounds;
        LogAreaFallbackRefresh(logger, countryCode, source, bounds.West, bounds.South, bounds.East, bounds.North);

        long startedTimestamp = timeProvider.GetTimestamp();

        ImmutableArray<Anomaly> areaAnomalies = await GetAreaEnvelopeAnomaliesAsync(countryCode, source, bounds, dayRange, startDate, startedTimestamp, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        var anomalies = areaAnomalies
            .Where(anomaly => boundary.Prepared.Covers(boundary.Geometry.Factory.CreatePoint(new Coordinate(anomaly.Longitude, anomaly.Latitude))))
            .DistinctBy(anomaly => anomaly.Id, StringComparer.Ordinal)
            .ToImmutableArray();

        return new(anomalies, IngestionModes.AreaFallback);
    }

    private async Task<FirmsSegmentResult> GetUsingCountryAsync(string countryCode, string source, int dayRange, DateOnly? startDate, CancellationToken cancellationToken)
    {
        try
        {
            ImmutableArray<Anomaly> anomalies = await GetCountryAnomaliesAsync(countryCode, source, dayRange, startDate, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            return new(anomalies, IngestionModes.Country);
        }
        catch (CountryFeatureUnavailableException)
        {
            MarkCountryUnavailable();

            return await GetUsingAreaAsync(countryCode, source, dayRange, startDate, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }
    }

    private async Task<bool> IsCountryFeatureUnavailableAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!CouldIndicateCountryFeatureOutage(response.StatusCode))
            return false;

        if (!HasReadableErrorBody(response.Content.Headers.ContentType?.MediaType))
            return false;

        string body = NormalizeErrorBody(
            await ReadPrefixAsync(response.Content, MaximumErrorBodyCharacters, cancellationToken).ConfigureAwait(continueOnCapturedContext: false)
        );

        if (IdentifiesRequestFailure(body))
            return false;

        if (IdentifiesCountryFeatureOutage(body))
            return true;

        bool isAmbiguousDisabledEndpointResponse = response.StatusCode == HttpStatusCode.BadRequest
            && ContainsAny(body, candidates: ["invalid api", "api call is invalid", "invalid endpoint call"]);

        return isAmbiguousDisabledEndpointResponse
            && await IsMapKeyUsableAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
    }

    private async Task<bool> IsMapKeyUsableAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"mapserver/mapkey_status/?MAP_KEY={Uri.EscapeDataString(options.MapKey)}");

        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        if (!response.IsSuccessStatusCode)
            return false;

        string? mediaType = response.Content.Headers.ContentType?.MediaType;

        if (mediaType is not null && !mediaType.Contains(value: "json", StringComparison.OrdinalIgnoreCase))
            throw new FirmsRequestException(safeMessage: "FIRMS returned an invalid MAP_KEY status.");

        try
        {
            string content = await ReadPrefixAsync(response.Content, MaximumErrorBodyCharacters, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            using var document = JsonDocument.Parse(content);

            return document.RootElement.TryGetProperty(propertyName: "transaction_limit", out JsonElement limitProperty)
                && limitProperty.TryGetInt32(out int limit)
                && document.RootElement.TryGetProperty(propertyName: "current_transactions", out JsonElement currentProperty)
                && currentProperty.TryGetInt32(out int current)
                && limit > 0
                && current < limit;
        }
        catch (JsonException)
        {
            throw new FirmsRequestException(safeMessage: "FIRMS returned an invalid MAP_KEY status.");
        }
    }

    private void LogAreaRequestSucceeded(string countryCode, string source, GeographicBounds bounds, long startedTimestamp)
    {
        if (!logger.IsEnabled(LogLevel.Debug))
            return;

        TimeSpan elapsed = timeProvider.GetElapsedTime(startedTimestamp);
        LogAreaRequestRefreshed(logger, countryCode, source, bounds.West, bounds.South, bounds.East, bounds.North, elapsed);
    }

    private void MarkCountryAvailable(CountryApiCapability previous)
    {
        using (_capabilitySync.Lock())
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

        using (_capabilitySync.Lock())
        {
            if (_countryCapability != CountryApiCapability.TemporarilyUnavailable)
                changed = true;

            _countryCapability = CountryApiCapability.TemporarilyUnavailable;
            _nextCountryProbeUtc = timeProvider.GetUtcNow() + CountryProbeInterval;
        }

        if (changed)
            LogCountryApiUnavailable(logger);
    }

    private ImmutableArray<Anomaly> ParseCsv(string content, string countryCode, string source)
    {
        if (string.IsNullOrWhiteSpace(content) || content.AsSpan().TrimStart().StartsWith(value: '<'))
            throw new FirmsRequestException(safeMessage: "FIRMS returned an invalid dataset.");

        var configuration = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            BadDataFound = null,
            HeaderValidated = null,
            MissingFieldFound = null,
            PrepareHeaderForMatch = static arguments => arguments.Header.Trim().TrimStart(trimChar: '\uFEFF'),
            TrimOptions = TrimOptions.Trim
        };

        try
        {
            using var reader = new StringReader(content);

            using var csvReader = new CsvReader(reader, configuration);

            if (!csvReader.Read() || !csvReader.ReadHeader() || csvReader.HeaderRecord is not { Length: > 0 } headers)
                throw new FirmsRequestException(safeMessage: "FIRMS returned an invalid CSV structure.");

            ValidateHeaders(headers, source);

            return ParseRows(csvReader, countryCode, source);
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

    private ImmutableArray<Anomaly> ParseRows(CsvReader csvReader, string countryCode, string source)
    {
        ImmutableArray<Anomaly>.Builder anomalies = ImmutableArray.CreateBuilder<Anomaly>();
        int dataRowCount = 0;
        int skippedRowCount = 0;

        while (csvReader.Read())
        {
            dataRowCount++;

            try
            {
                anomalies.Add(ParseRow(csvReader, countryCode, source));
            }
            catch (Exception exception) when (exception is FormatException or CsvHelperException)
            {
                skippedRowCount++;
                LogMalformedRowSkipped(logger, csvReader.Parser.Row, countryCode, source);
            }
        }

        if (dataRowCount > 0 && anomalies.Count == 0)
            throw new FirmsRequestException(safeMessage: "FIRMS returned no usable data rows.");

        if (skippedRowCount > 0)
            LogMalformedRowsSkipped(logger, skippedRowCount, countryCode, source);

        return [.. anomalies.DistinctBy(anomaly => anomaly.Id, StringComparer.Ordinal)];
    }

    private async Task<ImmutableArray<Anomaly>> ReadCsvResponseAsync(HttpResponseMessage response, string countryCode, string source, CancellationToken cancellationToken)
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

        bool hasInvalidMediaType = mediaType is not null
            && (mediaType.Contains(value: "html", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains(value: "xml", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains(value: "json", StringComparison.OrdinalIgnoreCase));

        if (hasInvalidMediaType)
            throw new FirmsRequestException(safeMessage: "FIRMS returned an invalid dataset.");

        string content = await ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        return ParseCsv(content, countryCode, source);
    }

    private void ScheduleNextCountryProbe()
    {
        using (_capabilitySync.Lock())
            _nextCountryProbeUtc = timeProvider.GetUtcNow() + CountryProbeInterval;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;

        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
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
}
