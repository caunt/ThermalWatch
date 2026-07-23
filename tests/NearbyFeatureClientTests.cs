using System.Collections.Immutable;
using System.Net;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ThermalWatch.Core;

namespace ThermalWatch.Tests;

public sealed class NearbyFeatureClientTests
{
    [Fact]
    public async Task FindNearbyAsyncQueriesNamedFeaturesSortsByDistanceAndClampsToFive()
    {
        const string responseJson = """
            {
              "elements": [
                { "type": "node", "id": 1, "lat": 0, "lon": 0.008, "tags": { "name": "Fourth" } },
                { "type": "way", "id": 2, "center": { "lat": 0, "lon": 0.002 }, "tags": { "name": "First" } },
                { "type": "relation", "id": 3, "center": { "lat": 0, "lon": 0.004 }, "tags": { "name": "Second" } },
                { "type": "node", "id": 4, "lat": 0, "lon": 0.006, "tags": { "name": "Third" } },
                { "type": "node", "id": 5, "lat": 0, "lon": 0.010, "tags": { "name": "Fifth" } },
                { "type": "node", "id": 6, "lat": 0, "lon": 0.012, "tags": { "name": "Sixth" } },
                { "type": "node", "id": 7, "lat": 0, "lon": 0.014, "tags": { "name": "Seventh" } },
                { "type": "node", "id": 8, "lat": 0, "lon": 0.030, "tags": { "name": "Outside" } },
                { "type": "way", "id": 9, "tags": { "name": "No center" } },
                { "type": "node", "id": 10, "lat": 0, "lon": 0.001, "tags": {} }
              ]
            }
            """;
        var handler = new RecordingHandler((_, _) => JsonResponse(responseJson));
        using MemoryCache cache = CreateCache();
        using NearbyFeatureClient client = CreateClient(handler, cache);

        ImmutableArray<NearbyFeature> features = await client.FindNearbyAsync(
            Detection(latitude: 0, longitude: 0),
            TestContext.Current.CancellationToken);

        Assert.Equal([2, 3, 4, 1, 5], features.Select(feature => feature.OsmId));
        NearbyFeature first = features[0];
        Assert.Equal("way", first.OsmType);
        Assert.Equal("First", first.Name);
        Assert.Equal("https://www.openstreetmap.org/way/2", first.OpenStreetMapUrl);
        Assert.InRange(first.DistanceKilometers, low: 0.22, high: 0.23);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://overpass.example.test/api/interpreter", handler.RequestUri?.AbsoluteUri);
        Assert.Equal(
            "[out:json][timeout:10];nwr(around:2000,0.000000,0.000000)[\"name\"];out center;",
            DecodeQuery(handler.RequestBody));
    }

    [Fact]
    public async Task FindNearbyAsyncCachesSuccessfulAndFailedLookupsByRoundedCoordinates()
    {
        var successfulHandler = new RecordingHandler((_, _) => JsonResponse(json: "{\"elements\":[]}"));
        using MemoryCache successCache = CreateCache();
        using NearbyFeatureClient successfulClient = CreateClient(successfulHandler, successCache);

        await successfulClient.FindNearbyAsync(
            Detection(latitude: 10.0000001, longitude: 20.0000001),
            TestContext.Current.CancellationToken);
        await successfulClient.FindNearbyAsync(
            Detection(latitude: 10.0000002, longitude: 20.0000002),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, successfulHandler.RequestCount);

        var logger = new CollectingLogger<NearbyFeatureClient>();
        var failedHandler = new RecordingHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using MemoryCache failureCache = CreateCache();
        using NearbyFeatureClient failedClient = CreateClient(failedHandler, failureCache, logger);

        ImmutableArray<NearbyFeature> first = await failedClient.FindNearbyAsync(
            Detection(latitude: 30, longitude: 40),
            TestContext.Current.CancellationToken);
        ImmutableArray<NearbyFeature> second = await failedClient.FindNearbyAsync(
            Detection(latitude: 30, longitude: 40),
            TestContext.Current.CancellationToken);

        Assert.Empty(first);
        Assert.Empty(second);
        Assert.Equal(1, failedHandler.RequestCount);
        Assert.Equal(1, logger.WarningCount);
    }

