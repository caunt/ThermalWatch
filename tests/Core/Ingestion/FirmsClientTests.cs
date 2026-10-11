using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using ThermalWatch.Core;

namespace ThermalWatch.Tests.Core.Ingestion;

/// <summary>Verifies FIRMS acquisition, fallback, timeouts, and cancellation.</summary>
public sealed class FirmsClientTests
{
    private const string CrimeaCsv = """
        latitude,longitude,brightness,scan,track,acq_date,acq_time,satellite,instrument,confidence,version,bright_t31,frp,daynight
        44.952,34.1,330,1,1,2026-07-23,0731,T,MODIS,80,6.1NRT,300,100,D
        """;

    private const string FallbackCsv = """
        latitude,longitude,brightness,scan,track,acq_date,acq_time,satellite,instrument,confidence,version,bright_t31,frp,daynight
        49,30,330,1,1,2026-07-23,0731,T,MODIS,80,6.1NRT,300,100,D
        49,30,330,1,1,2026-07-23,0731,T,MODIS,80,6.1NRT,300,100,D
        0,0,330,1,1,2026-07-23,0731,T,MODIS,80,6.1NRT,300,100,D
        """;
    private const string ModisCsv = """
        latitude,longitude,brightness,scan,track,acq_date,acq_time,satellite,instrument,confidence,version,bright_t31,frp,daynight
        49,30,330,1,1,2026-07-23,0731,T,MODIS,80,6.1NRT,300,100,D
        """;

    private const string RussiaCsv = """
        latitude,longitude,brightness,scan,track,acq_date,acq_time,satellite,instrument,confidence,version,bright_t31,frp,daynight
        60,100,330,1,1,2026-07-23,0731,T,MODIS,80,6.1NRT,300,100,D
        """;

    private const string UkraineWorldviewFallbackCsv = """
        latitude,longitude,brightness,scan,track,acq_date,acq_time,satellite,instrument,confidence,version,bright_t31,frp,daynight
        44.952,34.1,330,1,1,2026-07-23,0731,T,MODIS,80,6.1NRT,300,100,D
        55.755,37.617,330,1,1,2026-07-23,0731,T,MODIS,80,6.1NRT,300,100,D
        """;

    /// <summary>Pairs active windows with the required inclusive day ranges.</summary>
    public static TheoryData<int, int> ActiveWindowCases => new()
    {
        { 1, 2 },
        { 24, 2 },
        { 25, 3 },
        { 48, 3 },
        { 72, 4 }
    };

    /// <summary>Pairs fallback countries with the expected retained latitude.</summary>
    public static TheoryData<string, double> WorldviewCases => new()
    {
        { "UKR", 44.952 },
        { "RUS", 55.755 }
    };

    /// <summary>Includes the UTC day overlap needed for the configured active window.</summary>
    [Theory]
    [MemberData(nameof(ActiveWindowCases))]
    public async Task GetSegmentAsyncDerivesCountryRequestDayRangeFromActiveWindow(int activeWindowHours, int expectedDayRange)
    {
        using var handler = new RecordingHandler(
            (request, cancellationToken) =>
        {
            Assert.EndsWith($"/{expectedDayRange}", Assert.IsType<Uri>(request.RequestUri).AbsolutePath, StringComparison.Ordinal);

            return Task.FromResult(CsvResponse());
        }
        );

        using var httpClient = new HttpClient(handler);

        FirmsClient client = CreateClient(httpClient, countryCode: "UKR", TimeSpan.FromHours(activeWindowHours));

        FirmsSegmentResult result = await client.GetSegmentAsync(countryCode: "UKR", source: "MODIS_NRT", TestContext.Current.CancellationToken);

        Assert.Equal(IngestionModes.Country, result.IngestionMode);
        Assert.True(result.Anomalies.Length == 1);
    }

    /// <summary>Reports a failed fallback envelope without publishing partial observations.</summary>
    [Fact]
    public async Task GetSegmentAsyncFailsWhenFallbackEnvelopeRequestFails()
    {
        using var handler = new RecordingHandler(
            (request, cancellationToken) =>
        {
            string path = Assert.IsType<Uri>(request.RequestUri).AbsolutePath;

            return Task.FromResult(
                IsCountryRequest(path)
                ? CountryUnavailableResponse()
                : IsMapKeyStatusRequest(path)
                    ? MapKeyStatusResponse()
                    : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            );
        }
        );

        using var httpClient = new HttpClient(handler);

        FirmsClient client = CreateClient(httpClient, countryCode: "UKR");

        FirmsRequestException exception = await Assert.ThrowsAsync<FirmsRequestException>(() => client.GetSegmentAsync(countryCode: "UKR", source: "MODIS_NRT", TestContext.Current.CancellationToken));

        Assert.Equal(expected: "FIRMS area fallback request failed: FIRMS returned an upstream error.", exception.SafeMessage);
        Assert.Equal(expected: 1, handler.AreaRequestCount);
    }

