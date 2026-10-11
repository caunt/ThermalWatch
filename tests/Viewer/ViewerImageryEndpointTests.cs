using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using StbImageWriteSharp;

using ThermalWatch.Core;
using ThermalWatch.Viewer;

namespace ThermalWatch.Tests.Viewer;

/// <summary>Verifies viewer imagery responses, caching, and coordinate validation.</summary>
public sealed class ViewerImageryEndpointTests
{
    /// <summary>Rejects invalid tile coordinates before contacting GIBS.</summary>
    [Fact]
    public async Task EndpointRejectsCoordinatesOutsideTheZoomMatrixWithoutCallingGibs()
    {
        using var handler = new SolidTileHandler(CreateJpeg(red: 45, green: 75, blue: 105));

        WebApplication app = await CreateAppAsync(handler);

        await using ConfiguredAsyncDisposable applicationLifetime = app.ConfigureAwait(continueOnCapturedContext: true);

        using HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(uriString: "/api/viewer/imagery/gibs/2/4/0.png", UriKind.Relative), TestContext.Current.CancellationToken);

        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expectedSubstring: "\"error\"", body, StringComparison.Ordinal);
        Assert.Equal(expected: 0, handler.RequestCount);
    }

    /// <summary>Returns complete PNG imagery with public caching and coverage metadata.</summary>
    [Fact]
    public async Task EndpointReturnsCompletePngWithCoverageAndCacheHeaders()
    {
        using var handler = new SolidTileHandler(CreateJpeg(red: 45, green: 75, blue: 105));

        WebApplication app = await CreateAppAsync(handler);

        await using ConfiguredAsyncDisposable applicationLifetime = app.ConfigureAwait(continueOnCapturedContext: true);

        using HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(uriString: "/api/viewer/imagery/gibs/0/0/0.png", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected: "image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(expected: "complete", Assert.Single(response.Headers.GetValues(ViewerEndpoints.ImageryCoverageHeader)));
        Assert.True(response.Headers.CacheControl?.Public);
        Assert.Equal(TimeSpan.FromMinutes(minutes: 5), response.Headers.CacheControl?.MaxAge);
        Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(expected: 1, handler.RequestCount);
    }

    /// <summary>Prevents caching when all imagery candidates are unavailable.</summary>
    [Fact]
    public async Task EndpointReturnsNoStoreTransparentTileForUnavailableCoverage()
    {
        using var handler = new SolidTileHandler(CreateJpeg(red: 0, green: 0, blue: 0));

        WebApplication app = await CreateAppAsync(handler);

        await using ConfiguredAsyncDisposable applicationLifetime = app.ConfigureAwait(continueOnCapturedContext: true);

        using HttpClient client = app.GetTestClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri(uriString: "/api/viewer/imagery/gibs/0/0/0.png", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected: "none", Assert.Single(response.Headers.GetValues(ViewerEndpoints.ImageryCoverageHeader)));
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(expected: 10, handler.RequestCount);
    }

    private static async Task<WebApplication> CreateAppAsync(HttpMessageHandler handler)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        IWebHostBuilder webHost = builder.WebHost.UseTestServer();
        webHost = webHost.ConfigureServices(
            services => services = services
            .AddMemoryCache(options => options.SizeLimit = 64 * 1024 * 1024)
            .AddSingleton(new ViewerOptions(GoogleMapsApiKey: null))
            .AddSingleton(serviceProvider => new HttpClient(handler) { BaseAddress = new(uriString: "https://gibs.example.test/") })
            .AddSingleton(
            serviceProvider => new GibsMapTileClient(
                serviceProvider.GetRequiredService<HttpClient>(),
                serviceProvider.GetRequiredService<IMemoryCache>(),
                new FakeTimeProvider(new DateTimeOffset(year: 2026, month: 7, day: 27, hour: 12, minute: 0, second: 0, TimeSpan.Zero)),
                NullLogger<GibsMapTileClient>.Instance
            )
        )
        );

        WebApplication app = builder.Build();
        Assert.NotEmpty(app.MapThermalWatchViewer().DataSources);
        await app.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(continueOnCapturedContext: false);

        return app;
    }

    private static byte[] CreateJpeg(byte red, byte green, byte blue)
    {
        const int size = 256;
        byte[] bytes = new byte[size * size * 3];

        for (int offset = 0; offset < bytes.Length; offset += 3)
        {
            bytes[offset] = red;
            bytes[offset + 1] = green;
            bytes[offset + 2] = blue;
        }

        using var stream = new MemoryStream();

        new ImageWriter().WriteJpg(bytes, size, size, ColorComponents.RedGreenBlue, stream, quality: 100);

        return stream.ToArray();
    }

    private sealed class SolidTileHandler(byte[] bytes) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };

            response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType: "image/jpeg");

            return Task.FromResult(response);
        }
    }
}