    [Fact]
    public async Task FindNearbyAsyncTreatsMalformedAndOversizedResponsesAsUnavailable()
    {
        var logger = new CollectingLogger<NearbyFeatureClient>();
        var malformedHandler = new RecordingHandler((_, _) => JsonResponse(json: "{}"));
        using MemoryCache malformedCache = CreateCache();
        using NearbyFeatureClient malformedClient = CreateClient(malformedHandler, malformedCache, logger);

        ImmutableArray<NearbyFeature> malformed = await malformedClient.FindNearbyAsync(
            Detection(latitude: 1, longitude: 1),
            TestContext.Current.CancellationToken);

        var oversizedHandler = new RecordingHandler((_, _) =>
        {
            var content = new ByteArrayContent([]);
            content.Headers.ContentLength = 11 * 1024 * 1024;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using MemoryCache oversizedCache = CreateCache();
        using NearbyFeatureClient oversizedClient = CreateClient(oversizedHandler, oversizedCache, logger);

        ImmutableArray<NearbyFeature> oversized = await oversizedClient.FindNearbyAsync(
            Detection(latitude: 2, longitude: 2),
            TestContext.Current.CancellationToken);

        Assert.Empty(malformed);
        Assert.Empty(oversized);
        Assert.Equal(2, logger.WarningCount);
    }

    [Fact]
    public async Task FindNearbyAsyncSerializesRequestsAndPropagatesCallerCancellation()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            if (firstStarted.TrySetResult())
                await releaseFirst.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            return await JsonResponse(json: "{\"elements\":[]}").ConfigureAwait(false);
        });
        using MemoryCache cache = CreateCache();
        using NearbyFeatureClient client = CreateClient(handler, cache);

        Task<ImmutableArray<NearbyFeature>> first = client.FindNearbyAsync(
            Detection(latitude: 10, longitude: 10),
            TestContext.Current.CancellationToken);
        await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Task<ImmutableArray<NearbyFeature>> second = client.FindNearbyAsync(
            Detection(latitude: 20, longitude: 20),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.RequestCount);
        releaseFirst.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(1, handler.MaximumConcurrency);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.FindNearbyAsync(
            Detection(latitude: 30, longitude: 30),
            cancellation.Token));
    }

    private static NearbyFeatureClient CreateClient(
        HttpMessageHandler handler,
        IMemoryCache cache,
        ILogger<NearbyFeatureClient>? logger = null)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new(uriString: "https://overpass.example.test/api/")
        };
        return new(httpClient, cache, logger ?? NullLogger<NearbyFeatureClient>.Instance);
    }

    private static MemoryCache CreateCache() =>
        new(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });

    private static Task<HttpResponseMessage> JsonResponse(string json) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, mediaType: "application/json")
        });

    private static string DecodeQuery(string? requestBody)
    {
        Assert.NotNull(requestBody);
        Assert.StartsWith("data=", requestBody, StringComparison.Ordinal);
        return Uri.UnescapeDataString(requestBody[5..].Replace(oldChar: '+', newChar: ' '));
    }

    private static Anomaly Detection(double latitude, double longitude) =>
        new(
            Id: $"{latitude},{longitude}",
            CountryCode: "UKR",
            Source: "VIIRS_SNPP_NRT",
            Satellite: "N",
            Instrument: "VIIRS",
            latitude,
            longitude,
            new(year: 2026, month: 7, day: 23, hour: 12, minute: 0, second: 0, TimeSpan.Zero),
            DayNight: "D",
            BrightnessKelvin: 330,
            SecondaryBrightnessKelvin: 300,
            FrpMegawatts: 100,
            ScanKilometers: 0.4,
            TrackKilometers: 0.4,
            ConfidenceRaw: "n",
            ConfidencePercent: null,
            ConfidenceCategory: "nominal",
            Version: "2.0NRT",
            GoogleMapsUrl: "https://example.test/location");

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respondAsync) : HttpMessageHandler
    {
        private int _activeRequests;
        private int _maximumConcurrency;
        private int _requestCount;

        public HttpMethod? Method { get; private set; }

        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public int RequestCount => Volatile.Read(ref _requestCount);

        public string? RequestBody { get; private set; }

        public Uri? RequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            int activeRequests = Interlocked.Increment(ref _activeRequests);
            RecordMaximumConcurrency(activeRequests);
            Interlocked.Increment(ref _requestCount);
            try
            {
                Method = request.Method;
                RequestUri = request.RequestUri;
                RequestBody = request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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

    private sealed class CollectingLogger<loggerType> : ILogger<loggerType>
    {
        private int _warningCount;

        public int WarningCount => Volatile.Read(ref _warningCount);

        public IDisposable? BeginScope<stateType>(stateType state)
            where stateType : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<stateType>(
            LogLevel logLevel,
            EventId eventId,
            stateType state,
            Exception? exception,
            Func<stateType, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Interlocked.Increment(ref _warningCount);
        }
    }
}
