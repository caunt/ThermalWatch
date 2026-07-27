using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using ThermalWatch.Core;

namespace ThermalWatch.Api;

internal static class FirmsHistoryQuery
{
    public static bool TryParse(
        IQueryCollection query,
        FirmsHistory history,
        out DateOnly fromDate,
        out DateOnly toDate,
        out string? error)
    {
        fromDate = history.RetainedFromDate;
        toDate = history.RetainedThroughDate;
        error = null;

        if (!TryParseDate(query, name: "from", fromDate, out fromDate)
            || !TryParseDate(query, name: "to", toDate, out toDate))
        {
            error = "from and to must each contain one UTC date in YYYY-MM-DD format.";
            return false;
        }

        if (fromDate < history.RetainedFromDate || toDate > history.RetainedThroughDate)
        {
            error = "from and to must be within the retained FIRMS history range.";
            return false;
        }

        int requestedDayCount = toDate.DayNumber - fromDate.DayNumber + 1;
        if (requestedDayCount is < 1 or > FirmsHistoryStore.RetainedDayCount)
        {
            error = $"from must not be later than to, and the range must not exceed {FirmsHistoryStore.RetainedDayCount.ToString(CultureInfo.InvariantCulture)} days.";
            return false;
        }

        return true;
    }

    private static bool TryParseDate(
        IQueryCollection query,
        string name,
        DateOnly defaultValue,
        out DateOnly value)
    {
        value = defaultValue;
        if (!query.TryGetValue(name, out StringValues values))
            return true;

        return values.Count == 1
            && values[0] is { } candidate
            && DateOnly.TryParseExact(
                candidate,
                format: "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out value);
    }
}