    /// <summary>Preserves the provider's country assignment during country ingestion.</summary>
    [Fact]
    public async Task GetSegmentAsyncKeepsProviderCountryInCountryMode()
    {
        using var handler = new RecordingHandler(static (request, cancellationToken) => Task.FromResult(CsvResponse(CrimeaCsv)));

        using var httpClient = new HttpClient(handler);

        FirmsClient client = CreateClient(httpClient, countryCode: "RUS");

        FirmsSegmentResult result = await client.GetSegmentAsync(countryCode: "RUS", source: "MODIS_NRT", TestContext.Current.CancellationToken);

        Assert.True(result.Anomalies.Length == 1);
        Anomaly anomaly = result.Anomalies[index: 0];
        Assert.Equal(IngestionModes.Country, result.IngestionMode);
        Assert.Equal(expected: "RUS", anomaly.CountryCode);
        Assert.Equal(expected: 44.952, anomaly.Latitude);
    }

    /// <summary>Preserves historical dates when switching to verified area fallback.</summary>
    [Fact]
    public async Task GetSegmentAsyncPreservesExplicitDateInAreaFallback()
    {
        using var handler = new RecordingHandler(
            (request, cancellationToken) =>
        {
            string path = Assert.IsType<Uri>(request.RequestUri).AbsolutePath;

            if (IsCountryRequest(path))
                return Task.FromResult(CountryUnavailableResponse());

            if (IsMapKeyStatusRequest(path))
                return Task.FromResult(MapKeyStatusResponse());

            Assert.EndsWith(expectedEndString: "/5/2026-06-01", path, StringComparison.Ordinal);

            return Task.FromResult(CsvResponse(FallbackCsv));
        }
        );

        using var httpClient = new HttpClient(handler);

        FirmsClient client = CreateClient(httpClient, countryCode: "UKR");

        FirmsSegmentResult result = await client.GetSegmentAsync(countryCode: "UKR", source: "MODIS_NRT", new(year: 2026, month: 6, day: 1), dayRange: 5, TestContext.Current.CancellationToken);

        Assert.Equal(IngestionModes.AreaFallback, result.IngestionMode);
        Assert.Equal(expected: 1, handler.AreaRequestCount);
    }

    /// <summary>Propagates caller cancellation while awaiting response bytes.</summary>
    [Fact]
    public async Task GetSegmentAsyncPropagatesCallerCancellationDuringContentRead()
    {
        using var stream = new BlockingReadStream();

        using var handler = new RecordingHandler((request, cancellationToken) => Task.FromResult(BlockingCsvResponse(stream)));

        using var httpClient = new HttpClient(handler);

        FirmsClient client = CreateClient(httpClient, countryCode: "UKR", requestTimeout: TimeSpan.FromSeconds(seconds: 5), maximumConcurrency: 1);

        using var cancellation = new CancellationTokenSource();

        Task<FirmsSegmentResult> loading = client.GetSegmentAsync(countryCode: "UKR", source: "MODIS_NRT", cancellation.Token);

        await stream.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        Assert.True(exception.CancellationToken.IsCancellationRequested);
    }

    /// <summary>Rejects unsupported historical ranges without making a request.</summary>
    [Fact]
    public async Task GetSegmentAsyncRejectsHistoricalRangeLongerThanFiveDays()
    {
        using var handler = new RecordingHandler(static (request, cancellationToken) => Task.FromResult(CsvResponse()));

        using var httpClient = new HttpClient(handler);

        FirmsClient client = CreateClient(httpClient, countryCode: "UKR");

        FirmsRequestException exception = await Assert.ThrowsAsync<FirmsRequestException>(
            () =>
            client.GetSegmentAsync(countryCode: "UKR", source: "MODIS_NRT", new(year: 2026, month: 6, day: 1), dayRange: 6, TestContext.Current.CancellationToken)
        );

        Assert.Equal(expected: "FIRMS request parameters are invalid.", exception.SafeMessage);
        Assert.Equal(expected: 0, handler.RequestCount);
    }

