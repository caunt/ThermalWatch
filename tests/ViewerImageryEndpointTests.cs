using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StbImageWriteSharp;
using ThermalWatch.Core;
using ThermalWatch.Viewer;

namespace ThermalWatch.Tests;

public sealed class ViewerImageryEndpointTests
{
    [Fact]
    public async Task Endpoint_ReturnsCompletePngWithCoverageAndCacheHeaders()
    {
        var handler = new SolidTileHandler(CreateJpeg(45, 75, 105));
        await using var app = await CreateAppAsync(handler);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(
            "/api/viewer/imagery/gibs/0/0/0.png",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            "complete",
            Assert.Single(response.Headers.GetValues(ViewerEndpoints.ImageryCoverageHeader)));
        Assert.True(response.Headers.CacheControl?.Public);
        Assert.Equal(TimeSpan.FromMinutes(5), response.Headers.CacheControl?.MaxAge);
        Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Endpoint_ReturnsNoStoreTransparentTileForUnavailableCoverage()
    {
        var handler = new SolidTileHandler(CreateJpeg(0, 0, 0));
        await using var app = await CreateAppAsync(handler);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(
            "/api/viewer/imagery/gibs/0/0/0.png",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "none",
            Assert.Single(response.Headers.GetValues(ViewerEndpoints.ImageryCoverageHeader)));
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(5, handler.RequestCount);
    }

    [Fact]
    public async Task Endpoint_RejectsCoordinatesOutsideTheZoomMatrixWithoutCallingGibs()
    {
        var handler = new SolidTileHandler(CreateJpeg(45, 75, 105));
        await using var app = await CreateAppAsync(handler);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(
            "/api/viewer/imagery/gibs/2/4/0.png",
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"error\"", body, StringComparison.Ordinal);
        Assert.Equal(0, handler.RequestCount);
    }

    private static async Task<WebApplication> CreateAppAsync(HttpMessageHandler handler)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddMemoryCache(options => options.SizeLimit = 64 * 1024 * 1024);
        builder.Services.AddSingleton(new ViewerOptions(null));
        builder.Services.AddSingleton(serviceProvider => new GibsMapTileClient(
            new HttpClient(handler)
            {
                BaseAddress = new("https://gibs.example.test/")
            },
            serviceProvider.GetRequiredService<IMemoryCache>(),
            NullLogger<GibsMapTileClient>.Instance));

        var app = builder.Build();
        app.MapThermalWatchViewer();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static byte[] CreateJpeg(byte red, byte green, byte blue)
    {
        const int size = 256;
        var bytes = new byte[size * size * 3];
        for (var offset = 0; offset < bytes.Length; offset += 3)
        {
            bytes[offset] = red;
            bytes[offset + 1] = green;
            bytes[offset + 2] = blue;
        }

        using var stream = new MemoryStream();
        new ImageWriter().WriteJpg(
            bytes,
            size,
            size,
            StbImageWriteSharp.ColorComponents.RedGreenBlue,
            stream,
            100);
        return stream.ToArray();
    }

    private sealed class SolidTileHandler(byte[] bytes) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult(response);
        }
    }
}
