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
        string mapKey = Required(get, name: "FIRMS_MAP_KEY");
        if (mapKey.Length != 32 || mapKey.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new ApplicationConfigurationException(
                safeMessage: "FIRMS_MAP_KEY must be a valid 32-character MAP_KEY.");
        }

        ImmutableArray<string> countries = ParseCountries(Required(get, name: "FIRMS_COUNTRIES"));
        var firms = new FirmsOptions(
            mapKey,
            countries,
            ParseTimeSpan(get, name: "FIRMS_POLL_INTERVAL", TimeSpan.FromMinutes(minutes: 5), TimeSpan.FromSeconds(seconds: 10), TimeSpan.FromDays(days: 1)),
            ParseTimeSpan(get, name: "FIRMS_ACTIVE_WINDOW", TimeSpan.FromHours(hours: 24), TimeSpan.FromMinutes(minutes: 1), TimeSpan.FromHours(hours: 24)),
            ParseTimeSpan(get, name: "FIRMS_REQUEST_TIMEOUT", TimeSpan.FromSeconds(seconds: 45), TimeSpan.FromSeconds(seconds: 5), TimeSpan.FromMinutes(minutes: 5)),
            ParseInt(get, name: "FIRMS_MAX_CONCURRENCY", defaultValue: 4, minimum: 1, maximum: 32));
        var telegram = TelegramOptions.FromEnvironment(get);
        var viewer = ViewerOptions.FromEnvironment(get);

        if (telegram.SeenRetention < firms.ActiveWindow)
        {
            throw new ApplicationConfigurationException(
                safeMessage: "TELEGRAM_SEEN_RETENTION must be at least FIRMS_ACTIVE_WINDOW.");
        }

        return new(firms, telegram, viewer, ParseLogLevel(get));
    }

    private static ImmutableArray<string> ParseCountries(string value)
    {
        string[] entries = value.Split(',');
        if (entries.Any(string.IsNullOrWhiteSpace))
            throw new ApplicationConfigurationException(safeMessage: "FIRMS_COUNTRIES contains an empty country code.");

        var countries = entries
            .Select(entry => entry.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();

        if (countries.Length == 0)
            throw new ApplicationConfigurationException(safeMessage: "FIRMS_COUNTRIES must contain at least one country.");

        string? invalid = countries.FirstOrDefault(country => !CountryCatalog.IsValid(country));
        if (invalid is not null)
        {
            throw new ApplicationConfigurationException(
                safeMessage: $"FIRMS_COUNTRIES contains invalid ISO alpha-3 code '{invalid}'.");
        }

        return countries;
    }

    private static string Required(Func<string, string?> get, string name)
    {
        string? value = Normalize(get(name));
        return value ?? throw new ApplicationConfigurationException(safeMessage: $"{name} is required and cannot be empty.");
    }

    private static int ParseInt(
        Func<string, string?> get,
        string name,
        int defaultValue,
        int minimum,
        int maximum)
    {
        string? value = Normalize(get(name));
        if (value is null)
            return defaultValue;

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            && parsed >= minimum
            && parsed <= maximum
                ? parsed
                : throw new ApplicationConfigurationException(
                    safeMessage: $"{name} must be an integer between {minimum} and {maximum}.");
    }

    private static TimeSpan ParseTimeSpan(
        Func<string, string?> get,
        string name,
        TimeSpan defaultValue,
        TimeSpan minimum,
        TimeSpan maximum)
    {
        string? value = Normalize(get(name));
        if (value is null)
            return defaultValue;

        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out TimeSpan parsed)
            && parsed >= minimum
            && parsed <= maximum
                ? parsed
                : throw new ApplicationConfigurationException(
                    safeMessage: $"{name} must be a duration between {minimum} and {maximum}.");
    }

    private static LogEventLevel ParseLogLevel(Func<string, string?> get)
    {
        string value = Normalize(get("LOGGING_MINIMUM_LEVEL")) ?? "Information";
        return Enum.TryParse<LogEventLevel>(value, ignoreCase: true, out LogEventLevel parsed)
            ? parsed
            : throw new ApplicationConfigurationException(
                safeMessage: "LOGGING_MINIMUM_LEVEL must be Verbose, Debug, Information, Warning, Error, or Fatal.");
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
