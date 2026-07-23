using System.Collections.Immutable;
using System.Globalization;
using Serilog.Events;
using ThermalWatch.Core;
using ThermalWatch.Telegram;
using ThermalWatch.Viewer;

namespace ThermalWatch.Api;

public sealed record ApplicationConfiguration(
    FirmsOptions Firms,
    NotificationOptions Notifications,
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
        NotificationOptions notifications = ParseNotificationOptions(get);
        var telegram = TelegramOptions.FromEnvironment(get);
        var viewer = ViewerOptions.FromEnvironment(get);

        if (notifications.SeenRetention < firms.ActiveWindow)
        {
            throw new ApplicationConfigurationException(
                safeMessage: "TELEGRAM_SEEN_RETENTION must be at least FIRMS_ACTIVE_WINDOW.");
        }

        return new(firms, notifications, telegram, viewer, ParseLogLevel(get));
    }

    internal static NotificationOptions ParseNotificationOptions(Func<string, string?> get) =>
        new(
            ParseBool(get, name: "TELEGRAM_NOTIFY_EXISTING_ON_STARTUP", defaultValue: false),
            ParseDouble(get, name: "TELEGRAM_CLUSTER_RADIUS_KM", defaultValue: 5, minimum: 0.01, maximum: 100),
            ParseTimeSpan(get, name: "TELEGRAM_CLUSTER_TIME_WINDOW", TimeSpan.FromMinutes(minutes: 90), TimeSpan.FromMinutes(minutes: 1), TimeSpan.FromDays(days: 1)),
            ParseTimeSpan(get, name: "TELEGRAM_SEEN_RETENTION", TimeSpan.FromHours(hours: 48), TimeSpan.FromMinutes(minutes: 1), TimeSpan.FromDays(days: 30)),
            ParseTimeSpan(get, name: "TELEGRAM_PREVIEW_RETRY_WINDOW", TimeSpan.FromHours(hours: 1), TimeSpan.Zero, TimeSpan.FromDays(days: 1)),
            new(
                new(
                    ParsePositiveDouble(get, name: "TELEGRAM_PREVIEW_WIDTH_KM", defaultValue: 30),
                    ParsePositiveDouble(get, name: "TELEGRAM_PREVIEW_HEIGHT_KM", defaultValue: 20)),
                new(
                    ParsePositiveDouble(get, name: "TELEGRAM_LARGE_PREVIEW_WIDTH_KM", defaultValue: 45),
                    ParsePositiveDouble(get, name: "TELEGRAM_LARGE_PREVIEW_HEIGHT_KM", defaultValue: 30)),
                ParsePositiveInt(get, name: "TELEGRAM_PREVIEW_PIXEL_WIDTH", defaultValue: 900),
                ParsePositiveInt(get, name: "TELEGRAM_PREVIEW_PIXEL_HEIGHT", defaultValue: 600),
                ParsePositiveInt(get, name: "TELEGRAM_LARGE_CLUSTER_MIN_DETECTIONS", defaultValue: 8),
                ParseNonNegativeDouble(get, name: "TELEGRAM_LARGE_CLUSTER_MIN_FRP_MW", defaultValue: 500),
                ParseNonNegativeDouble(get, name: "TELEGRAM_LARGE_CLUSTER_MIN_DIAMETER_KM", defaultValue: 8)),
            new(
                ParseBool(get, name: "TELEGRAM_LAND_COVER_FILTER_ENABLED", defaultValue: true),
                ParseDouble(get, name: "TELEGRAM_VEGETATION_PERCENT_THRESHOLD", defaultValue: 50, minimum: 0, maximum: 100),
                ParseDouble(get, name: "TELEGRAM_BUILT_UP_PROXIMITY_KM", defaultValue: 2, minimum: 0, maximum: 100),
                ParseNonNegativeDouble(get, name: "TELEGRAM_VEGETATION_MAX_FRP_MW", defaultValue: 300),
                ParseBool(get, name: "TELEGRAM_KEEP_HIGH_FRP_VEGETATION", defaultValue: false),
                ParseBool(get, name: "TELEGRAM_KEEP_MULTI_SATELLITE_VEGETATION", defaultValue: false)),
            new(
                ParseBool(get, name: "TELEGRAM_VISIBILITY_FILTER_ENABLED", defaultValue: true),
                ParseNonNegativeDouble(get, name: "TELEGRAM_MIN_FRP_MW", defaultValue: 50),
                ParseNonNegativeDouble(get, name: "TELEGRAM_MIN_THERMAL_CONTRAST_K", defaultValue: 20),
                ParsePositiveInt(get, name: "TELEGRAM_MIN_CLUSTER_DETECTIONS", defaultValue: 2),
                ParseDouble(get, name: "TELEGRAM_MIN_MODIS_CONFIDENCE_PERCENT", defaultValue: 60, minimum: 0, maximum: 100),
                ParseViirsConfidence(get),
                ParseBool(get, name: "TELEGRAM_REQUIRE_DAYTIME", defaultValue: true),
                ParseBool(get, name: "TELEGRAM_REQUIRE_PREVIEW", defaultValue: true)));

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

    private static bool ParseBool(
        Func<string, string?> get,
        string name,
        bool defaultValue)
    {
        string? value = Normalize(get(name));
        if (value is null)
            return defaultValue;

        return bool.TryParse(value, out bool parsed)
            ? parsed
            : throw new ApplicationConfigurationException(safeMessage: $"{name} must be true or false.");
    }

    private static double ParseDouble(
        Func<string, string?> get,
        string name,
        double defaultValue,
        double minimum,
        double maximum)
    {
        string? value = Normalize(get(name));
        if (value is null)
            return defaultValue;

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            && double.IsFinite(parsed)
            && parsed >= minimum
            && parsed <= maximum
                ? parsed
                : throw new ApplicationConfigurationException(
                    safeMessage: $"{name} must be between {minimum} and {maximum}.");
    }

    private static double ParseNonNegativeDouble(
        Func<string, string?> get,
        string name,
        double defaultValue)
    {
        string? value = Normalize(get(name));
        if (value is null)
            return defaultValue;

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            && double.IsFinite(parsed)
            && parsed >= 0
                ? parsed
                : throw new ApplicationConfigurationException(
                    safeMessage: $"{name} must be a non-negative finite number.");
    }

    private static double ParsePositiveDouble(
        Func<string, string?> get,
        string name,
        double defaultValue)
    {
        string? value = Normalize(get(name));
        if (value is null)
            return defaultValue;

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            && double.IsFinite(parsed)
            && parsed > 0
                ? parsed
                : throw new ApplicationConfigurationException(
                    safeMessage: $"{name} must be a positive finite number.");
    }

    private static int ParsePositiveInt(
        Func<string, string?> get,
        string name,
        int defaultValue)
    {
        string? value = Normalize(get(name));
        if (value is null)
            return defaultValue;

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            && parsed >= 1
                ? parsed
                : throw new ApplicationConfigurationException(
                    safeMessage: $"{name} must be an integer greater than or equal to 1.");
    }

    private static NotificationViirsConfidenceLevel ParseViirsConfidence(
        Func<string, string?> get) =>
        Normalize(get("TELEGRAM_MIN_VIIRS_CONFIDENCE"))?.ToLowerInvariant() switch
        {
            null or "n" => NotificationViirsConfidenceLevel.Nominal,
            "l" => NotificationViirsConfidenceLevel.Low,
            "h" => NotificationViirsConfidenceLevel.High,
            _ => throw new ApplicationConfigurationException(
                safeMessage: "TELEGRAM_MIN_VIIRS_CONFIDENCE must be l, n, or h.")
        };

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
