using System.Collections.Immutable;
using System.Globalization;
using Serilog.Events;
using ThermalWatch.Core;
using ThermalWatch.Telegram;
using ThermalWatch.Viewer;

namespace ThermalWatch.Api;

public sealed record ApplicationConfiguration(
    FirmsOptions Firms,
    TelegramOptions Telegram,
    ViewerOptions Viewer,
    LogEventLevel MinimumLogLevel)
{
    public static ApplicationConfiguration FromEnvironment()
    {
        Func<string, string?> get = Environment.GetEnvironmentVariable;
        var mapKey = Required(get, "FIRMS_MAP_KEY");
        if (mapKey.Length != 32 || mapKey.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new ApplicationConfigurationException(
                "FIRMS_MAP_KEY must be a valid 32-character MAP_KEY.");
        }

        var countries = ParseCountries(Required(get, "FIRMS_COUNTRIES"));
        var firms = new FirmsOptions(
            mapKey,
            countries,
            ParseTimeSpan(get, "FIRMS_POLL_INTERVAL", TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10), TimeSpan.FromDays(1)),
            ParseTimeSpan(get, "FIRMS_ACTIVE_WINDOW", TimeSpan.FromHours(24), TimeSpan.FromMinutes(1), TimeSpan.FromHours(24)),
            ParseTimeSpan(get, "FIRMS_REQUEST_TIMEOUT", TimeSpan.FromSeconds(45), TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5)),
            ParseInt(get, "FIRMS_MAX_CONCURRENCY", 4, 1, 32));
        var telegram = TelegramOptions.FromEnvironment(get);
        var viewer = ViewerOptions.FromEnvironment(get);

        if (telegram.SeenRetention < firms.ActiveWindow)
        {
            throw new ApplicationConfigurationException(
                "TELEGRAM_SEEN_RETENTION must be at least FIRMS_ACTIVE_WINDOW.");
        }

        return new(firms, telegram, viewer, ParseLogLevel(get));
    }

    private static ImmutableArray<string> ParseCountries(string value)
    {
        var entries = value.Split(',');
        if (entries.Any(string.IsNullOrWhiteSpace))
            throw new ApplicationConfigurationException("FIRMS_COUNTRIES contains an empty country code.");

        var countries = entries
            .Select(entry => entry.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();

        if (countries.Length == 0)
            throw new ApplicationConfigurationException("FIRMS_COUNTRIES must contain at least one country.");

        var invalid = countries.FirstOrDefault(country => !CountryCatalog.IsValid(country));
        if (invalid is not null)
        {
            throw new ApplicationConfigurationException(
                $"FIRMS_COUNTRIES contains invalid ISO alpha-3 code '{invalid}'.");
        }

        return countries;
    }

    private static string Required(Func<string, string?> get, string name)
    {
        var value = Normalize(get(name));
        return value ?? throw new ApplicationConfigurationException($"{name} is required and cannot be empty.");
    }

    private static int ParseInt(
        Func<string, string?> get,
        string name,
        int defaultValue,
        int minimum,
        int maximum)
    {
        var value = Normalize(get(name));
        if (value is null)
            return defaultValue;

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            && parsed >= minimum
            && parsed <= maximum
                ? parsed
                : throw new ApplicationConfigurationException(
                    $"{name} must be an integer between {minimum} and {maximum}.");
    }

    private static TimeSpan ParseTimeSpan(
        Func<string, string?> get,
        string name,
        TimeSpan defaultValue,
        TimeSpan minimum,
        TimeSpan maximum)
    {
        var value = Normalize(get(name));
        if (value is null)
            return defaultValue;

        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
            && parsed >= minimum
            && parsed <= maximum
                ? parsed
                : throw new ApplicationConfigurationException(
                    $"{name} must be a duration between {minimum} and {maximum}.");
    }

    private static LogEventLevel ParseLogLevel(Func<string, string?> get)
    {
        var value = Normalize(get("LOGGING_MINIMUM_LEVEL")) ?? "Information";
        return Enum.TryParse<LogEventLevel>(value, true, out var parsed)
            ? parsed
            : throw new ApplicationConfigurationException(
                "LOGGING_MINIMUM_LEVEL must be Verbose, Debug, Information, Warning, Error, or Fatal.");
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class ApplicationConfigurationException(string safeMessage) : Exception(safeMessage);
