using System.Collections.Concurrent;
using System.Net;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using ThermalWatch.Api;
using ThermalWatch.Api.Polling;
using ThermalWatch.Core;

namespace ThermalWatch.Tests.Api;

/// <summary>Verifies historical acquisition windows and retry isolation.</summary>
public sealed class FirmsHistoryBackfillTests
{
    private static readonly DateTimeOffset Now = new(year: 2026, month: 7, day: 27, hour: 12, minute: 0, second: 0, TimeSpan.Zero);

    /// <summary>Loads each completed historical window once for every source.</summary>
    [Fact]
    public async Task RefreshAsyncLoadsSixFiveDayWindowsForEveryNrtSourceOnce()
    {
        using var handler = new RecordingHandler(static request => CsvResponse(Assert.IsType<Uri>(request.RequestUri).AbsolutePath));

        using var fixture = new BackfillFixture(handler);

        FirmsHistoryBackfillResult first = await fixture.Backfill.RefreshAsync(TestContext.Current.CancellationToken);
        FirmsHistoryBackfillResult second = await fixture.Backfill.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected: 24, first.SuccessfulRequestCount);
        Assert.Equal(expected: 0, first.FailedRequestCount);
        Assert.Equal(expected: 0, second.AttemptedRequestCount);
        Assert.True(fixture.Store.Current.IsReady);
        Assert.Equal(expected: 24, handler.Paths.Count);

        foreach (string source in FirmsSources.All)
        {
            Assert.Equal(
                [
                    "2026-06-27",
                    "2026-07-02",
                    "2026-07-07",
                    "2026-07-12",
                    "2026-07-17",
                    "2026-07-22"
                ],
                handler.Paths
                    .Where(path => path.Contains(source, StringComparison.Ordinal))
                    .Select(path => path[^10..])
                    .Order(StringComparer.Ordinal),
                StringComparer.Ordinal
            );
        }
    }

    /// <summary>Retries only a failed window while retaining successful historical slices.</summary>
    [Fact]
    public async Task RefreshAsyncRetriesOnlyFailedHistoricalWindow()
    {
        int shouldFail = 1;

        using var handler = new RecordingHandler(
            request =>
        {
            string path = Assert.IsType<Uri>(request.RequestUri).AbsolutePath;

            bool shouldFailThisRequest = path.Contains(value: "MODIS_NRT", StringComparison.Ordinal)
                && path.EndsWith(value: "/5/2026-06-27", StringComparison.Ordinal)
                && Interlocked.Exchange(ref shouldFail, value: 0) == 1;

            return shouldFailThisRequest ? new(HttpStatusCode.InternalServerError) : CsvResponse(path);
        }
        );

        using var fixture = new BackfillFixture(handler);

        FirmsHistoryBackfillResult first = await fixture.Backfill.RefreshAsync(TestContext.Current.CancellationToken);
        FirmsHistoryBackfillResult second = await fixture.Backfill.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected: 23, first.SuccessfulRequestCount);
        Assert.Equal(expected: 1, first.FailedRequestCount);
        Assert.Equal(expected: 1, second.SuccessfulRequestCount);
        Assert.Equal(expected: 0, second.FailedRequestCount);
        Assert.Equal(expected: 25, handler.Paths.Count);
        Assert.True(fixture.Store.Current.IsReady);
    }

    private static HttpResponseMessage CsvResponse(string path)
    {
        return new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                path.Contains(value: "MODIS_NRT", StringComparison.Ordinal)
                    ? "latitude,longitude,brightness,scan,track,acq_date,acq_time,satellite,instrument,confidence,version,bright_t31,frp,daynight\n"
                    : "latitude,longitude,bright_ti4,scan,track,acq_date,acq_time,satellite,instrument,confidence,version,bright_ti5,frp,daynight\n",
                Encoding.UTF8,
                mediaType: "text/csv"
            )
        };
    }

    private sealed class BackfillFixture : IDisposable
    {
        private readonly FirmsClient _client;
        private readonly HttpClient _httpClient;

        public BackfillFixture(HttpMessageHandler handler)
        {
            FirmsOptions options = new(
                new string(c: 'A', count: 32),
                ["UKR"],
                TimeSpan.FromMinutes(minutes: 5),
                TimeSpan.FromHours(hours: 24),
                TimeSpan.FromSeconds(seconds: 45),
                MaximumConcurrency: 4
            );

            var timeProvider = new FakeTimeProvider(Now);
            _httpClient = new(handler) { BaseAddress = new(uriString: "https://firms.example.test/") };
            _client = new(_httpClient, options, new CountryBoundaryCatalog(options), timeProvider, NullLogger<FirmsClient>.Instance);
            Store = new(options, ApplicationConfiguration.ParseNotificationOptions(name => null), timeProvider);
            Backfill = new(_client, options, Store, timeProvider, NullLogger<FirmsHistoryBackfill>.Instance);
        }

        public FirmsHistoryBackfill Backfill { get; }

        public FirmsHistoryStore Store { get; }

        public void Dispose()
        {
            _httpClient.Dispose();
        }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public ConcurrentQueue<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Paths.Enqueue(Assert.IsType<Uri>(request.RequestUri).AbsolutePath);

            return Task.FromResult(respond(request));
        }
    }
}
