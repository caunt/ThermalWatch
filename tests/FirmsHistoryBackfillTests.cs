using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ThermalWatch.Api;
using ThermalWatch.Core;

namespace ThermalWatch.Tests;

public sealed class FirmsHistoryBackfillTests
{
    private static readonly DateTimeOffset s_now = new(
        year: 2026,
        month: 7,
        day: 27,
        hour: 12,
        minute: 0,
        second: 0,
        TimeSpan.Zero);

    [Fact]
    public async Task RefreshAsyncLoadsSixFiveDayWindowsForEveryNrtSourceOnce()
    {
        var handler = new RecordingHandler(static request => CsvResponse(request.RequestUri!.AbsolutePath));
        using var fixture = new BackfillFixture(handler);

        FirmsHistoryBackfillResult first = await fixture.Backfill.RefreshAsync(TestContext.Current.CancellationToken);
        FirmsHistoryBackfillResult second = await fixture.Backfill.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(24, first.SuccessfulRequestCount);
        Assert.Equal(0, first.FailedRequestCount);
        Assert.Equal(0, second.AttemptedRequestCount);
        Assert.True(fixture.Store.Current.IsReady);
        Assert.Equal(24, handler.Paths.Count);
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
                StringComparer.Ordinal);
        }
    }

    [Fact]
    public async Task RefreshAsyncRetriesOnlyFailedHistoricalWindow()
    {
        int shouldFail = 1;
        var handler = new RecordingHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.Contains(value: "MODIS_NRT", StringComparison.Ordinal)
                && path.EndsWith(value: "/5/2026-06-27", StringComparison.Ordinal)
                && Interlocked.Exchange(ref shouldFail, value: 0) == 1)
            {
                return new(HttpStatusCode.InternalServerError);
            }

            return CsvResponse(path);
        });
        using var fixture = new BackfillFixture(handler);

        FirmsHistoryBackfillResult first = await fixture.Backfill.RefreshAsync(TestContext.Current.CancellationToken);
        FirmsHistoryBackfillResult second = await fixture.Backfill.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(23, first.SuccessfulRequestCount);
        Assert.Equal(1, first.FailedRequestCount);
        Assert.Equal(1, second.SuccessfulRequestCount);
        Assert.Equal(0, second.FailedRequestCount);
        Assert.Equal(25, handler.Paths.Count);
        Assert.True(fixture.Store.Current.IsReady);
    }

    private static HttpResponseMessage CsvResponse(string path) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                path.Contains(value: "MODIS_NRT", StringComparison.Ordinal)
                    ? "latitude,longitude,brightness,scan,track,acq_date,acq_time,satellite,instrument,confidence,version,bright_t31,frp,daynight\n"
                    : "latitude,longitude,bright_ti4,scan,track,acq_date,acq_time,satellite,instrument,confidence,version,bright_ti5,frp,daynight\n",
                Encoding.UTF8,
                mediaType: "text/csv")
        };

    private sealed class BackfillFixture : IDisposable
    {
        private readonly FirmsClient _client;

        public BackfillFixture(HttpMessageHandler handler)
        {
            FirmsOptions options = new(
                MapKey: new string('A', count: 32),
                CountryCodes: ["UKR"],
                PollInterval: TimeSpan.FromMinutes(minutes: 5),
                ActiveWindow: TimeSpan.FromHours(hours: 24),
                RequestTimeout: TimeSpan.FromSeconds(seconds: 45),
                MaxConcurrency: 4);
            var timeProvider = new FakeTimeProvider(s_now);
            _client = new(
                new HttpClient(handler) { BaseAddress = new(uriString: "https://firms.example.test/") },
                options,
                new CountryBoundaryCatalog(options),
                timeProvider,
                NullLogger<FirmsClient>.Instance);
            Store = new(
                options,
                ApplicationConfiguration.ParseNotificationOptions(_ => null),
                timeProvider);
            Backfill = new(
                _client,
                options,
                Store,
                timeProvider,
                NullLogger<FirmsHistoryBackfill>.Instance);
        }

        public FirmsHistoryBackfill Backfill { get; }

        public FirmsHistoryStore Store { get; }

        public void Dispose() => _client.Dispose();
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public ConcurrentQueue<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Paths.Enqueue(request.RequestUri!.AbsolutePath);
            return Task.FromResult(respond(request));
        }
    }
}
