using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;
using ThermalWatch.Core;

namespace ThermalWatch.Viewer;

public static class ViewerEndpoints
{
    public const string ImageryCoverageHeader = "X-ThermalWatch-Imagery-Coverage";

    public static IEndpointRouteBuilder MapThermalWatchViewer(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(pattern: "/api/viewer/config", (ViewerOptions viewerOptions) => Results.Ok(new
        {
            googleMaps = new
            {
                available = viewerOptions.GoogleMapsApiKey is not null,
                apiKey = viewerOptions.GoogleMapsApiKey
            }
        }));

        endpoints.MapGet(
            pattern: "/api/viewer/imagery/gibs/{z:int}/{x:int}/{y:int}.png",
            GetGibsMapTileAsync);
        endpoints.MapGet(
            pattern: "/api/viewer/eligible-notification-clusters",
            GetEligibleNotificationClustersAsync);
        endpoints.MapGet(
            pattern: "/api/viewer/notification-diagnostics/{anomalyId}",
            GetNotificationDiagnosticAsync);

        return endpoints;
    }

    private static async Task<IResult> GetEligibleNotificationClustersAsync(
        [FromServices] AnomalySnapshotStore snapshotStore,
        [FromServices] NotificationCandidateEngine candidateEngine,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        AnomalySnapshot snapshot = snapshotStore.Current;
        EligibleNotificationClusters eligibleClusters;
        try
        {
            eligibleClusters = await candidateEngine.GetEligibleNotificationClustersAsync(
                snapshot,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return Results.Empty;
        }

        return Results.Ok(eligibleClusters);
    }

    private static async Task<IResult> GetNotificationDiagnosticAsync(
        string anomalyId,
        [FromServices] AnomalySnapshotStore snapshotStore,
        [FromServices] NotificationCandidateEngine candidateEngine,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        NotificationDiagnostic? diagnostic;
        try
        {
            diagnostic = await candidateEngine.GetNotificationDiagnosticAsync(
                snapshotStore.Current,
                anomalyId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return Results.Empty;
        }

        return diagnostic is null
            ? Results.NotFound(new { error = "The selected anomaly is not present in the current snapshot." })
            : Results.Ok(diagnostic);
    }

    private static async Task<IResult> GetGibsMapTileAsync(
        int z,
        int x,
        int y,
        GibsMapTileClient client,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!GibsMapTileCoordinates.TryCreate(z, x, y, out GibsMapTileCoordinates coordinates))
        {
            return Results.BadRequest(new
            {
                error = "z must be between 0 and 9, and x and y must address a tile at that zoom level."
            });
        }

        GibsMapTileResult tile;
        try
        {
            tile = await client.GetMapTileAsync(coordinates, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return Results.Empty;
        }

        string coverage = tile.Coverage.ToString().ToLowerInvariant();
        context.Response.Headers[ImageryCoverageHeader] = coverage;
        context.Response.Headers[HeaderNames.CacheControl] = tile.Coverage == GibsMapTileCoverage.Complete
            ? "public, max-age=300"
            : "no-store";
        return Results.Bytes(tile.PngBytes, contentType: "image/png");
    }
}
