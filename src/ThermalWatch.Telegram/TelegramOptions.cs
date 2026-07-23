using System.Globalization;

namespace ThermalWatch.Telegram;

public sealed record TelegramOptions(
    string? BotToken,
    string? ChannelId)
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

        return new(botToken, channelId);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
