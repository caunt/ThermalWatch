using System.Collections.Immutable;

namespace ThermalWatch.Core;

/// <summary>An immutable view of the retained daily FIRMS history and its completeness.</summary>
/// <param name="GeneratedAtUtc">The history publication time.</param>
/// <param name="CompletedDayCount">The number of completed dates in the baseline.</param>
/// <param name="RetainedFromDate">The oldest retained UTC date.</param>
/// <param name="RetainedThroughDate">Today's retained live UTC date.</param>
/// <param name="FromDate">The first date included in this view.</param>
/// <param name="ToDate">The last date included in this view.</param>
/// <param name="IsReady">Whether every completed baseline slice is complete and fresh.</param>
/// <param name="IsPartiallyStale">Whether any retained date is incomplete or stale.</param>
/// <param name="Days">The selected daily observations and raw clusters.</param>
public sealed record FirmsHistory(
    DateTimeOffset GeneratedAtUtc,
    int CompletedDayCount,
    DateOnly RetainedFromDate,
    DateOnly RetainedThroughDate,
    DateOnly FromDate,
    DateOnly ToDate,
    bool IsReady,
    bool IsPartiallyStale,
    ImmutableArray<FirmsHistoryDay> Days
)
{
    /// <summary>Selects an inclusive range from the retained immutable daily history.</summary>
    /// <param name="fromDate">The first selected UTC date.</param>
    /// <param name="toDate">The last selected UTC date.</param>
    public FirmsHistory Select(DateOnly fromDate, DateOnly toDate)
    {
        return this with
        {
            FromDate = fromDate,
            ToDate = toDate,
            Days = [.. Days.Where(day => day.Date >= fromDate && day.Date <= toDate)]
        };
    }
}
