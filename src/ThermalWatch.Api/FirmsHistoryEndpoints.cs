using ThermalWatch.Core;

namespace ThermalWatch.Api;

internal static class FirmsHistoryEndpoints
{
    public static IEndpointRouteBuilder MapFirmsHistory(this IEndpointRouteBuilder endpoints)
    {
        RouteHandlerBuilder history = endpoints.MapGet(pattern: "/api/history", GetHistory);
        history.Add(static endpoint => endpoint.DisplayName = "FIRMS retained daily history");

        return endpoints;
    }

    private static IResult GetHistory(HttpRequest request, FirmsHistoryStore store)
    {
        FirmsHistory history = store.Current;

        return !FirmsHistoryQuery.TryParse(request.Query, history, out DateOnly fromDate, out DateOnly toDate, out string? error)
            ? Results.BadRequest(new { error })
            : Results.Ok(history.Select(fromDate, toDate));
    }
}
