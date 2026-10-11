using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using ThermalWatch.Core;

namespace ThermalWatch.Tests.Core.Places;

/// <summary>Verifies nearby-feature lookup, settlement selection, caching, and failure isolation.</summary>
public sealed class NearbyFeatureClientTests
{
    /// <summary>Provides result limits outside the supported range.</summary>
    public static TheoryData<int> InvalidResultLimits => [0, 26];

    /// <summary>Uses the native settlement name when its English name is blank.</summary>
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

        using var handler = new RecordingHandler((request, cancellationToken) => JsonResponse(responseJson));

        using MemoryCache cache = CreateCache();

        using var httpClient = new HttpClient(handler);

        NearbyFeatureClient client = CreateClient(httpClient, cache);

        NearbyMappedContext context = await client.FindContextAsync(CreateAnomaly(latitude: 0, longitude: 0), maximumResults: 5, TestContext.Current.CancellationToken);

        Assert.Equal(expected: "Рідне місто", context.SettlementName);
    }

    /// <summary>Omits settlement context when no supported containing boundary exists.</summary>
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

        using var handler = new RecordingHandler((request, cancellationToken) => JsonResponse(responseJson));

        using MemoryCache cache = CreateCache();

        using var httpClient = new HttpClient(handler);

        NearbyFeatureClient client = CreateClient(httpClient, cache);

        NearbyMappedContext context = await client.FindContextAsync(CreateAnomaly(latitude: 0, longitude: 0), maximumResults: 5, TestContext.Current.CancellationToken);

        Assert.Null(context.SettlementName);
        Assert.True(context.NearbyFeatures.Length == 1);
    }

    /// <summary>Selects the most local supported settlement and prefers its English name.</summary>
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

        using var handler = new RecordingHandler((request, cancellationToken) => JsonResponse(responseJson));

        using MemoryCache cache = CreateCache();

        using var httpClient = new HttpClient(handler);

        NearbyFeatureClient client = CreateClient(httpClient, cache);

        NearbyMappedContext context = await client.FindContextAsync(CreateAnomaly(latitude: 0, longitude: 0), maximumResults: 5, TestContext.Current.CancellationToken);

        Assert.Equal(expected: "English Village", context.SettlementName);
        Assert.True(context.NearbyFeatures.Length == 1);
        Assert.Equal(expected: 5, context.NearbyFeatures[index: 0].OsmId);
    }

    /// <summary>Caches successful and unavailable results for rounded coordinates.</summary>
    [Fact]
    public async Task FindNearbyAsyncCachesSuccessfulAndFailedLookupsByRoundedCoordinates()
    {
        using var successfulHandler = new RecordingHandler((request, cancellationToken) => JsonResponse(json: "{\"elements\":[]}"));

        using MemoryCache successCache = CreateCache();

        using var successfulHttpClient = new HttpClient(successfulHandler);

        NearbyFeatureClient successfulClient = CreateClient(successfulHttpClient, successCache);

        ImmutableArray<NearbyFeature> successfulFirst = await successfulClient.FindNearbyAsync(CreateAnomaly(latitude: 10.0000001, longitude: 20.0000001), maximumResults: 5, TestContext.Current.CancellationToken);
        ImmutableArray<NearbyFeature> successfulSecond = await successfulClient.FindNearbyAsync(CreateAnomaly(latitude: 10.0000002, longitude: 20.0000002), maximumResults: 5, TestContext.Current.CancellationToken);
        Assert.True(successfulFirst.IsEmpty);
        Assert.True(successfulSecond.IsEmpty);

        Assert.Equal(expected: 1, successfulHandler.RequestCount);

        var logger = new CollectingLogger<NearbyFeatureClient>();

        using var failedHandler = new RecordingHandler((request, cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        using MemoryCache failureCache = CreateCache();

        using var failedHttpClient = new HttpClient(failedHandler);

        NearbyFeatureClient failedClient = CreateClient(failedHttpClient, failureCache, logger);

        ImmutableArray<NearbyFeature> first = await failedClient.FindNearbyAsync(CreateAnomaly(latitude: 30, longitude: 40), maximumResults: 5, TestContext.Current.CancellationToken);

        ImmutableArray<NearbyFeature> second = await failedClient.FindNearbyAsync(CreateAnomaly(latitude: 30, longitude: 40), maximumResults: 5, TestContext.Current.CancellationToken);

        Assert.True(first.IsEmpty);
        Assert.True(second.IsEmpty);
        Assert.Equal(expected: 1, failedHandler.RequestCount);
        Assert.Equal(expected: 1, logger.WarningCount);
    }

    /// <summary>Filters excluded map-feature tags before applying the result limit.</summary>
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

        using var handler = new RecordingHandler((request, cancellationToken) => JsonResponse(responseJson));

        using MemoryCache cache = CreateCache();

        using var httpClient = new HttpClient(handler);

        NearbyFeatureClient client = CreateClient(httpClient, cache);

        ImmutableArray<NearbyFeature> features = await client.FindNearbyAsync(CreateAnomaly(latitude: 0, longitude: 0), maximumResults: 5, TestContext.Current.CancellationToken);

        Assert.Equal([4, 3, 5, 6, 7], features.Select(feature => feature.OsmId));
    }

    /// <summary>Ranks named map features by tag count and applies each caller's requested limit.</summary>
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

        using var handler = new RecordingHandler((request, cancellationToken) => JsonResponse(responseJson));

        using MemoryCache cache = CreateCache();

        using var httpClient = new HttpClient(handler);

        NearbyFeatureClient client = CreateClient(httpClient, cache);

        ImmutableArray<NearbyFeature> features = await client.FindNearbyAsync(CreateAnomaly(latitude: 0, longitude: 0), maximumResults: 5, TestContext.Current.CancellationToken);

        ImmutableArray<NearbyFeature> expanded = await client.FindNearbyAsync(CreateAnomaly(latitude: 0, longitude: 0), maximumResults: 25, TestContext.Current.CancellationToken);

        Assert.Equal([1, 3, 4, 5, 6], features.Select(feature => feature.OsmId));
        Assert.Equal([1, 3, 4, 5, 6, 2, 7], expanded.Select(feature => feature.OsmId));
        NearbyFeature first = features[index: 0];
        Assert.Equal(expected: "node", first.OsmType);
        Assert.Equal(expected: "Most tagged", first.Name);
        Assert.Equal(expected: "https://www.openstreetmap.org/node/1", first.OpenStreetMapUrl.AbsoluteUri);
        Assert.InRange(first.DistanceKilometers, low: 0.88, high: 0.90);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(expected: "https://overpass.example.test/api/interpreter", handler.RequestUri?.AbsoluteUri);
        Assert.Equal(expected: 1, handler.RequestCount);
        Assert.Equal(
            expected: "[out:json][timeout:10];nwr(around:2000,0.000000,0.000000)[\"name\"][!\"highway\"][!\"railway\"][\"type\"!=\"public_transport\"];out center;is_in(0.000000,0.000000)->.containing;area.containing[\"name\"][\"place\"~\"^(city|town|village)$\"];out tags;",
            DecodeQuery(handler.RequestBody)
        );
    }

    /// <summary>Rejects unsupported result limits without making a provider request.</summary>
    [Theory]
    [MemberData(nameof(InvalidResultLimits))]
    public async Task FindNearbyAsyncRejectsUnsupportedMaximumResultCounts(int maximumResults)
    {
        using var handler = new RecordingHandler((request, cancellationToken) => JsonResponse(json: "{\"elements\":[]}"));

        using MemoryCache cache = CreateCache();

        using var httpClient = new HttpClient(handler);

        NearbyFeatureClient client = CreateClient(httpClient, cache);

        ArgumentOutOfRangeException exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.FindNearbyAsync(CreateAnomaly(latitude: 0, longitude: 0), maximumResults, TestContext.Current.CancellationToken));
        Assert.Equal(expected: "maximumResults", exception.ParamName);

        Assert.Equal(expected: 0, handler.RequestCount);
    }

    /// <summary>Serializes provider requests and propagates cancellation.</summary>
    [Fact]
    public async Task FindNearbyAsyncSerializesRequestsAndPropagatesCallerCancellation()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var handler = new RecordingHandler(
            async (request, cancellationToken) =>
        {
            if (firstStarted.TrySetResult())
                await releaseFirst.Task.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            return await JsonResponse(json: "{\"elements\":[]}").ConfigureAwait(continueOnCapturedContext: false);
        }
        );

        using MemoryCache cache = CreateCache();

        using var httpClient = new HttpClient(handler);

        NearbyFeatureClient client = CreateClient(httpClient, cache);

        Task<ImmutableArray<NearbyFeature>> first = client.FindNearbyAsync(CreateAnomaly(latitude: 10, longitude: 10), maximumResults: 5, TestContext.Current.CancellationToken);

        await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        Task<ImmutableArray<NearbyFeature>> second = client.FindNearbyAsync(CreateAnomaly(latitude: 20, longitude: 20), maximumResults: 5, TestContext.Current.CancellationToken);

        Assert.Equal(expected: 1, handler.RequestCount);
        releaseFirst.SetResult();
        ImmutableArray<NearbyFeature>[] completed = await Task.WhenAll(first, second);

        foreach (ImmutableArray<NearbyFeature> features in completed)
            Assert.True(features.IsEmpty);

        Assert.Equal(expected: 2, handler.RequestCount);
        Assert.Equal(expected: 1, handler.MaximumConcurrency);

        using var cancellation = new CancellationTokenSource();

        await cancellation.CancelAsync();
        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.FindNearbyAsync(CreateAnomaly(latitude: 30, longitude: 30), maximumResults: 5, cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    /// <summary>Treats malformed and oversized responses as unavailable with warnings.</summary>
    [Fact]
    public async Task FindNearbyAsyncTreatsMalformedAndOversizedResponsesAsUnavailable()
    {
        var logger = new CollectingLogger<NearbyFeatureClient>();

        using var malformedHandler = new RecordingHandler((request, cancellationToken) => JsonResponse(json: "{}"));

        using MemoryCache malformedCache = CreateCache();

        using var malformedHttpClient = new HttpClient(malformedHandler);

        NearbyFeatureClient malformedClient = CreateClient(malformedHttpClient, malformedCache, logger);

        ImmutableArray<NearbyFeature> malformed = await malformedClient.FindNearbyAsync(CreateAnomaly(latitude: 1, longitude: 1), maximumResults: 5, TestContext.Current.CancellationToken);

        using var oversizedHandler = new RecordingHandler(
            (request, cancellationToken) =>
        {
            var content = new ByteArrayContent([]);
            content.Headers.ContentLength = 11 * 1024 * 1024;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
        );

        using MemoryCache oversizedCache = CreateCache();

        using var oversizedHttpClient = new HttpClient(oversizedHandler);

        NearbyFeatureClient oversizedClient = CreateClient(oversizedHttpClient, oversizedCache, logger);

        ImmutableArray<NearbyFeature> oversized = await oversizedClient.FindNearbyAsync(CreateAnomaly(latitude: 2, longitude: 2), maximumResults: 5, TestContext.Current.CancellationToken);

        Assert.True(malformed.IsEmpty);
        Assert.True(oversized.IsEmpty);
        Assert.Equal(expected: 2, logger.WarningCount);
    }

    private static Anomaly CreateAnomaly(double latitude, double longitude)
    {
        return new(
            $"{latitude},{longitude}",
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
            new Uri(uriString: "https://example.test/location")
        );
    }

    private static MemoryCache CreateCache()
    {
        return new(new MemoryCacheOptions { SizeLimit = 64 * 1024 * 1024 });
    }

    private static NearbyFeatureClient CreateClient(HttpClient httpClient, IMemoryCache cache, ILogger<NearbyFeatureClient>? logger = null)
    {
        httpClient.BaseAddress = new(uriString: "https://overpass.example.test/api/");

        return new(httpClient, cache, logger ?? NullLogger<NearbyFeatureClient>.Instance);
    }

    private static string DecodeQuery(string? requestBody)
    {
        Assert.NotNull(requestBody);
        Assert.StartsWith(expectedStartString: "data=", requestBody, StringComparison.Ordinal);

        return Uri.UnescapeDataString(requestBody[5..].Replace(oldChar: '+', newChar: ' '));
    }

    private static Task<HttpResponseMessage> JsonResponse([StringSyntax(StringSyntaxAttribute.Json)] string json)
    {
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, mediaType: "application/json") });
    }

    private sealed class CollectingLogger<TLoggerType> : ILogger<TLoggerType>
    {
        private int _warningCount;

        public int WarningCount => Volatile.Read(ref _warningCount);

        public IDisposable? BeginScope<TStateType>(TStateType state)
            where TStateType : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TStateType>(LogLevel logLevel, EventId eventId, TStateType state, Exception? exception, Func<TStateType, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                Assert.True(Interlocked.Increment(ref _warningCount) > 0);
        }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respondAsync) : HttpMessageHandler
    {
        private int _activeRequests;
        private int _maximumConcurrency;
        private int _requestCount;

        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public HttpMethod? Method { get; private set; }

        public string? RequestBody { get; private set; }

        public int RequestCount => Volatile.Read(ref _requestCount);

        public Uri? RequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int activeRequests = Interlocked.Increment(ref _activeRequests);
            RecordMaximumConcurrency(activeRequests);
            Assert.True(Interlocked.Increment(ref _requestCount) > 0);

            try
            {
                Method = request.Method;
                RequestUri = request.RequestUri;
                RequestBody = request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

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
