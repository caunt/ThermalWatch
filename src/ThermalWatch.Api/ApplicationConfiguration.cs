using System.Collections.Immutable;
using System.Globalization;

using Serilog.Events;

using ThermalWatch.Core;
using ThermalWatch.Telegram;
using ThermalWatch.Viewer;

namespace ThermalWatch.Api;

internal sealed record ApplicationConfiguration(
    FirmsOptions Firms,
    NotificationOptions Notifications,
    TelegramOptions Telegram,
    ViewerOptions Viewer,
    LogEventLevel MinimumLogLevel
)
{
    private static readonly TimeSpan DefaultEpisodeRetention = TimeSpan.FromHours(hours: 48);
    private static readonly TimeSpan MaximumActiveWindow = TimeSpan.FromHours(hours: 72);

    public static ApplicationConfiguration FromEnvironment()
    {
        return FromEnvironment(Environment.GetEnvironmentVariable);
    }

    internal static ApplicationConfiguration FromEnvironment(Func<string, string?> get)
    {
        string mapKey = Required(get, name: "FIRMS_MAP_KEY");

        if (mapKey.Length != 32 || mapKey.Any(static character => !char.IsAsciiLetterOrDigit(character)))
            throw new ApplicationConfigurationException(safeMessage: "FIRMS_MAP_KEY must be a valid 32-character MAP_KEY.");

        ImmutableArray<string> countryCodes = ParseCountryCodes(Required(get, name: "FIRMS_COUNTRIES"));

        FirmsOptions firms = new(
            mapKey,
            countryCodes,
            ParseTimeSpan(
                get,
                name: "FIRMS_POLL_INTERVAL",
                TimeSpan.FromMinutes(minutes: 5),
                TimeSpan.FromSeconds(seconds: 10),
                TimeSpan.FromDays(days: 1)
            ),
            ParseTimeSpan(get, name: "FIRMS_ACTIVE_WINDOW", TimeSpan.FromHours(hours: 24), TimeSpan.FromMinutes(minutes: 1), MaximumActiveWindow),
            ParseTimeSpan(
                get,
                name: "FIRMS_REQUEST_TIMEOUT",
                TimeSpan.FromSeconds(seconds: 45),
                TimeSpan.FromSeconds(seconds: 5),
                TimeSpan.FromMinutes(minutes: 5)
            ),
            ParseInt(get, name: "FIRMS_MAX_CONCURRENCY", defaultValue: 4, minimum: 1, maximum: 32)
        );

        TimeSpan defaultEpisodeRetention = firms.ActiveWindow > DefaultEpisodeRetention
            ? firms.ActiveWindow
            : DefaultEpisodeRetention;

        NotificationOptions notifications = ParseNotificationOptions(get, defaultEpisodeRetention);
        TelegramOptions telegram = TelegramOptions.FromEnvironment(get);
        ViewerOptions viewer = ViewerOptions.FromEnvironment(get);

        return notifications.EpisodeRetention < firms.ActiveWindow
            ? throw new ApplicationConfigurationException(safeMessage: "NOTIFICATION_EPISODE_RETENTION must be at least FIRMS_ACTIVE_WINDOW.")
            : new(firms, notifications, telegram, viewer, ParseLogLevel(get));
    }

    internal static NotificationOptions ParseNotificationOptions(Func<string, string?> get)
    {
        return ParseNotificationOptions(get, DefaultEpisodeRetention);
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool ParseBool(Func<string, string?> get, string name, bool defaultValue)
    {
        string? value = Normalize(get(name));

        return value is null
            ? defaultValue
            : bool.TryParse(value, out bool parsed)
            ? parsed
            : throw new ApplicationConfigurationException($"{name} must be true or false.");
    }

    private static ImmutableArray<string> ParseCountryCodes(string value)
    {
        string[] entries = value.Split(separator: ',');

        if (entries.Any(string.IsNullOrWhiteSpace))
            throw new ApplicationConfigurationException(safeMessage: "FIRMS_COUNTRIES contains an empty country code.");

        ImmutableArray<string> countryCodes = [.. entries
            .Select(static entry => entry.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)];

        if (countryCodes.Length == 0)
            throw new ApplicationConfigurationException(safeMessage: "FIRMS_COUNTRIES must contain at least one country.");

        string? invalid = countryCodes.FirstOrDefault(static countryCode => !CountryCatalog.IsValid(countryCode));

        return invalid is not null
            ? throw new ApplicationConfigurationException($"FIRMS_COUNTRIES contains invalid ISO alpha-3 code '{invalid}'.")
            : countryCodes;
    }

    private static double ParseDouble(Func<string, string?> get, string name, double defaultValue, double minimum, double maximum)
    {
        string? value = Normalize(get(name));

        return value is null
            ? defaultValue
            : !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? throw new ApplicationConfigurationException($"{name} must be between {minimum} and {maximum}.")
            : double.IsFinite(parsed) && parsed >= minimum && parsed <= maximum
            ? parsed
            : throw new ApplicationConfigurationException($"{name} must be between {minimum} and {maximum}.");
    }

    private static int ParseInt(Func<string, string?> get, string name, int defaultValue, int minimum, int maximum)
    {
        string? value = Normalize(get(name));

        return value is null
            ? defaultValue
            : !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            ? throw new ApplicationConfigurationException($"{name} must be an integer between {minimum} and {maximum}.")
            : parsed >= minimum && parsed <= maximum
            ? parsed
            : throw new ApplicationConfigurationException($"{name} must be an integer between {minimum} and {maximum}.");
    }

    private static LogEventLevel ParseLogLevel(Func<string, string?> get)
    {
        string value = Normalize(get(arg: "LOGGING_MINIMUM_LEVEL")) ?? "Information";

        return Enum.TryParse(value, ignoreCase: true, out LogEventLevel parsed)
            ? parsed
            : throw new ApplicationConfigurationException(safeMessage: "LOGGING_MINIMUM_LEVEL must be Verbose, Debug, Information, Warning, Error, or Fatal.");
    }

    private static double ParseNonNegativeDouble(Func<string, string?> get, string name, double defaultValue)
    {
        string? value = Normalize(get(name));

        return value is null
            ? defaultValue
            : !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? throw new ApplicationConfigurationException($"{name} must be a non-negative finite number.")
            : double.IsFinite(parsed) && parsed >= 0
            ? parsed
            : throw new ApplicationConfigurationException($"{name} must be a non-negative finite number.");
    }

    private static NotificationOptions ParseNotificationOptions(Func<string, string?> get, TimeSpan defaultEpisodeRetention)
    {
        return new(
            ParseBool(get, name: "NOTIFICATION_SEND_EXISTING_ON_STARTUP", defaultValue: false),
            ParseDouble(get, name: "NOTIFICATION_CLUSTER_RADIUS_KM", defaultValue: 5, minimum: 0.01, maximum: 100),
            ParseTimeSpan(
                get,
                name: "NOTIFICATION_CLUSTER_TIME_WINDOW",
                TimeSpan.FromMinutes(minutes: 90),
                TimeSpan.FromMinutes(minutes: 1),
                TimeSpan.FromDays(days: 1)
            ),
            ParseTimeSpan(
                get,
                name: "NOTIFICATION_EPISODE_RETENTION",
                defaultEpisodeRetention,
                TimeSpan.FromMinutes(minutes: 1),
                TimeSpan.FromDays(days: 30)
            ),
            ParseBool(get, name: "NOTIFICATION_HISTORICAL_FRP_FILTER_ENABLED", defaultValue: true),
            new(
                new(
                    ParsePositiveDouble(get, name: "NOTIFICATION_PREVIEW_WIDTH_KM", defaultValue: 48),
                    ParsePositiveDouble(get, name: "NOTIFICATION_PREVIEW_HEIGHT_KM", defaultValue: 60)
                ),
                new(
                    ParsePositiveDouble(get, name: "NOTIFICATION_LARGE_PREVIEW_WIDTH_KM", defaultValue: 72),
                    ParsePositiveDouble(get, name: "NOTIFICATION_LARGE_PREVIEW_HEIGHT_KM", defaultValue: 90)
                ),
                ParsePositiveInt(get, name: "NOTIFICATION_PREVIEW_PIXEL_WIDTH", defaultValue: 3072),
                ParsePositiveInt(get, name: "NOTIFICATION_PREVIEW_PIXEL_HEIGHT", defaultValue: 3840),
                ParsePositiveInt(get, name: "NOTIFICATION_LARGE_CLUSTER_MIN_DETECTIONS", defaultValue: 8),
                ParseNonNegativeDouble(get, name: "NOTIFICATION_LARGE_CLUSTER_MIN_FRP_MW", defaultValue: 500),
                ParseNonNegativeDouble(get, name: "NOTIFICATION_LARGE_CLUSTER_MIN_DIAMETER_KM", defaultValue: 8)
            ),
            new(
                ParseBool(get, name: "NOTIFICATION_LAND_COVER_FILTER_ENABLED", defaultValue: true),
                ParseDouble(get, name: "NOTIFICATION_VEGETATION_PERCENT_THRESHOLD", defaultValue: 50, minimum: 0, maximum: 100),
                ParseDouble(get, name: "NOTIFICATION_BUILT_UP_PROXIMITY_KM", defaultValue: 15, minimum: 0, maximum: 100),
                ParseNonNegativeDouble(get, name: "NOTIFICATION_VEGETATION_MAX_FRP_MW", defaultValue: 300),
                ParseBool(get, name: "NOTIFICATION_KEEP_HIGH_FRP_VEGETATION", defaultValue: false),
                ParseBool(get, name: "NOTIFICATION_KEEP_MULTI_SATELLITE_VEGETATION", defaultValue: false)
            ),
            new(
                ParseBool(get, name: "NOTIFICATION_VISIBILITY_FILTER_ENABLED", defaultValue: true),
                ParseNonNegativeDouble(get, name: "NOTIFICATION_MIN_FRP_MW", defaultValue: 0),
                ParseNonNegativeDouble(get, name: "NOTIFICATION_MIN_CLUSTER_TOTAL_FRP_MW", defaultValue: 100),
                ParseNonNegativeDouble(get, name: "NOTIFICATION_MIN_THERMAL_CONTRAST_K", defaultValue: 20),
                ParsePositiveInt(get, name: "NOTIFICATION_MIN_CLUSTER_DETECTIONS", defaultValue: 2),
                ParseDouble(get, name: "NOTIFICATION_MIN_MODIS_CONFIDENCE_PERCENT", defaultValue: 60, minimum: 0, maximum: 100),
                ParseViirsConfidence(get),
                ParseBool(get, name: "NOTIFICATION_REQUIRE_DAYTIME", defaultValue: true),
                ParseBool(get, name: "NOTIFICATION_REQUIRE_PREVIEW", defaultValue: true)
            )
        );
    }

    private static double ParsePositiveDouble(Func<string, string?> get, string name, double defaultValue)
    {
        string? value = Normalize(get(name));

        return value is null
            ? defaultValue
            : !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? throw new ApplicationConfigurationException($"{name} must be a positive finite number.")
            : double.IsFinite(parsed) && parsed > 0
            ? parsed
            : throw new ApplicationConfigurationException($"{name} must be a positive finite number.");
    }

    private static int ParsePositiveInt(Func<string, string?> get, string name, int defaultValue)
    {
        string? value = Normalize(get(name));

        return value is null
            ? defaultValue
            : !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            ? throw new ApplicationConfigurationException($"{name} must be an integer greater than or equal to 1.")
            : parsed >= 1
            ? parsed
            : throw new ApplicationConfigurationException($"{name} must be an integer greater than or equal to 1.");
    }

    private static TimeSpan ParseTimeSpan(Func<string, string?> get, string name, TimeSpan defaultValue, TimeSpan minimum, TimeSpan maximum)
    {
        string? value = Normalize(get(name));

        return value is null
            ? defaultValue
            : !TryParseTimeSpan(value, out TimeSpan parsed)
            ? throw new ApplicationConfigurationException($"{name} must be a duration between {minimum} and {maximum}.")
            : parsed >= minimum && parsed <= maximum
            ? parsed
            : throw new ApplicationConfigurationException($"{name} must be a duration between {minimum} and {maximum}.");
    }

    private static NotificationViirsConfidenceLevel ParseViirsConfidence(Func<string, string?> get)
    {
        return Normalize(get(arg: "NOTIFICATION_MIN_VIIRS_CONFIDENCE"))?.ToUpperInvariant() switch
        {
            null or "N" => NotificationViirsConfidenceLevel.Nominal,
            "L" => NotificationViirsConfidenceLevel.Low,
            "H" => NotificationViirsConfidenceLevel.High,
            _ => throw new ApplicationConfigurationException(safeMessage: "NOTIFICATION_MIN_VIIRS_CONFIDENCE must be l, n, or h.")
        };
    }

    private static string Required(Func<string, string?> get, string name)
    {
        string? value = Normalize(get(name));

        return value ?? throw new ApplicationConfigurationException($"{name} is required and cannot be empty.");
    }

    private static bool TryParseTimeSpan(string value, out TimeSpan parsed)
    {
        int firstSeparator = value.IndexOf(value: ':', StringComparison.Ordinal);
        int secondSeparator = value.IndexOf(value: ':', firstSeparator + 1);

        if (firstSeparator > 0 && secondSeparator > firstSeparator + 1)
        {
            bool validHours = long.TryParse(value.AsSpan(start: 0, firstSeparator), NumberStyles.None, CultureInfo.InvariantCulture, out long totalHours);

            if (validHours && totalHours >= 24)
            {
                bool validRemainder = TimeSpan.TryParse($"00:{value[(firstSeparator + 1)..]}", CultureInfo.InvariantCulture, out TimeSpan remainder);

                if (validRemainder && remainder >= TimeSpan.Zero && remainder < TimeSpan.FromHours(hours: 1))
                {
                    if (totalHours <= (TimeSpan.MaxValue.Ticks - remainder.Ticks) / TimeSpan.TicksPerHour)
                    {
                        parsed = TimeSpan.FromTicks((totalHours * TimeSpan.TicksPerHour) + remainder.Ticks);

                        return true;
                    }
                }
            }
        }

        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out parsed);
    }
}
