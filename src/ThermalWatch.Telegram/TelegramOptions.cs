using System.Globalization;

namespace ThermalWatch.Telegram;

public sealed record TelegramOptions(
    string? BotToken,
    string? ChannelId,
    bool NotifyExistingOnStartup,
    double ClusterRadiusKilometers,
    TimeSpan ClusterTimeWindow,
    TimeSpan SeenRetention,
    TimeSpan PreviewRetryWindow)
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
            ParseTimeSpan(getEnvironmentVariable, "TELEGRAM_PREVIEW_RETRY_WINDOW", TimeSpan.FromHours(1), TimeSpan.Zero, TimeSpan.FromDays(1)));
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

public sealed class TelegramConfigurationException(string safeMessage) : Exception(safeMessage);