    /// <summary>Releases request concurrency after a stalled response times out.</summary>
    [Fact]
    public async Task GetSegmentAsyncTimesOutBlockedContentAndReleasesRequestGate()
    {
        int countryRequestCount = 0;

        using var handler = new RecordingHandler(
            (request, cancellationToken) =>
        {
            Assert.True(IsCountryRequest(Assert.IsType<Uri>(request.RequestUri).AbsolutePath));

            return Task.FromResult(Interlocked.Increment(ref countryRequestCount) == 1 ? BlockingCsvResponse(new BlockingReadStream()) : CsvResponse());
        }
        );

        using var httpClient = new HttpClient(handler);

        FirmsClient client = CreateClient(httpClient, countryCode: "UKR", requestTimeout: TimeSpan.FromMilliseconds(milliseconds: 100), maximumConcurrency: 1);

        FirmsRequestException exception = await Assert.ThrowsAsync<FirmsRequestException>(
            () => client.GetSegmentAsync(countryCode: "UKR", source: "MODIS_NRT", TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(seconds: 2), TestContext.Current.CancellationToken)
        );

        Assert.Equal(expected: "FIRMS request timed out.", exception.SafeMessage);

        FirmsSegmentResult recovered = await client.GetSegmentAsync(countryCode: "UKR", source: "MODIS_NRT", TestContext.Current.CancellationToken).ConfigureAwait(continueOnCapturedContext: true);

        Assert.Equal(IngestionModes.Country, recovered.IngestionMode);
        Assert.True(recovered.Anomalies.Length == 1);
        Assert.Equal(expected: 2, handler.RequestCount);
    }

    /// <summary>Releases fallback concurrency after a stalled envelope response times out.</summary>
    [Fact]
    public async Task GetSegmentAsyncTimesOutBlockedFallbackContentAndReleasesRequestGate()
    {
        int areaRequestCount = 0;

        using var handler = new RecordingHandler(
            (request, cancellationToken) =>
        {
            string path = Assert.IsType<Uri>(request.RequestUri).AbsolutePath;

            return IsCountryRequest(path)
                ? Task.FromResult(CountryUnavailableResponse())
                : IsMapKeyStatusRequest(path)
                ? Task.FromResult(MapKeyStatusResponse())
                : Task.FromResult(Interlocked.Increment(ref areaRequestCount) == 1 ? BlockingCsvResponse(new BlockingReadStream()) : CsvResponse());
        }
        );

        using var httpClient = new HttpClient(handler);

        FirmsClient client = CreateClient(httpClient, countryCode: "UKR", requestTimeout: TimeSpan.FromMilliseconds(milliseconds: 100), maximumConcurrency: 1);

        FirmsRequestException exception = await Assert.ThrowsAsync<FirmsRequestException>(
            () => client.GetSegmentAsync(countryCode: "UKR", source: "MODIS_NRT", TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(seconds: 2), TestContext.Current.CancellationToken)
        );

        Assert.Equal(expected: "FIRMS area fallback request failed: FIRMS request timed out.", exception.SafeMessage);

        FirmsSegmentResult recovered = await client.GetSegmentAsync(countryCode: "UKR", source: "MODIS_NRT", TestContext.Current.CancellationToken).ConfigureAwait(continueOnCapturedContext: true);

        Assert.Equal(IngestionModes.AreaFallback, recovered.IngestionMode);
        Assert.True(recovered.Anomalies.Length == 1);
        Assert.Equal(expected: 2, handler.AreaRequestCount);
        Assert.Equal(expected: 1, handler.MaximumConcurrency);
    }

    /// <summary>Includes the requested start date in historical country acquisition.</summary>
    [Fact]
    public async Task GetSegmentAsyncUsesExplicitDateForHistoricalCountryRequest()
    {
        using var handler = new RecordingHandler(
            (request, cancellationToken) =>
        {
            Assert.EndsWith(expectedEndString: "/5/2026-06-01", Assert.IsType<Uri>(request.RequestUri).AbsolutePath, StringComparison.Ordinal);

            return Task.FromResult(CsvResponse());
        }
        );

        using var httpClient = new HttpClient(handler);

        FirmsClient client = CreateClient(httpClient, countryCode: "UKR");

        FirmsSegmentResult result = await client.GetSegmentAsync(countryCode: "UKR", source: "MODIS_NRT", new(year: 2026, month: 6, day: 1), dayRange: 5, TestContext.Current.CancellationToken);

        Assert.Equal(IngestionModes.Country, result.IngestionMode);
        Assert.Equal(expected: 1, handler.RequestCount);
    }

