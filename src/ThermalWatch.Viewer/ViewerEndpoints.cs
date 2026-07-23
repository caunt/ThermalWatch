using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;
using ThermalWatch.Core;

namespace ThermalWatch.Viewer;

public static class ViewerEndpoints
{
    public const string ImageryCoverageHeader = "X-ThermalWatch-Imagery-Coverage";

    public static IEndpointRouteBuilder MapThermalWatchViewer(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/viewer/config", (ViewerOptions viewerOptions) => Results.Ok(new
        {
            googleMaps = new
            {
                available = viewerOptions.GoogleMapsApiKey is not null,
                apiKey = viewerOptions.GoogleMapsApiKey
            }
        }));

        endpoints.MapGet(
            "/api/viewer/imagery/gibs/{z:int}/{x:int}/{y:int}.png",
            GetGibsMapTileAsync);

        return endpoints;
    }

    private static async Task<IResult> GetGibsMapTileAsync(
        int z,
        int x,
        int y,
        GibsMapTileClient client,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!GibsMapTileCoordinates.TryCreate(z, x, y, out var coordinates))
        {
            return Results.BadRequest(new
            {
                error = "z must be between 0 and 9, and x and y must address a tile at that zoom level."
            });
        }

        GibsMapTileResult tile;
        try
        {
            tile = await client.GetMapTileAsync(coordinates, cancellationToken);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return Results.Empty;
        }

        var coverage = tile.Coverage.ToString().ToLowerInvariant();
        context.Response.Headers[ImageryCoverageHeader] = coverage;
        context.Response.Headers[HeaderNames.CacheControl] = tile.Coverage == GibsMapTileCoverage.Complete
            ? "public, max-age=300"
            : "no-store";
        return Results.Bytes(tile.PngBytes, "image/png");
    }
}
