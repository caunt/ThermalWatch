using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;

using ThermalWatch.Core;

namespace ThermalWatch.Viewer;

/// <summary>Registers viewer configuration, server-mediated imagery, and notification diagnostics.</summary>
public static class ViewerEndpoints
{
    /// <summary>The response header describing complete, partial, or absent imagery coverage.</summary>
    public const string ImageryCoverageHeader = "X-ThermalWatch-Imagery-Coverage";

    /// <summary>Registers the viewer's public GET routes.</summary>
    public static IEndpointRouteBuilder MapThermalWatchViewer(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteHandlerBuilder configuration = endpoints.MapGet(
            pattern: "/api/viewer/config",
            static (ViewerOptions viewerOptions) => Results.Ok(new { googleMaps = new { available = viewerOptions.GoogleMapsApiKey is not null, apiKey = viewerOptions.GoogleMapsApiKey } })
        );

        configuration.Add(static endpoint => endpoint.DisplayName = "Viewer configuration");

        RouteHandlerBuilder imagery = endpoints.MapGet(pattern: "/api/viewer/imagery/gibs/{z:int}/{x:int}/{y:int}.png", GetGibsMapTileAsync);
        imagery.Add(static endpoint => endpoint.DisplayName = "Viewer GIBS imagery");

        RouteHandlerBuilder eligibleClusters = endpoints.MapGet(pattern: "/api/viewer/eligible-notification-clusters", GetEligibleNotificationClustersAsync);
        eligibleClusters.Add(static endpoint => endpoint.DisplayName = "Viewer eligible notification clusters");

        RouteHandlerBuilder diagnostics = endpoints.MapGet(pattern: "/api/viewer/notification-diagnostics/{anomalyId}", GetNotificationDiagnosticAsync);
        diagnostics.Add(static endpoint => endpoint.DisplayName = "Viewer notification diagnostics");

        return endpoints;
    }

    private static async Task<IResult> GetEligibleNotificationClustersAsync(
        [FromServices] AnomalySnapshotStore snapshotStore,
        [FromServices] NotificationCandidateEngine candidateEngine,
        HttpContext context,
        CancellationToken cancellationToken
    )
    {
        AnomalySnapshot snapshot = snapshotStore.Current;
        EligibleNotificationClusters eligibleClusters;

        try
        {
            eligibleClusters = await candidateEngine.GetEligibleNotificationClustersAsync(snapshot, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return Results.Empty;
        }

        return Results.Ok(eligibleClusters);
    }

    private static async Task<IResult> GetGibsMapTileAsync(
        [FromRoute(Name = "z")] int zoom,
        [FromRoute(Name = "x")] int column,
        [FromRoute(Name = "y")] int row,
        GibsMapTileClient client,
        HttpContext context,
        CancellationToken cancellationToken
    )
    {
        if (!GibsMapTileCoordinates.TryCreate(zoom, column, row, out GibsMapTileCoordinates coordinates))
            return Results.BadRequest(new { error = "z must be between 0 and 9, and x and y must address a tile at that zoom level." });

        GibsMapTileResult tile;

        try
        {
            tile = await client.GetMapTileAsync(coordinates, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return Results.Empty;
        }

        string coverage = tile.Coverage switch
        {
            GibsMapTileCoverage.Complete => "complete",
            GibsMapTileCoverage.Partial => "partial",
            GibsMapTileCoverage.None => "none",
            _ => throw new InvalidOperationException(message: "The imagery provider returned an unknown coverage state.")
        };

        context.Response.Headers[ImageryCoverageHeader] = coverage;
        context.Response.Headers[HeaderNames.CacheControl] = tile.Coverage == GibsMapTileCoverage.Complete
            ? "public, max-age=300"
            : "no-store";

        return Results.Bytes(tile.PngBytes, contentType: "image/png");
    }

    private static async Task<IResult> GetNotificationDiagnosticAsync(
        string anomalyId,
        [FromServices] AnomalySnapshotStore snapshotStore,
        [FromServices] NotificationCandidateEngine candidateEngine,
        HttpContext context,
        CancellationToken cancellationToken
    )
    {
        NotificationDiagnostic? diagnostic;

        try
        {
            diagnostic = await candidateEngine.GetNotificationDiagnosticAsync(snapshotStore.Current, anomalyId, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return Results.Empty;
        }

        return diagnostic is null
            ? Results.NotFound(new { error = "The selected anomaly is not present in the current snapshot." })
            : Results.Ok(diagnostic);
    }
}