    /// <summary>Uses country ingestion without probing fallback when the provider succeeds.</summary>
    [Fact]
    public async Task GetSegmentAsyncUsesOneCountryRequestWhenCountryApiIsAvailable()
    {
        using var handler = new RecordingHandler(
            static (request, cancellationToken) =>
        {
            Assert.EndsWith(expectedEndString: "/2", Assert.IsType<Uri>(request.RequestUri).AbsolutePath, StringComparison.Ordinal);

            return Task.FromResult(CsvResponse());
        }
        );

        using var httpClient = new HttpClient(handler);

        FirmsClient client = CreateClient(httpClient, countryCode: "UKR");

        FirmsSegmentResult result = await client.GetSegmentAsync(countryCode: "UKR", source: "MODIS_NRT", TestContext.Current.CancellationToken);

        Assert.Equal(IngestionModes.Country, result.IngestionMode);
        Assert.True(result.Anomalies.Length == 1);
        Assert.Equal(expected: 1, handler.RequestCount);
        Assert.Equal(expected: 0, handler.AreaRequestCount);
    }

    /// <summary>Clips fallback observations to the country and removes duplicates.</summary>
    [Fact]
    public async Task GetSegmentAsyncUsesOneFallbackEnvelopeAndPublishesOnlyClippedDistinctData()
    {
        using var handler = new RecordingHandler(
            (request, cancellationToken) =>
        {
            string path = Assert.IsType<Uri>(request.RequestUri).AbsolutePath;

            if (IsCountryRequest(path))
                return Task.FromResult(CountryUnavailableResponse());

            if (IsMapKeyStatusRequest(path))
                return Task.FromResult(MapKeyStatusResponse());

            Assert.True(path.Contains(value: "/api/area/csv/", StringComparison.Ordinal));
            Assert.EndsWith(expectedEndString: "/4", path, StringComparison.Ordinal);

            return Task.FromResult(CsvResponse(FallbackCsv));
        }
        );

        using var httpClient = new HttpClient(handler);

        FirmsClient client = CreateClient(httpClient, countryCode: "UKR", activeWindow: TimeSpan.FromHours(hours: 72), maximumConcurrency: 2);

        FirmsSegmentResult result = await client.GetSegmentAsync(countryCode: "UKR", source: "MODIS_NRT", TestContext.Current.CancellationToken);

        Assert.Equal(IngestionModes.AreaFallback, result.IngestionMode);
        Assert.True(result.Anomalies.Length == 1);
        Assert.Equal(expected: 1, handler.AreaRequestCount);
        Assert.Equal(expected: 3, handler.RequestCount);
        Assert.Equal(expected: 1, handler.MaximumConcurrency);
    }

    /// <summary>Requests a world-spanning envelope for Russia's antimeridian boundary.</summary>
    [Fact]
    public async Task GetSegmentAsyncUsesOneWorldSpanningEnvelopeForRussia()
    {
        string? areaBounds = null;

        using var handler = new RecordingHandler(
            (request, cancellationToken) =>
        {
            string path = Assert.IsType<Uri>(request.RequestUri).AbsolutePath;

            if (IsCountryRequest(path))
                return Task.FromResult(CountryUnavailableResponse());

            if (IsMapKeyStatusRequest(path))
                return Task.FromResult(MapKeyStatusResponse());

            string[] segments = path.Split(separator: '/', StringSplitOptions.RemoveEmptyEntries);
            areaBounds = segments[5];

            return Task.FromResult(CsvResponse(RussiaCsv));
        }
        );

        using var httpClient = new HttpClient(handler);

        FirmsClient client = CreateClient(httpClient, countryCode: "RUS");

        FirmsSegmentResult result = await client.GetSegmentAsync(countryCode: "RUS", source: "MODIS_NRT", TestContext.Current.CancellationToken);

        Assert.Equal(IngestionModes.AreaFallback, result.IngestionMode);
        Assert.True(result.Anomalies.Length == 1);
        Assert.Equal(expected: 1, handler.AreaRequestCount);
        Assert.NotNull(areaBounds);
        string[] coordinates = areaBounds.Split(separator: ',');
        Assert.Equal(expected: "-180", coordinates[0]);
        Assert.Equal(expected: "180", coordinates[2]);
    }

