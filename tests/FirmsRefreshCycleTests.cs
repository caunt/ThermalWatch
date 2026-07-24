using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ThermalWatch.Api;
using ThermalWatch.Core;

namespace ThermalWatch.Tests;

public sealed class FirmsRefreshCycleTests
{
    private const string ModisCsv = """
        latitude,longitude,brightness,scan,track,acq_date,acq_time,satellite,instrument,confidence,version,bright_t31,frp,daynight
        49,30,330,1,1,2026-07-23,0731,T,MODIS,80,6.1NRT,300,100,D
        """;
    private const string ViirsCsv = """
        latitude,longitude,bright_ti4,scan,track,acq_date,acq_time,satellite,instrument,confidence,version,bright_ti5,frp,daynight
        49,30,330,1,1,2026-07-23,0731,N,VIIRS,n,2.0NRT,300,100,D
        """;
    private const string RolloverModisCsv = """
        latitude,longitude,brightness,scan,track,acq_date,acq_time,satellite,instrument,confidence,version,bright_t31,frp,daynight
        49,30,330,1,1,2026-07-21,0000,T,MODIS,80,6.1NRT,300,100,D
        49,30,330,1,1,2026-07-21,0002,T,MODIS,80,6.1NRT,300,100,D
        """;
    private const string RolloverViirsCsv = """
        latitude,longitude,bright_ti4,scan,track,acq_date,acq_time,satellite,instrument,confidence,version,bright_ti5,frp,daynight
        49,30,330,1,1,2026-07-21,0000,N,VIIRS,n,2.0NRT,300,100,D
        49,30,330,1,1,2026-07-21,0002,N,VIIRS,n,2.0NRT,300,100,D
        """;

    [Fact]
    public async Task RefreshAsyncAppliesSeventyTwoHourWindowAcrossUtcCalendarDays()
    {
        var handler = new CoordinatedHandler((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            Assert.EndsWith(expectedEndString: "/4", path, StringComparison.Ordinal);
            return Task.FromResult(RolloverCsvResponse(path));
        });
        FirmsOptions options = new(
            MapKey: new string('A', count: 32),
            Countries: ["UKR"],
            PollInterval: TimeSpan.FromMinutes(minutes: 5),
            ActiveWindow: TimeSpan.FromHours(hours: 72),
            RequestTimeout: TimeSpan.FromSeconds(seconds: 45),
            MaxConcurrency: 4);
        var timeProvider = new FakeTimeProvider(
            new(year: 2026, month: 7, day: 24, hour: 0, minute: 1, second: 0, TimeSpan.Zero));
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new(uriString: "https://firms.example.test/")
        };
        using var firmsClient = new FirmsClient(
            httpClient,
            options,
            new CountryBoundaryCatalog(options),
            timeProvider,
            NullLogger<FirmsClient>.Instance);
        var snapshotStore = new AnomalySnapshotStore(options, timeProvider);
        var cycle = new FirmsRefreshCycle(
            firmsClient,
            options,
            snapshotStore,
            timeProvider,
            NullLogger<FirmsRefreshCycle>.Instance);

        FirmsRefreshCycleResult result = await cycle.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, result.SuccessfulSegmentCount);
        Assert.Equal(0, result.FailedSegmentCount);
        Assert.Equal(4, snapshotStore.Current.Count);
        Assert.All(
            snapshotStore.Current.Items,
            detection => Assert.Equal(
                new DateTimeOffset(
                    year: 2026,
                    month: 7,
                    day: 21,
                    hour: 0,
                    minute: 2,
                    second: 0,
                    TimeSpan.Zero),
                detection.AcquiredAtUtc));
    }

    [Fact]
    public async Task RefreshAsyncLimitsConcurrentSegmentsToTwo()
    {
        var twoParallelRequestsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseParallelRequests = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int requestCount = 0;
        var handler = new CoordinatedHandler(async (request, cancellationToken) =>
        {
            int currentRequest = Interlocked.Increment(ref requestCount);
            if (currentRequest > 1)
            {
                if (currentRequest == 3)
                    twoParallelRequestsStarted.SetResult();

                await releaseParallelRequests.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return CsvResponse(request.RequestUri!.AbsolutePath);
        });
        FirmsOptions options = new(
            MapKey: new string('A', count: 32),
            Countries: ["UKR", "RUS"],
            PollInterval: TimeSpan.FromMinutes(minutes: 5),
            ActiveWindow: TimeSpan.FromHours(hours: 24),
            RequestTimeout: TimeSpan.FromSeconds(seconds: 45),
            MaxConcurrency: 4);
        var timeProvider = new FakeTimeProvider(
            new(year: 2026, month: 7, day: 23, hour: 12, minute: 0, second: 0, TimeSpan.Zero));
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new(uriString: "https://firms.example.test/")
        };
        using var firmsClient = new FirmsClient(
            httpClient,
            options,
            new CountryBoundaryCatalog(options),
            timeProvider,
            NullLogger<FirmsClient>.Instance);
        var snapshotStore = new AnomalySnapshotStore(options, timeProvider);
        var cycle = new FirmsRefreshCycle(
            firmsClient,
            options,
            snapshotStore,
            timeProvider,
            NullLogger<FirmsRefreshCycle>.Instance);

        Task<FirmsRefreshCycleResult> refreshing = cycle.RefreshAsync(TestContext.Current.CancellationToken);
        await twoParallelRequestsStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, Volatile.Read(ref requestCount));
        Assert.Equal(2, handler.MaximumConcurrency);
        releaseParallelRequests.SetResult();

        FirmsRefreshCycleResult result = await refreshing;
        Assert.Equal(8, result.SuccessfulSegmentCount);
        Assert.Equal(0, result.FailedSegmentCount);
        Assert.True(snapshotStore.Current.IsReady);
        Assert.False(snapshotStore.Current.IsPartiallyStale);
        Assert.Equal(2, handler.MaximumConcurrency);
    }

    private static HttpResponseMessage CsvResponse(string path) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                path.Contains(value: "MODIS_NRT", StringComparison.Ordinal) ? ModisCsv : ViirsCsv,
                Encoding.UTF8,
                mediaType: "text/csv")
        };

    private static HttpResponseMessage RolloverCsvResponse(string path) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                path.Contains(value: "MODIS_NRT", StringComparison.Ordinal)
                    ? RolloverModisCsv
                    : RolloverViirsCsv,
                Encoding.UTF8,
                mediaType: "text/csv")
        };

    private sealed class CoordinatedHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respondAsync) : HttpMessageHandler
    {
        private int _activeRequests;
        private int _maximumConcurrency;

        internal int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            int activeRequests = Interlocked.Increment(ref _activeRequests);
            RecordMaximumConcurrency(activeRequests);
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
}
