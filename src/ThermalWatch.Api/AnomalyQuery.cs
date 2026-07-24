using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using ThermalWatch.Core;

namespace ThermalWatch.Api;

public sealed record AnomalyQuery(
    FrozenSet<string>? CountryCodes,
    FrozenSet<string>? Sources,
    FrozenSet<string>? Satellites,
    DateTimeOffset? Since,
    string? DayNight)
{
    private static readonly FrozenSet<string> s_allowedParameters = new[]
    {
        "country", "source", "satellite", "since", "dayNight"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static bool TryParse(
        IQueryCollection query,
        DateTimeOffset activeWindowCutoff,
        out AnomalyQuery? parsed,
        out string? error)
    {
        parsed = null;
        error = null;

        string? unknown = query.Keys.FirstOrDefault(key => !s_allowedParameters.Contains(key));
        if (unknown is not null)
        {
            error = $"Unknown query parameter '{unknown}'.";
            return false;
        }

        if (!TryParseList(query, name: "country", value => value.ToUpperInvariant(), out FrozenSet<string>? countryCodes, out error)
            || countryCodes is not null && countryCodes.Any(countryCode => !CountryCatalog.IsValid(countryCode)))
        {
            error ??= "country must contain comma-separated ISO alpha-3 country codes.";
            return false;
        }

        if (!TryParseList(query, name: "source", value => value.ToUpperInvariant(), out FrozenSet<string>? sources, out error)
            || sources is not null && sources.Any(source => !FirmsSources.All.Contains(source, StringComparer.Ordinal)))
        {
            error ??= "source must contain supported FIRMS source IDs.";
            return false;
        }

        if (!TryParseList(query, name: "satellite", value => value, out FrozenSet<string>? satellites, out error))
            return false;

        if (!TryParseSince(query, activeWindowCutoff, out DateTimeOffset? since, out error)
            || !TryParseDayNight(query, out string? dayNight, out error))
        {
            return false;
        }

        parsed = new(countryCodes, sources, satellites, since, dayNight);
        return true;
    }

    private static bool TryParseSince(
        IQueryCollection query,
        DateTimeOffset activeWindowCutoff,
        out DateTimeOffset? since,
        out string? error)
    {
        since = null;
        error = null;
        if (!query.TryGetValue(key: "since", out StringValues sinceValues))
            return true;

        string value = sinceValues.ToString().Trim();
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset parsedSince)
            || parsedSince.Offset != TimeSpan.Zero)
        {
            error = "since must be an ISO-8601 UTC timestamp.";
            return false;
        }

        if (parsedSince < activeWindowCutoff)
        {
            error = "since cannot be earlier than FIRMS_ACTIVE_WINDOW.";
            return false;
        }

        since = parsedSince;
        return true;
    }

    private static bool TryParseDayNight(
        IQueryCollection query,
        out string? dayNight,
        out string? error)
    {
        dayNight = null;
        error = null;
        if (!query.TryGetValue(key: "dayNight", out StringValues dayNightValues))
            return true;

        dayNight = dayNightValues.ToString().Trim().ToUpperInvariant();
        if (dayNight is "D" or "N")
            return true;

        error = "dayNight must be D or N.";
        return false;
    }

    public ImmutableArray<Anomaly> Apply(IEnumerable<Anomaly> anomalies) =>
    [
        .. anomalies
            .Where(anomaly => CountryCodes is null || CountryCodes.Contains(anomaly.CountryCode))
            .Where(anomaly => Sources is null || Sources.Contains(anomaly.Source))
            .Where(anomaly => Satellites is null || Satellites.Contains(anomaly.Satellite))
            .Where(anomaly => Since is null || anomaly.AcquiredAtUtc >= Since)
            .Where(anomaly => DayNight is null || string.Equals(anomaly.DayNight, DayNight, StringComparison.Ordinal))
            .OrderByDescending(anomaly => anomaly.AcquiredAtUtc)
            .ThenBy(anomaly => anomaly.Id, StringComparer.Ordinal)
    ];

    private static bool TryParseList(
        IQueryCollection query,
        string name,
        Func<string, string> normalize,
        out FrozenSet<string>? values,
        out string? error)
    {
        values = null;
        error = null;

        if (!query.TryGetValue(name, out StringValues queryValues))
            return true;

        string[] entries = queryValues.ToString().Split(',');
        if (entries.Any(string.IsNullOrWhiteSpace))
        {
            error = $"{name} must not contain empty values.";
            return false;
        }

        values = entries
            .Select(entry => normalize(entry.Trim()))
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        return true;
    }
}