    /// <summary>Applies the Ukraine worldview when assigning fallback observations.</summary>
    [Theory]
    [MemberData(nameof(WorldviewCases))]
    public async Task GetSegmentAsyncUsesUkraineWorldviewForAreaFallback(string countryCode, double expectedLatitude)
    {
        using var handler = new RecordingHandler(
            (request, cancellationToken) =>
        {
            string path = Assert.IsType<Uri>(request.RequestUri).AbsolutePath;

            return IsCountryRequest(path)
                ? Task.FromResult(CountryUnavailableResponse())
                : IsMapKeyStatusRequest(path)
                ? Task.FromResult(MapKeyStatusResponse())
                : Task.FromResult(CsvResponse(UkraineWorldviewFallbackCsv));
        }
        );

        using var httpClient = new HttpClient(handler);

        FirmsClient client = CreateClient(httpClient, countryCode);

        FirmsSegmentResult result = await client.GetSegmentAsync(countryCode, source: "MODIS_NRT", TestContext.Current.CancellationToken);

        Assert.True(result.Anomalies.Length == 1);
        Anomaly anomaly = result.Anomalies[index: 0];
        Assert.Equal(IngestionModes.AreaFallback, result.IngestionMode);
        Assert.Equal(countryCode, anomaly.CountryCode);
        Assert.Equal(expectedLatitude, anomaly.Latitude);
        Assert.Equal(expected: 1, handler.AreaRequestCount);
    }

    private static HttpResponseMessage BlockingCsvResponse(Stream stream)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType: "text/csv");

        return new(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage CountryUnavailableResponse()
    {
        return new(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(content: "Invalid API call", Encoding.UTF8, mediaType: "text/plain")
        };
    }

    private static FirmsClient CreateClient(
        HttpClient httpClient,
        string countryCode,
        TimeSpan? activeWindow = null,
        TimeSpan? requestTimeout = null,
        int maximumConcurrency = 4
    )
    {
        FirmsOptions options = new(
            new string(c: 'A', count: 32),
            [countryCode],
            TimeSpan.FromMinutes(minutes: 5),
            activeWindow ?? TimeSpan.FromHours(hours: 24),
            requestTimeout ?? TimeSpan.FromSeconds(seconds: 45),
            maximumConcurrency
        );

        httpClient.BaseAddress = new(uriString: "https://firms.example.test/");

        return new(httpClient, options, new CountryBoundaryCatalog(options), TimeProvider.System, NullLogger<FirmsClient>.Instance);
    }

    private static HttpResponseMessage CsvResponse(string content = ModisCsv)
    {
        return new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, mediaType: "text/csv")
        };
    }

    private static bool IsCountryRequest(string path)
    {
        return path.Contains(value: "/api/country/csv/", StringComparison.Ordinal);
    }

    private static bool IsMapKeyStatusRequest(string path)
    {
        return path.Contains(value: "/mapserver/mapkey_status/", StringComparison.Ordinal);
    }

    private static HttpResponseMessage MapKeyStatusResponse()
    {
        // lang=json
        const string status = """{"transaction_limit":5000,"current_transactions":1}""";

        return new(HttpStatusCode.OK)
        {
            Content = new StringContent(status, Encoding.UTF8, mediaType: "application/json")
        };
    }

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return WaitForCancellationAsync(cancellationToken);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return new(WaitForCancellationAsync(cancellationToken));
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        private async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            if (!Started.Task.IsCompleted)
                Started.SetResult();

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            throw new UnreachableException();
        }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respondAsync) : HttpMessageHandler
    {
        private int _activeRequests;
        private int _areaRequestCount;
        private int _maximumConcurrency;
        private int _requestCount;

        internal int AreaRequestCount => Volatile.Read(ref _areaRequestCount);

        internal int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        internal int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int activeRequests = Interlocked.Increment(ref _activeRequests);
            RecordMaximumConcurrency(activeRequests);
            Assert.True(Interlocked.Increment(ref _requestCount) > 0);

            if (request.RequestUri?.AbsolutePath.Contains(value: "/api/area/csv/", StringComparison.Ordinal) == true)
                Assert.True(Interlocked.Increment(ref _areaRequestCount) > 0);

            try
            {
                return await respondAsync(request, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            }
            finally
            {
                Assert.True(Interlocked.Decrement(ref _activeRequests) >= 0);
            }
        }

        private void RecordMaximumConcurrency(int activeRequests)
        {
            int maximumConcurrency = Volatile.Read(ref _maximumConcurrency);

            while (activeRequests > maximumConcurrency)
            {
                int previous = Interlocked.CompareExchange(ref _maximumConcurrency, activeRequests, maximumConcurrency);

                if (previous == maximumConcurrency)
                    return;

                maximumConcurrency = previous;
            }
        }
    }
}
