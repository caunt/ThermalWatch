using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using ThermalWatch.Core;

namespace ThermalWatch.Api;

public sealed record AnomalyQuery(
    FrozenSet<string>? Countries,
    FrozenSet<string>? Sources,
    FrozenSet<string>? Satellites,
    DateTimeOffset? Since,
    string? DayNight)
{
    private static readonly FrozenSet<string> AllowedParameters = new[]
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

        var unknown = query.Keys.FirstOrDefault(key => !AllowedParameters.Contains(key));
        if (unknown is not null)
        {
            error = $"Unknown query parameter '{unknown}'.";
            return false;
        }

        if (!TryParseList(query, "country", value => value.ToUpperInvariant(), out var countries, out error)
            || countries is not null && countries.Any(country => !CountryCatalog.IsValid(country)))
        {
            error ??= "country must contain comma-separated ISO alpha-3 country codes.";
            return false;
        }

        if (!TryParseList(query, "source", value => value.ToUpperInvariant(), out var sources, out error)
            || sources is not null && sources.Any(source => !FirmsSources.All.Contains(source)))
        {
            error ??= "source must contain supported FIRMS source IDs.";
            return false;
        }

        if (!TryParseList(query, "satellite", value => value, out var satellites, out error))
            return false;

        DateTimeOffset? since = null;
        if (query.TryGetValue("since", out var sinceValues))
        {
            var value = sinceValues.ToString().Trim();
            if (!DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsedSince)
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
        }

        string? dayNight = null;
        if (query.TryGetValue("dayNight", out var dayNightValues))
        {
            dayNight = dayNightValues.ToString().Trim().ToUpperInvariant();
            if (dayNight is not ("D" or "N"))
            {
                error = "dayNight must be D or N.";
                return false;
            }
        }

        parsed = new(countries, sources, satellites, since, dayNight);
        return true;
    }

    public ImmutableArray<Anomaly> Apply(IEnumerable<Anomaly> detections) =>
    [
        .. detections
            .Where(detection => Countries is null || Countries.Contains(detection.CountryCode))
            .Where(detection => Sources is null || Sources.Contains(detection.Source))
            .Where(detection => Satellites is null || Satellites.Contains(detection.Satellite))
            .Where(detection => Since is null || detection.AcquiredAtUtc >= Since)
            .Where(detection => DayNight is null || detection.DayNight == DayNight)
            .OrderByDescending(detection => detection.AcquiredAtUtc)
            .ThenBy(detection => detection.Id, StringComparer.Ordinal)
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

        if (!query.TryGetValue(name, out var queryValues))
            return true;

        var entries = queryValues.ToString().Split(',');
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
