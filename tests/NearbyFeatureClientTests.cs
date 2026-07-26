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
    public async Task FindNearbyAsyncQueriesFilteredNamedFeaturesRanksByTagCountAndAppliesCallerLimit()
    {
        const string responseJson = """
            {
              "elements": [
                { "type": "node", "id": 1, "lat": 0, "lon": 0.008, "tags": { "name": "Most tagged", "man_made": "works", "industrial": "oil", "operator": "Operator", "website": "https://example.test" } },
                { "type": "way", "id": 2, "center": { "lat": 0, "lon": 0.002 }, "tags": { "name": "Nearest" } },
                { "type": "relation", "id": 3, "center": { "lat": 0, "lon": 0.004 }, "tags": { "name": "Four tags", "landuse": "industrial", "operator": "Operator", "source": "survey" } },
                { "type": "node", "id": 4, "lat": 0, "lon": 0.006, "tags": { "name": "Three tags", "man_made": "works", "product": "cement" } },
                { "type": "node", "id": 5, "lat": 0, "lon": 0.010, "tags": { "name": "Two tags nearer", "industrial": "warehouse" } },
                { "type": "node", "id": 6, "lat": 0, "lon": 0.012, "tags": { "name": "Two tags farther", "industrial": "warehouse" } },
                { "type": "node", "id": 7, "lat": 0, "lon": 0.014, "tags": { "name": "One tag farther" } },
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
            CreateAnomaly(latitude: 0, longitude: 0),
            maximumResults: 5,
            TestContext.Current.CancellationToken);
        ImmutableArray<NearbyFeature> expanded = await client.FindNearbyAsync(
            CreateAnomaly(latitude: 0, longitude: 0),
            maximumResults: 25,
            TestContext.Current.CancellationToken);

        Assert.Equal([1, 3, 4, 5, 6], features.Select(feature => feature.OsmId));
        Assert.Equal([1, 3, 4, 5, 6, 2, 7], expanded.Select(feature => feature.OsmId));
        NearbyFeature first = features[0];
        Assert.Equal("node", first.OsmType);
        Assert.Equal("Most tagged", first.Name);
        Assert.Equal("https://www.openstreetmap.org/node/1", first.OpenStreetMapUrl);
        Assert.InRange(first.DistanceKilometers, low: 0.88, high: 0.90);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://overpass.example.test/api/interpreter", handler.RequestUri?.AbsoluteUri);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(
            "[out:json][timeout:10];nwr(around:2000,0.000000,0.000000)[\"name\"][!\"highway\"][!\"railway\"][\"type\"!=\"public_transport\"];out center;is_in(0.000000,0.000000)->.containing;area.containing[\"name\"][\"place\"~\"^(city|town|village)$\"];out tags;",
            DecodeQuery(handler.RequestBody));
    }

    [Fact]
    public async Task FindContextAsyncSelectsMostLocalContainingSettlementAndPrefersEnglishName()
    {
        const string responseJson = """
            {
              "elements": [
                { "type": "area", "id": 1, "tags": { "name": "Місто", "name:en": "Outer City", "place": "city", "admin_level": "8" } },
                { "type": "area", "id": 2, "tags": { "name": "Native Town", "place": "town", "admin_level": "9" } },
                { "type": "area", "id": 3, "tags": { "name": "Native Village", "name:en": "English Village", "place": "village", "admin_level": "9" } },
                { "type": "area", "id": 4, "tags": { "name": "Hamlet", "place": "hamlet", "admin_level": "10" } },
                { "type": "node", "id": 5, "lat": 0, "lon": 0.001, "tags": { "name": "Nearby feature" } }
              ]
            }
            """;
        var handler = new RecordingHandler((_, _) => JsonResponse(responseJson));
        using MemoryCache cache = CreateCache();
        using NearbyFeatureClient client = CreateClient(handler, cache);

        NearbyMappedContext context = await client.FindContextAsync(
            CreateAnomaly(latitude: 0, longitude: 0),
            maximumResults: 5,
            TestContext.Current.CancellationToken);

        Assert.Equal("English Village", context.SettlementName);
        Assert.Equal(5, Assert.Single(context.NearbyFeatures).OsmId);
    }

    [Fact]
    public async Task FindContextAsyncOmitsSettlementWithoutSupportedContainingBoundary()
    {
        const string responseJson = """
            {
              "elements": [
                { "type": "area", "id": 1, "tags": { "name": "District", "boundary": "administrative", "admin_level": "6" } },
                { "type": "area", "id": 2, "tags": { "name": "Hamlet", "place": "hamlet", "admin_level": "10" } },
                { "type": "node", "id": 3, "lat": 0, "lon": 0.001, "tags": { "name": "Nearby feature" } }
              ]
            }
            """;
        var handler = new RecordingHandler((_, _) => JsonResponse(responseJson));
        using MemoryCache cache = CreateCache();
        using NearbyFeatureClient client = CreateClient(handler, cache);

        NearbyMappedContext context = await client.FindContextAsync(
            CreateAnomaly(latitude: 0, longitude: 0),
            maximumResults: 5,
            TestContext.Current.CancellationToken);

        Assert.Null(context.SettlementName);
        Assert.Single(context.NearbyFeatures);
    }

    [Fact]
    public async Task FindContextAsyncFallsBackToNativeSettlementName()
    {
        const string responseJson = """
            {
              "elements": [
                { "type": "area", "id": 1, "tags": { "name": "  Рідне місто  ", "name:en": " ", "place": "city", "admin_level": "8" } }
              ]
            }
            """;
        var handler = new RecordingHandler((_, _) => JsonResponse(responseJson));
        using MemoryCache cache = CreateCache();
        using NearbyFeatureClient client = CreateClient(handler, cache);

        NearbyMappedContext context = await client.FindContextAsync(
            CreateAnomaly(latitude: 0, longitude: 0),
            maximumResults: 5,
            TestContext.Current.CancellationToken);

        Assert.Equal("Рідне місто", context.SettlementName);
    }

    [Fact]
    public async Task FindNearbyAsyncExcludesBlacklistedTagsBeforeLimit()
    {
        const string responseJson = """
            {
              "elements": [
                {
                  "type": "relation",
                  "id": 4586917,
                  "center": { "lat": 0, "lon": 0.001 },
                  "tags": {
                    "name": "Автобус № 388: Звёздная улица => Ростовская улица, 25",
                    "ref": "388",
                    "route": "bus",
                    "type": "route"
                  }
                },
                {
                  "type": "relation",
                  "id": 4586918,
                  "center": { "lat": 0, "lon": 0.002 },
                  "tags": {
                    "name": "Автобус № 388: Славянка, Ростовская улица => Звёздная улица",
                    "ref": "388",
                    "route": "bus",
                    "type": "route"
                  }
                },
                { "type": "node", "id": 3, "lat": 0, "lon": 0.003, "tags": { "name": "Bus stop", "highway": "bus_stop" } },
                { "type": "relation", "id": 4, "center": { "lat": 0, "lon": 0.004 }, "tags": { "name": "Train route", "route": "train", "type": "route" } },
                { "type": "way", "id": 9, "center": { "lat": 0, "lon": 0.004 }, "tags": { "name": "River", "waterway": "river" } },
                { "type": "node", "id": 10, "lat": 0, "lon": 0.004, "tags": { "name": "Waterfall", "waterway": "waterfall", "tourism": "attraction" } },
                { "type": "node", "id": 5, "lat": 0, "lon": 0.005, "tags": { "name": "Third retained" } },
                { "type": "node", "id": 6, "lat": 0, "lon": 0.006, "tags": { "name": "Fourth retained" } },
                { "type": "node", "id": 7, "lat": 0, "lon": 0.007, "tags": { "name": "Fifth retained" } },
                { "type": "node", "id": 8, "lat": 0, "lon": 0.008, "tags": { "name": "Beyond limit" } }
              ]
            }
            """;
        var handler = new RecordingHandler((_, _) => JsonResponse(responseJson));
        using MemoryCache cache = CreateCache();
        using NearbyFeatureClient client = CreateClient(handler, cache);

        ImmutableArray<NearbyFeature> features = await client.FindNearbyAsync(
            CreateAnomaly(latitude: 0, longitude: 0),
            maximumResults: 5,
            TestContext.Current.CancellationToken);

        Assert.Equal([4, 3, 5, 6, 7], features.Select(feature => feature.OsmId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(26)]
    public async Task FindNearbyAsyncRejectsUnsupportedMaximumResultCounts(int maximumResults)
    {
        var handler = new RecordingHandler((_, _) => JsonResponse(json: "{\"elements\":[]}"));
        using MemoryCache cache = CreateCache();
        using NearbyFeatureClient client = CreateClient(handler, cache);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.FindNearbyAsync(
            CreateAnomaly(latitude: 0, longitude: 0),
            maximumResults,
            TestContext.Current.CancellationToken));

        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task FindNearbyAsyncCachesSuccessfulAndFailedLookupsByRoundedCoordinates()
    {
        var successfulHandler = new RecordingHandler((_, _) => JsonResponse(json: "{\"elements\":[]}"));
        using MemoryCache successCache = CreateCache();
        using NearbyFeatureClient successfulClient = CreateClient(successfulHandler, successCache);

        await successfulClient.FindNearbyAsync(
            CreateAnomaly(latitude: 10.0000001, longitude: 20.0000001),
            maximumResults: 5,
            TestContext.Current.CancellationToken);
        await successfulClient.FindNearbyAsync(
            CreateAnomaly(latitude: 10.0000002, longitude: 20.0000002),
            maximumResults: 5,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, successfulHandler.RequestCount);

        var logger = new CollectingLogger<NearbyFeatureClient>();
        var failedHandler = new RecordingHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using MemoryCache failureCache = CreateCache();
        using NearbyFeatureClient failedClient = CreateClient(failedHandler, failureCache, logger);

        ImmutableArray<NearbyFeature> first = await failedClient.FindNearbyAsync(
            CreateAnomaly(latitude: 30, longitude: 40),
            maximumResults: 5,
            TestContext.Current.CancellationToken);
        ImmutableArray<NearbyFeature> second = await failedClient.FindNearbyAsync(
            CreateAnomaly(latitude: 30, longitude: 40),
            maximumResults: 5,
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
            CreateAnomaly(latitude: 1, longitude: 1),
            maximumResults: 5,
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
            CreateAnomaly(latitude: 2, longitude: 2),
            maximumResults: 5,
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
            CreateAnomaly(latitude: 10, longitude: 10),
            maximumResults: 5,
            TestContext.Current.CancellationToken);
        await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Task<ImmutableArray<NearbyFeature>> second = client.FindNearbyAsync(
            CreateAnomaly(latitude: 20, longitude: 20),
            maximumResults: 5,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.RequestCount);
        releaseFirst.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(1, handler.MaximumConcurrency);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.FindNearbyAsync(
            CreateAnomaly(latitude: 30, longitude: 30),
            maximumResults: 5,
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

    private static Anomaly CreateAnomaly(double latitude, double longitude) =>
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
