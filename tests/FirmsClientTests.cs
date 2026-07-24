using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ThermalWatch.Core;

namespace ThermalWatch.Tests;

public sealed class FirmsClientTests
{
    private const string ModisCsv = """
        latitude,longitude,brightness,scan,track,acq_date,acq_time,satellite,instrument,confidence,version,bright_t31,frp,daynight
        49,30,330,1,1,2026-07-23,0731,T,MODIS,80,6.1NRT,300,100,D
        """;

    private const string FallbackCsv = """
        latitude,longitude,brightness,scan,track,acq_date,acq_time,satellite,instrument,confidence,version,bright_t31,frp,daynight
        49,30,330,1,1,2026-07-23,0731,T,MODIS,80,6.1NRT,300,100,D
        49,30,330,1,1,2026-07-23,0731,T,MODIS,80,6.1NRT,300,100,D
        0,0,330,1,1,2026-07-23,0731,T,MODIS,80,6.1NRT,300,100,D
        """;

    private const string RussiaCsv = """
        latitude,longitude,brightness,scan,track,acq_date,acq_time,satellite,instrument,confidence,version,bright_t31,frp,daynight
        60,100,330,1,1,2026-07-23,0731,T,MODIS,80,6.1NRT,300,100,D
        """;

    [Fact]
    public async Task GetSegmentAsyncUsesOneCountryRequestWhenCountryApiIsAvailable()
    {
        var handler = new RecordingHandler(static (request, _) =>
        {
            Assert.EndsWith(
                expectedEndString: "/2",
                request.RequestUri!.AbsolutePath,
                StringComparison.Ordinal);
            return Task.FromResult(CsvResponse());
        });
        using FirmsClient client = CreateClient(handler, countryCode: "UKR");

        FirmsSegmentResult result = await client.GetSegmentAsync(
            countryCode: "UKR",
            source: "MODIS_NRT",
            TestContext.Current.CancellationToken);

        Assert.Equal(IngestionModes.Country, result.IngestionMode);
        Assert.Single(result.Detections);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(0, handler.AreaRequestCount);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(24, 2)]
    [InlineData(25, 3)]
    [InlineData(48, 3)]
    [InlineData(72, 4)]
    public async Task GetSegmentAsyncDerivesCountryRequestDayRangeFromActiveWindow(
        int activeWindowHours,
        int expectedDayRange)
    {
        var handler = new RecordingHandler((request, _) =>
        {
            Assert.EndsWith(
                expectedEndString: $"/{expectedDayRange}",
                request.RequestUri!.AbsolutePath,
                StringComparison.Ordinal);
            return Task.FromResult(CsvResponse());
        });
        using FirmsClient client = CreateClient(
            handler,
            countryCode: "UKR",
            activeWindow: TimeSpan.FromHours(activeWindowHours));

        FirmsSegmentResult result = await client.GetSegmentAsync(
            countryCode: "UKR",
            source: "MODIS_NRT",
            TestContext.Current.CancellationToken);

        Assert.Equal(IngestionModes.Country, result.IngestionMode);
        Assert.Single(result.Detections);
    }

    [Fact]
    public async Task GetSegmentAsyncUsesOneFallbackEnvelopeAndPublishesOnlyClippedDistinctData()
    {
        var handler = new RecordingHandler((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (IsCountryRequest(path))
                return Task.FromResult(CountryUnavailableResponse());
            if (IsMapKeyStatusRequest(path))
                return Task.FromResult(MapKeyStatusResponse());

            Assert.True(path.Contains(value: "/api/area/csv/", StringComparison.Ordinal));
            Assert.EndsWith(expectedEndString: "/4", path, StringComparison.Ordinal);
            return Task.FromResult(CsvResponse(FallbackCsv));
        });
        using FirmsClient client = CreateClient(
            handler,
            countryCode: "UKR",
            activeWindow: TimeSpan.FromHours(hours: 72),
            maxConcurrency: 2);

        FirmsSegmentResult result = await client.GetSegmentAsync(
            countryCode: "UKR",
            source: "MODIS_NRT",
            TestContext.Current.CancellationToken);

        Assert.Equal(IngestionModes.AreaFallback, result.IngestionMode);
        Assert.Single(result.Detections);
        Assert.Equal(1, handler.AreaRequestCount);
        Assert.Equal(3, handler.RequestCount);
        Assert.Equal(1, handler.MaximumConcurrency);
    }

