using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using ThermalWatch.Api;
using ThermalWatch.Api.Polling;
using ThermalWatch.Core;

namespace ThermalWatch.Tests.Api;

/// <summary>Verifies active-window filtering and refresh concurrency.</summary>
public sealed class FirmsRefreshCycleTests
{
    private const string ModisCsv = """
        latitude,longitude,brightness,scan,track,acq_date,acq_time,satellite,instrument,confidence,version,bright_t31,frp,daynight
        49,30,330,1,1,2026-07-23,0731,T,MODIS,80,6.1NRT,300,100,D
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
    private const string ViirsCsv = """
        latitude,longitude,bright_ti4,scan,track,acq_date,acq_time,satellite,instrument,confidence,version,bright_ti5,frp,daynight
        49,30,330,1,1,2026-07-23,0731,N,VIIRS,n,2.0NRT,300,100,D
        """;

    /// <summary>Applies the exact active window across UTC calendar boundaries.</summary>
    [Fact]
    public async Task RefreshAsyncAppliesSeventyTwoHourWindowAcrossUtcCalendarDays()
    {
        using var handler = new CoordinatedHandler(
            (request, cancellationToken) =>
        {
            string path = Assert.IsType<Uri>(request.RequestUri).AbsolutePath;
            Assert.EndsWith(expectedEndString: "/4", path, StringComparison.Ordinal);

            return Task.FromResult(RolloverCsvResponse(path));
        }
        );

        FirmsOptions options = new(
            new string(c: 'A', count: 32),
            ["UKR"],
            TimeSpan.FromMinutes(minutes: 5),
            TimeSpan.FromHours(hours: 72),
            TimeSpan.FromSeconds(seconds: 45),
            MaximumConcurrency: 4
        );

        var timeProvider = new FakeTimeProvider(new(year: 2026, month: 7, day: 24, hour: 0, minute: 1, second: 0, TimeSpan.Zero));

        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new(uriString: "https://firms.example.test/")
        };

        var firmsClient = new FirmsClient(httpClient, options, new CountryBoundaryCatalog(options), timeProvider, NullLogger<FirmsClient>.Instance);

        var snapshotStore = new AnomalySnapshotStore(options, timeProvider);
        FirmsHistoryStore historyStore = CreateHistoryStore(options, timeProvider);

        var cycle = new FirmsRefreshCycle(firmsClient, options, snapshotStore, historyStore, timeProvider, NullLogger<FirmsRefreshCycle>.Instance);

        FirmsRefreshCycleResult result = await cycle.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected: 4, result.SuccessfulSegmentCount);
        Assert.Equal(expected: 0, result.FailedSegmentCount);
        Assert.Equal(expected: 4, snapshotStore.Current.AnomalyCount);

        foreach (Anomaly anomaly in snapshotStore.Current.Anomalies)
            Assert.Equal(new DateTimeOffset(year: 2026, month: 7, day: 21, hour: 0, minute: 2, second: 0, TimeSpan.Zero), anomaly.AcquiredAtUtc);
    }

    /// <summary>Refreshes at most two country/source segments concurrently.</summary>
    [Fact]
    public async Task RefreshAsyncLimitsConcurrentSegmentsToTwo()
    {
        var twoParallelRequestsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseParallelRequests = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int requestCount = 0;

        using var handler = new CoordinatedHandler(
            async (request, cancellationToken) =>
        {
            int currentRequest = Interlocked.Increment(ref requestCount);

            if (currentRequest > 1)
            {
                if (currentRequest == 3)
                    twoParallelRequestsStarted.SetResult();

                await releaseParallelRequests.Task.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            }

            return CsvResponse(Assert.IsType<Uri>(request.RequestUri).AbsolutePath);
        }
        );

        FirmsOptions options = new(
            new string(c: 'A', count: 32),
            ["UKR", "RUS"],
            TimeSpan.FromMinutes(minutes: 5),
            TimeSpan.FromHours(hours: 24),
            TimeSpan.FromSeconds(seconds: 45),
            MaximumConcurrency: 4
        );

        var timeProvider = new FakeTimeProvider(new(year: 2026, month: 7, day: 23, hour: 12, minute: 0, second: 0, TimeSpan.Zero));

        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new(uriString: "https://firms.example.test/")
        };

        var firmsClient = new FirmsClient(httpClient, options, new CountryBoundaryCatalog(options), timeProvider, NullLogger<FirmsClient>.Instance);

        var snapshotStore = new AnomalySnapshotStore(options, timeProvider);
        FirmsHistoryStore historyStore = CreateHistoryStore(options, timeProvider);

        var cycle = new FirmsRefreshCycle(firmsClient, options, snapshotStore, historyStore, timeProvider, NullLogger<FirmsRefreshCycle>.Instance);

        Task<FirmsRefreshCycleResult> refreshing = cycle.RefreshAsync(TestContext.Current.CancellationToken);
        await twoParallelRequestsStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected: 3, Volatile.Read(ref requestCount));
        Assert.Equal(expected: 2, handler.MaximumConcurrency);
        releaseParallelRequests.SetResult();

        FirmsRefreshCycleResult result = await refreshing.ConfigureAwait(continueOnCapturedContext: true);
        Assert.Equal(expected: 8, result.SuccessfulSegmentCount);
        Assert.Equal(expected: 0, result.FailedSegmentCount);
        Assert.True(snapshotStore.Current.IsReady);
        Assert.False(snapshotStore.Current.IsPartiallyStale);
        Assert.Equal(expected: 2, handler.MaximumConcurrency);
    }

    private static FirmsHistoryStore CreateHistoryStore(FirmsOptions options, TimeProvider timeProvider)
    {
        return new(options, ApplicationConfiguration.ParseNotificationOptions(name => null), timeProvider);
    }

    private static HttpResponseMessage CsvResponse(string path)
    {
        return new(HttpStatusCode.OK)
        {
            Content = new StringContent(path.Contains(value: "MODIS_NRT", StringComparison.Ordinal) ? ModisCsv : ViirsCsv, Encoding.UTF8, mediaType: "text/csv")
        };
    }

    private static HttpResponseMessage RolloverCsvResponse(string path)
    {
        return new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                path.Contains(value: "MODIS_NRT", StringComparison.Ordinal)
                    ? RolloverModisCsv
                    : RolloverViirsCsv,
                Encoding.UTF8,
                mediaType: "text/csv"
            )
        };
    }

    private sealed class CoordinatedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respondAsync) : HttpMessageHandler
    {
        private int _activeRequests;
        private int _maximumConcurrency;

        internal int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int activeRequests = Interlocked.Increment(ref _activeRequests);
            RecordMaximumConcurrency(activeRequests);

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
