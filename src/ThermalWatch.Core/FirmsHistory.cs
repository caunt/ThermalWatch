using System.Collections.Immutable;

namespace ThermalWatch.Core;

public sealed record FirmsHistory(
    DateTimeOffset GeneratedAtUtc,
    int CompletedDayCount,
    DateOnly RetainedFromDate,
    DateOnly RetainedThroughDate,
    DateOnly FromDate,
    DateOnly ToDate,
    bool IsReady,
    bool IsPartiallyStale,
    ImmutableArray<FirmsHistoryDay> Days)
{
    public FirmsHistory Select(DateOnly fromDate, DateOnly toDate) =>
        this with
        {
            FromDate = fromDate,
            ToDate = toDate,
            Days = [.. Days.Where(day => day.Date >= fromDate && day.Date <= toDate)]
        };
}
