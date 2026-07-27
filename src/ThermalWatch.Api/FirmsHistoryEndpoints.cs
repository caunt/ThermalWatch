using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ThermalWatch.Core;

namespace ThermalWatch.Api;

public static class FirmsHistoryEndpoints
{
    public static IEndpointRouteBuilder MapFirmsHistory(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(pattern: "/api/history", GetHistory);
        return endpoints;
    }

    private static IResult GetHistory(HttpRequest request, FirmsHistoryStore store)
    {
        FirmsHistory history = store.Current;
        if (!FirmsHistoryQuery.TryParse(
                request.Query,
                history,
                out DateOnly fromDate,
                out DateOnly toDate,
                out string? error))
        {
            return Results.BadRequest(new { error });
        }

        return Results.Ok(history.Select(fromDate, toDate));
    }
}
