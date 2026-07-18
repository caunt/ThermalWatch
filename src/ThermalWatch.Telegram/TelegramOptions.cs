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
    TelegramVisibilityOptions Visibility)
{
    public bool IsEnabled => BotToken is not null && ChannelId is not null;

    public bool IsPartiallyConfigured => (BotToken is null) != (ChannelId is null);

    public static TelegramOptions FromEnvironment(Func<string, string?> getEnvironmentVariable)
    {
        var botToken = Normalize(getEnvironmentVariable("TELEGRAM_BOT_TOKEN"));
        var channelId = Normalize(getEnvironmentVariable("TELEGRAM_CHANNEL_ID"));

        if (channelId is not null
            && !long.TryParse(channelId, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            && (channelId.Length < 2 || channelId[0] != '@'))
        {
            throw new TelegramConfigurationException(
                "TELEGRAM_CHANNEL_ID must be a numeric channel ID or an @channel_username.");
        }

        return new(
            botToken,
            channelId,
            ParseBool(getEnvironmentVariable, "TELEGRAM_NOTIFY_EXISTING_ON_STARTUP", false),
            ParseDouble(getEnvironmentVariable, "TELEGRAM_CLUSTER_RADIUS_KM", 5, 0.01, 100),
            ParseTimeSpan(getEnvironmentVariable, "TELEGRAM_CLUSTER_TIME_WINDOW", TimeSpan.FromMinutes(90), TimeSpan.FromMinutes(1), TimeSpan.FromDays(1)),
            ParseTimeSpan(getEnvironmentVariable, "TELEGRAM_SEEN_RETENTION", TimeSpan.FromHours(48), TimeSpan.FromMinutes(1), TimeSpan.FromDays(30)),
            ParseTimeSpan(getEnvironmentVariable, "TELEGRAM_PREVIEW_RETRY_WINDOW", TimeSpan.FromHours(1), TimeSpan.Zero, TimeSpan.FromDays(1)),
            new(
                new(
                    ParsePositiveDouble(getEnvironmentVariable, "TELEGRAM_PREVIEW_WIDTH_KM", 20),
                    ParsePositiveDouble(getEnvironmentVariable, "TELEGRAM_PREVIEW_HEIGHT_KM", 30)),
                new(
                    ParsePositiveDouble(getEnvironmentVariable, "TELEGRAM_LARGE_PREVIEW_WIDTH_KM", 30),
                    ParsePositiveDouble(getEnvironmentVariable, "TELEGRAM_LARGE_PREVIEW_HEIGHT_KM", 45)),
                ParsePositiveInt(getEnvironmentVariable, "TELEGRAM_PREVIEW_PIXEL_WIDTH", 600),
                ParsePositiveInt(getEnvironmentVariable, "TELEGRAM_PREVIEW_PIXEL_HEIGHT", 900),
                ParsePositiveInt(getEnvironmentVariable, "TELEGRAM_LARGE_CLUSTER_MIN_DETECTIONS", 8),
                ParseNonNegativeDouble(getEnvironmentVariable, "TELEGRAM_LARGE_CLUSTER_MIN_FRP_MW", 500),
                ParseNonNegativeDouble(getEnvironmentVariable, "TELEGRAM_LARGE_CLUSTER_MIN_DIAMETER_KM", 8)),
            new(
                ParseBool(getEnvironmentVariable, "TELEGRAM_VISIBILITY_FILTER_ENABLED", true),
                ParseNonNegativeDouble(getEnvironmentVariable, "TELEGRAM_MIN_FRP_MW", 50),
                ParseNonNegativeDouble(getEnvironmentVariable, "TELEGRAM_MIN_THERMAL_CONTRAST_K", 20),
                ParsePositiveInt(getEnvironmentVariable, "TELEGRAM_MIN_CLUSTER_DETECTIONS", 2),
                ParseDouble(getEnvironmentVariable, "TELEGRAM_MIN_MODIS_CONFIDENCE_PERCENT", 60, 0, 100),
                ParseViirsConfidence(getEnvironmentVariable),
                ParseBool(getEnvironmentVariable, "TELEGRAM_REQUIRE_DAYTIME", true),
                ParseBool(getEnvironmentVariable, "TELEGRAM_REQUIRE_PREVIEW", true)));
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool ParseBool(
        Func<string, string?> getEnvironmentVariable,
        string name,
        bool defaultValue)
    {
        var value = Normalize(getEnvironmentVariable(name));
        if (value is null)
            return defaultValue;

        return bool.TryParse(value, out var parsed)
            ? parsed
            : throw new TelegramConfigurationException($"{name} must be true or false.");
    }

    private static double ParseDouble(
        Func<string, string?> getEnvironmentVariable,
        string name,
        double defaultValue,
        double minimum,
        double maximum)
    {
        var value = Normalize(getEnvironmentVariable(name));
        if (value is null)
            return defaultValue;

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && double.IsFinite(parsed)
            && parsed >= minimum
            && parsed <= maximum
                ? parsed
                : throw new TelegramConfigurationException(
                    $"{name} must be between {minimum} and {maximum}.");
    }

    private static double ParseNonNegativeDouble(
        Func<string, string?> getEnvironmentVariable,
        string name,
        double defaultValue)
    {
        var value = Normalize(getEnvironmentVariable(name));
        if (value is null)
            return defaultValue;

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && double.IsFinite(parsed)
            && parsed >= 0
                ? parsed
                : throw new TelegramConfigurationException(
                    $"{name} must be a non-negative finite number.");
    }

    private static double ParsePositiveDouble(
        Func<string, string?> getEnvironmentVariable,
        string name,
        double defaultValue)
    {
        var value = Normalize(getEnvironmentVariable(name));
        if (value is null)
            return defaultValue;

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && double.IsFinite(parsed)
            && parsed > 0
                ? parsed
                : throw new TelegramConfigurationException(
                    $"{name} must be a positive finite number.");
    }

    private static int ParsePositiveInt(
        Func<string, string?> getEnvironmentVariable,
        string name,
        int defaultValue)
    {
        var value = Normalize(getEnvironmentVariable(name));
        if (value is null)
            return defaultValue;

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            && parsed >= 1
                ? parsed
                : throw new TelegramConfigurationException(
                    $"{name} must be an integer greater than or equal to 1.");
    }

    private static ViirsConfidenceLevel ParseViirsConfidence(
        Func<string, string?> getEnvironmentVariable) =>
        Normalize(getEnvironmentVariable("TELEGRAM_MIN_VIIRS_CONFIDENCE"))?.ToLowerInvariant() switch
        {
            null or "n" => ViirsConfidenceLevel.Nominal,
            "l" => ViirsConfidenceLevel.Low,
            "h" => ViirsConfidenceLevel.High,
            _ => throw new TelegramConfigurationException(
                "TELEGRAM_MIN_VIIRS_CONFIDENCE must be l, n, or h.")
        };

    private static TimeSpan ParseTimeSpan(
        Func<string, string?> getEnvironmentVariable,
        string name,
        TimeSpan defaultValue,
        TimeSpan minimum,
        TimeSpan maximum)
    {
        var value = Normalize(getEnvironmentVariable(name));
        if (value is null)
            return defaultValue;

        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
            && parsed >= minimum
            && parsed <= maximum
                ? parsed
                : throw new TelegramConfigurationException(
                    $"{name} must be a duration between {minimum} and {maximum}.");
    }
}

public sealed record TelegramPreviewOptions(
    TelegramPreviewSize PreviewSize,
    TelegramPreviewSize LargePreviewSize,
    int PixelWidth,
    int PixelHeight,
    int LargeClusterMinimumDetections,
    double LargeClusterMinimumFrpMegawatts,
    double LargeClusterMinimumDiameterKilometers);

public readonly record struct TelegramPreviewSize(
    double WidthKilometers,
    double HeightKilometers);

public sealed record TelegramVisibilityOptions(
    bool Enabled,
    double MinimumFrpMegawatts,
    double MinimumThermalContrastKelvin,
    int MinimumClusterDetections,
    double MinimumModisConfidencePercent,
    ViirsConfidenceLevel MinimumViirsConfidence,
    bool RequireDaytime,
    bool RequirePreview);

public enum ViirsConfidenceLevel
{
    Low,
    Nominal,
    High
}

public sealed class TelegramConfigurationException(string safeMessage) : Exception(safeMessage);
