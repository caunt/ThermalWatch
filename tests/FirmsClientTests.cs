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

    [Fact]
    public async Task GetSegmentAsyncUsesOneCountryRequestWhenCountryApiIsAvailable()
    {
        var handler = new RecordingHandler(static (_, _) => Task.FromResult(CsvResponse()));
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

    [Fact]
    public async Task GetSegmentAsyncBoundsFallbackConcurrencyAndPublishesOnlyClippedDistinctData()
    {
        var twoAreaRequestsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAreaRequests = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int areaRequestCount = 0;
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (IsCountryRequest(path))
                return CountryUnavailableResponse();
            if (IsMapKeyStatusRequest(path))
                return MapKeyStatusResponse();

            int count = Interlocked.Increment(ref areaRequestCount);
            if (count == 2)
                twoAreaRequestsStarted.SetResult();

            await releaseAreaRequests.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return CsvResponse();
        });
        using FirmsClient client = CreateClient(handler, countryCode: "UKR", maxConcurrency: 2);

        Task<FirmsSegmentResult> loading = client.GetSegmentAsync(
            countryCode: "UKR",
            source: "MODIS_NRT",
            TestContext.Current.CancellationToken);
        await twoAreaRequestsStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, Volatile.Read(ref areaRequestCount));
        Assert.Equal(2, handler.MaximumConcurrency);
        releaseAreaRequests.SetResult();

        FirmsSegmentResult result = await loading;
        Assert.Equal(IngestionModes.AreaFallback, result.IngestionMode);
        Assert.Single(result.Detections);
        Assert.Equal(5, handler.AreaRequestCount);
        Assert.Equal(2, handler.MaximumConcurrency);
    }

    [Fact]
    public async Task GetSegmentAsyncCancelsSiblingTilesAfterFirstFallbackFailure()
    {
        var fourAreaRequestsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int areaRequestCount = 0;
        int canceledRequestCount = 0;
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (IsCountryRequest(path))
                return CountryUnavailableResponse();
            if (IsMapKeyStatusRequest(path))
                return MapKeyStatusResponse();

            int requestNumber = Interlocked.Increment(ref areaRequestCount);
            if (requestNumber == 4)
                fourAreaRequestsStarted.SetResult();

            await fourAreaRequestsStarted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (requestNumber == 1)
                return new(HttpStatusCode.ServiceUnavailable);

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                throw new UnreachableException();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref canceledRequestCount);
                throw;
            }
        });
        using FirmsClient client = CreateClient(handler, countryCode: "UKR", maxConcurrency: 4);

        FirmsRequestException exception = await Assert.ThrowsAsync<FirmsRequestException>(() => client.GetSegmentAsync(
            countryCode: "UKR",
            source: "MODIS_NRT",
            TestContext.Current.CancellationToken));

        Assert.Equal(
            "FIRMS area fallback tile failed: FIRMS returned an upstream error.",
            exception.SafeMessage);
        Assert.Equal(4, Volatile.Read(ref areaRequestCount));
        Assert.Equal(3, Volatile.Read(ref canceledRequestCount));
        Assert.Equal(4, handler.AreaRequestCount);
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
        TimeSpan? requestTimeout = null,
        int maxConcurrency = 4)
    {
        FirmsOptions options = new(
            MapKey: new string('A', count: 32),
            Countries: [countryCode],
            PollInterval: TimeSpan.FromMinutes(minutes: 5),
            ActiveWindow: TimeSpan.FromHours(hours: 24),
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

    private static HttpResponseMessage CsvResponse() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(ModisCsv, Encoding.UTF8, mediaType: "text/csv")
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