    [Fact]
    public async Task GetSegmentAsyncUsesOneWorldSpanningEnvelopeForRussia()
    {
        string? areaBounds = null;
        var handler = new RecordingHandler((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (IsCountryRequest(path))
                return Task.FromResult(CountryUnavailableResponse());
            if (IsMapKeyStatusRequest(path))
                return Task.FromResult(MapKeyStatusResponse());

            string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            areaBounds = segments[5];
            return Task.FromResult(CsvResponse(RussiaCsv));
        });
        using FirmsClient client = CreateClient(handler, countryCode: "RUS");

        FirmsSegmentResult result = await client.GetSegmentAsync(
            countryCode: "RUS",
            source: "MODIS_NRT",
            TestContext.Current.CancellationToken);

        Assert.Equal(IngestionModes.AreaFallback, result.IngestionMode);
        Assert.Single(result.Detections);
        Assert.Equal(1, handler.AreaRequestCount);
        Assert.NotNull(areaBounds);
        string[] coordinates = areaBounds.Split(',');
        Assert.Equal("-180", coordinates[0]);
        Assert.Equal("180", coordinates[2]);
    }

    [Fact]
    public async Task GetSegmentAsyncFailsWhenFallbackEnvelopeRequestFails()
    {
        var handler = new RecordingHandler((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            return Task.FromResult(IsCountryRequest(path)
                ? CountryUnavailableResponse()
                : IsMapKeyStatusRequest(path)
                    ? MapKeyStatusResponse()
                    : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });
        using FirmsClient client = CreateClient(handler, countryCode: "UKR");

        FirmsRequestException exception = await Assert.ThrowsAsync<FirmsRequestException>(() => client.GetSegmentAsync(
            countryCode: "UKR",
            source: "MODIS_NRT",
            TestContext.Current.CancellationToken));

        Assert.Equal(
            "FIRMS area fallback request failed: FIRMS returned an upstream error.",
            exception.SafeMessage);
        Assert.Equal(1, handler.AreaRequestCount);
    }

    [Fact]
    public async Task GetSegmentAsyncTimesOutBlockedContentAndReleasesRequestGate()
    {
        int countryRequestCount = 0;
        var handler = new RecordingHandler((request, _) =>
        {
            Assert.True(IsCountryRequest(request.RequestUri!.AbsolutePath));
            return Task.FromResult(Interlocked.Increment(ref countryRequestCount) == 1
                ? BlockingCsvResponse(new BlockingReadStream())
                : CsvResponse());
        });
        using FirmsClient client = CreateClient(
            handler,
            countryCode: "UKR",
            requestTimeout: TimeSpan.FromMilliseconds(milliseconds: 100),
            maxConcurrency: 1);

        FirmsRequestException exception = await Assert.ThrowsAsync<FirmsRequestException>(async () =>
            await client.GetSegmentAsync(
                countryCode: "UKR",
                source: "MODIS_NRT",
                TestContext.Current.CancellationToken).WaitAsync(
                    TimeSpan.FromSeconds(seconds: 2),
                    TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal("FIRMS request timed out.", exception.SafeMessage);

        FirmsSegmentResult recovered = await client.GetSegmentAsync(
            countryCode: "UKR",
            source: "MODIS_NRT",
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(IngestionModes.Country, recovered.IngestionMode);
        Assert.Single(recovered.Detections);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task GetSegmentAsyncTimesOutBlockedFallbackContentAndReleasesRequestGate()
    {
        int areaRequestCount = 0;
        var handler = new RecordingHandler((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (IsCountryRequest(path))
                return Task.FromResult(CountryUnavailableResponse());
            if (IsMapKeyStatusRequest(path))
                return Task.FromResult(MapKeyStatusResponse());

            return Task.FromResult(Interlocked.Increment(ref areaRequestCount) == 1
                ? BlockingCsvResponse(new BlockingReadStream())
                : CsvResponse());
        });
        using FirmsClient client = CreateClient(
            handler,
            countryCode: "UKR",
            requestTimeout: TimeSpan.FromMilliseconds(milliseconds: 100),
            maxConcurrency: 1);

        FirmsRequestException exception = await Assert.ThrowsAsync<FirmsRequestException>(async () =>
            await client.GetSegmentAsync(
                countryCode: "UKR",
                source: "MODIS_NRT",
                TestContext.Current.CancellationToken).WaitAsync(
                    TimeSpan.FromSeconds(seconds: 2),
                    TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal(
            "FIRMS area fallback request failed: FIRMS request timed out.",
            exception.SafeMessage);

        FirmsSegmentResult recovered = await client.GetSegmentAsync(
            countryCode: "UKR",
            source: "MODIS_NRT",
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(IngestionModes.AreaFallback, recovered.IngestionMode);
        Assert.Single(recovered.Detections);
        Assert.Equal(2, handler.AreaRequestCount);
        Assert.Equal(1, handler.MaximumConcurrency);
    }

    [Fact]
    public async Task GetSegmentAsyncPropagatesCallerCancellationDuringContentRead()
    {
        var stream = new BlockingReadStream();
        var handler = new RecordingHandler((_, _) => Task.FromResult(BlockingCsvResponse(stream)));
        using FirmsClient client = CreateClient(
            handler,
            countryCode: "UKR",
            requestTimeout: TimeSpan.FromSeconds(seconds: 5),
            maxConcurrency: 1);
        using var cancellation = new CancellationTokenSource();

        Task<FirmsSegmentResult> loading = client.GetSegmentAsync(
            countryCode: "UKR",
            source: "MODIS_NRT",
            cancellation.Token);
        await stream.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
    }

    private static FirmsClient CreateClient(
        HttpMessageHandler handler,
        string countryCode,
        TimeSpan? activeWindow = null,
        TimeSpan? requestTimeout = null,
        int maxConcurrency = 4)
    {
        FirmsOptions options = new(
            MapKey: new string('A', count: 32),
            Countries: [countryCode],
            PollInterval: TimeSpan.FromMinutes(minutes: 5),
            ActiveWindow: activeWindow ?? TimeSpan.FromHours(hours: 24),
            RequestTimeout: requestTimeout ?? TimeSpan.FromSeconds(seconds: 45),
            maxConcurrency);
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new(uriString: "https://firms.example.test/")
        };
        return new(
            httpClient,
            options,
            new CountryBoundaryCatalog(options),
            TimeProvider.System,
            NullLogger<FirmsClient>.Instance);
    }

    private static bool IsCountryRequest(string path) =>
        path.Contains(value: "/api/country/csv/", StringComparison.Ordinal);

    private static bool IsMapKeyStatusRequest(string path) =>
        path.Contains(value: "/mapserver/mapkey_status/", StringComparison.Ordinal);

    private static HttpResponseMessage CountryUnavailableResponse() =>
        new(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(content: "Invalid API call", Encoding.UTF8, mediaType: "text/plain")
        };

    private static HttpResponseMessage MapKeyStatusResponse() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                content: "{\"transaction_limit\":5000,\"current_transactions\":1}",
                Encoding.UTF8,
                mediaType: "application/json")
        };

    private static HttpResponseMessage CsvResponse(string content = ModisCsv) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, mediaType: "text/csv")
        };

    private static HttpResponseMessage BlockingCsvResponse(Stream stream)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType: "text/csv");
        return new(HttpStatusCode.OK) { Content = content };
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respondAsync) : HttpMessageHandler
    {
        private int _activeRequests;
        private int _areaRequestCount;
        private int _maximumConcurrency;
        private int _requestCount;

        internal int AreaRequestCount => Volatile.Read(ref _areaRequestCount);

        internal int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        internal int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            int activeRequests = Interlocked.Increment(ref _activeRequests);
            RecordMaximumConcurrency(activeRequests);
            Interlocked.Increment(ref _requestCount);
            if (request.RequestUri?.AbsolutePath.Contains(value: "/api/area/csv/", StringComparison.Ordinal) == true)
                Interlocked.Increment(ref _areaRequestCount);

            try
            {
                return await respondAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _activeRequests);
            }
        }

        private void RecordMaximumConcurrency(int activeRequests)
        {
            int maximumConcurrency = Volatile.Read(ref _maximumConcurrency);
            while (activeRequests > maximumConcurrency)
            {
                int previous = Interlocked.CompareExchange(
                    ref _maximumConcurrency,
                    activeRequests,
                    maximumConcurrency);
                if (previous == maximumConcurrency)
                    return;

                maximumConcurrency = previous;
            }
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) => WaitForCancellationAsync(cancellationToken);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) => new(WaitForCancellationAsync(cancellationToken));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new UnreachableException();
        }
    }
}
