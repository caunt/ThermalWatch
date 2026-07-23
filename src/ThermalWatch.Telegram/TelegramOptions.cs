using System.Globalization;

namespace ThermalWatch.Telegram;

public sealed record TelegramOptions(
    string? BotToken,
    string? ChannelId,
    bool NotifyExistingOnStartup,
    double ClusterRadiusKilometers,
    TimeSpan ClusterTimeWindow,
    TimeSpan SeenRetention,
    TimeSpan PreviewRetryWindow,
    TelegramPreviewOptions Preview,
    TelegramLandCoverOptions LandCover,
    TelegramVisibilityOptions Visibility)
{
    public bool IsEnabled => BotToken is not null && ChannelId is not null;

    public bool IsPartiallyConfigured => (BotToken is null) != (ChannelId is null);

    public static TelegramOptions FromEnvironment(Func<string, string?> getEnvironmentVariable)
    {
        string? botToken = Normalize(getEnvironmentVariable("TELEGRAM_BOT_TOKEN"));
        string? channelId = Normalize(getEnvironmentVariable("TELEGRAM_CHANNEL_ID"));

        if (channelId is not null
            && !long.TryParse(channelId, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            && (channelId.Length < 2 || channelId[0] != '@'))
        {
            throw new TelegramConfigurationException(
                safeMessage: "TELEGRAM_CHANNEL_ID must be a numeric channel ID or an @channel_username.");
        }

        return new(
            botToken,
            channelId,
            ParseBool(getEnvironmentVariable, name: "TELEGRAM_NOTIFY_EXISTING_ON_STARTUP", defaultValue: false),
            ParseDouble(getEnvironmentVariable, name: "TELEGRAM_CLUSTER_RADIUS_KM", defaultValue: 5, minimum: 0.01, maximum: 100),
            ParseTimeSpan(getEnvironmentVariable, name: "TELEGRAM_CLUSTER_TIME_WINDOW", TimeSpan.FromMinutes(minutes: 90), TimeSpan.FromMinutes(minutes: 1), TimeSpan.FromDays(days: 1)),
            ParseTimeSpan(getEnvironmentVariable, name: "TELEGRAM_SEEN_RETENTION", TimeSpan.FromHours(hours: 48), TimeSpan.FromMinutes(minutes: 1), TimeSpan.FromDays(days: 30)),
            ParseTimeSpan(getEnvironmentVariable, name: "TELEGRAM_PREVIEW_RETRY_WINDOW", TimeSpan.FromHours(hours: 1), TimeSpan.Zero, TimeSpan.FromDays(days: 1)),
            new(
                new(
                    ParsePositiveDouble(getEnvironmentVariable, name: "TELEGRAM_PREVIEW_WIDTH_KM", defaultValue: 30),
                    ParsePositiveDouble(getEnvironmentVariable, name: "TELEGRAM_PREVIEW_HEIGHT_KM", defaultValue: 20)),
                new(
                    ParsePositiveDouble(getEnvironmentVariable, name: "TELEGRAM_LARGE_PREVIEW_WIDTH_KM", defaultValue: 45),
                    ParsePositiveDouble(getEnvironmentVariable, name: "TELEGRAM_LARGE_PREVIEW_HEIGHT_KM", defaultValue: 30)),
                ParsePositiveInt(getEnvironmentVariable, name: "TELEGRAM_PREVIEW_PIXEL_WIDTH", defaultValue: 900),
                ParsePositiveInt(getEnvironmentVariable, name: "TELEGRAM_PREVIEW_PIXEL_HEIGHT", defaultValue: 600),
                ParsePositiveInt(getEnvironmentVariable, name: "TELEGRAM_LARGE_CLUSTER_MIN_DETECTIONS", defaultValue: 8),
                ParseNonNegativeDouble(getEnvironmentVariable, name: "TELEGRAM_LARGE_CLUSTER_MIN_FRP_MW", defaultValue: 500),
                ParseNonNegativeDouble(getEnvironmentVariable, name: "TELEGRAM_LARGE_CLUSTER_MIN_DIAMETER_KM", defaultValue: 8)),
            new(
                ParseBool(getEnvironmentVariable, name: "TELEGRAM_LAND_COVER_FILTER_ENABLED", defaultValue: true),
                ParseDouble(getEnvironmentVariable, name: "TELEGRAM_VEGETATION_PERCENT_THRESHOLD", defaultValue: 50, minimum: 0, maximum: 100),
                ParseDouble(getEnvironmentVariable, name: "TELEGRAM_BUILT_UP_PROXIMITY_KM", defaultValue: 2, minimum: 0, maximum: 100),
                ParseNonNegativeDouble(getEnvironmentVariable, name: "TELEGRAM_VEGETATION_MAX_FRP_MW", defaultValue: 300),
                ParseBool(getEnvironmentVariable, name: "TELEGRAM_KEEP_HIGH_FRP_VEGETATION", defaultValue: false),
                ParseBool(getEnvironmentVariable, name: "TELEGRAM_KEEP_MULTI_SATELLITE_VEGETATION", defaultValue: false)),
            new(
                ParseBool(getEnvironmentVariable, name: "TELEGRAM_VISIBILITY_FILTER_ENABLED", defaultValue: true),
                ParseNonNegativeDouble(getEnvironmentVariable, name: "TELEGRAM_MIN_FRP_MW", defaultValue: 50),
                ParseNonNegativeDouble(getEnvironmentVariable, name: "TELEGRAM_MIN_THERMAL_CONTRAST_K", defaultValue: 20),
                ParsePositiveInt(getEnvironmentVariable, name: "TELEGRAM_MIN_CLUSTER_DETECTIONS", defaultValue: 2),
                ParseDouble(getEnvironmentVariable, name: "TELEGRAM_MIN_MODIS_CONFIDENCE_PERCENT", defaultValue: 60, minimum: 0, maximum: 100),
                ParseViirsConfidence(getEnvironmentVariable),
                ParseBool(getEnvironmentVariable, name: "TELEGRAM_REQUIRE_DAYTIME", defaultValue: true),
                ParseBool(getEnvironmentVariable, name: "TELEGRAM_REQUIRE_PREVIEW", defaultValue: true)));
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool ParseBool(
        Func<string, string?> getEnvironmentVariable,
        string name,
        bool defaultValue)
    {
        string? value = Normalize(getEnvironmentVariable(name));
        if (value is null)
            return defaultValue;

        return bool.TryParse(value, out bool parsed)
            ? parsed
            : throw new TelegramConfigurationException(safeMessage: $"{name} must be true or false.");
    }

    private static double ParseDouble(
        Func<string, string?> getEnvironmentVariable,
        string name,
        double defaultValue,
        double minimum,
        double maximum)
    {
        string? value = Normalize(getEnvironmentVariable(name));
        if (value is null)
            return defaultValue;

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            && double.IsFinite(parsed)
            && parsed >= minimum
            && parsed <= maximum
                ? parsed
                : throw new TelegramConfigurationException(
                    safeMessage: $"{name} must be between {minimum} and {maximum}.");
    }

    private static double ParseNonNegativeDouble(
        Func<string, string?> getEnvironmentVariable,
        string name,
        double defaultValue)
    {
        string? value = Normalize(getEnvironmentVariable(name));
        if (value is null)
            return defaultValue;

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            && double.IsFinite(parsed)
            && parsed >= 0
                ? parsed
                : throw new TelegramConfigurationException(
                    safeMessage: $"{name} must be a non-negative finite number.");
    }

    private static double ParsePositiveDouble(
        Func<string, string?> getEnvironmentVariable,
        string name,
        double defaultValue)
    {
        string? value = Normalize(getEnvironmentVariable(name));
        if (value is null)
            return defaultValue;

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            && double.IsFinite(parsed)
            && parsed > 0
                ? parsed
                : throw new TelegramConfigurationException(
                    safeMessage: $"{name} must be a positive finite number.");
    }

    private static int ParsePositiveInt(
        Func<string, string?> getEnvironmentVariable,
        string name,
        int defaultValue)
    {
        string? value = Normalize(getEnvironmentVariable(name));
        if (value is null)
            return defaultValue;

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            && parsed >= 1
                ? parsed
                : throw new TelegramConfigurationException(
                    safeMessage: $"{name} must be an integer greater than or equal to 1.");
    }

    private static ViirsConfidenceLevel ParseViirsConfidence(
        Func<string, string?> getEnvironmentVariable) =>
        Normalize(getEnvironmentVariable("TELEGRAM_MIN_VIIRS_CONFIDENCE"))?.ToLowerInvariant() switch
        {
            null or "n" => ViirsConfidenceLevel.Nominal,
            "l" => ViirsConfidenceLevel.Low,
            "h" => ViirsConfidenceLevel.High,
            _ => throw new TelegramConfigurationException(
                safeMessage: "TELEGRAM_MIN_VIIRS_CONFIDENCE must be l, n, or h.")
        };

    private static TimeSpan ParseTimeSpan(
        Func<string, string?> getEnvironmentVariable,
        string name,
        TimeSpan defaultValue,
        TimeSpan minimum,
        TimeSpan maximum)
    {
        string? value = Normalize(getEnvironmentVariable(name));
        if (value is null)
            return defaultValue;

        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out TimeSpan parsed)
            && parsed >= minimum
            && parsed <= maximum
                ? parsed
                : throw new TelegramConfigurationException(
                    safeMessage: $"{name} must be a duration between {minimum} and {maximum}.");
    }
}
