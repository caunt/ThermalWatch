using System.Globalization;

namespace ThermalWatch.Telegram;

/// <summary>The optional Telegram credentials and configured channel.</summary>
/// <param name="BotToken">The bot credential, or null when absent.</param>
/// <param name="ChannelId">The numeric channel ID or public channel username.</param>
public sealed record TelegramOptions(string? BotToken, string? ChannelId)
{
    /// <summary>Whether both required values are configured.</summary>
    public bool IsEnabled => BotToken is not null && ChannelId is not null;

    /// <summary>Whether exactly one required value is configured.</summary>
    public bool IsPartiallyConfigured => (BotToken is null) != (ChannelId is null);

    /// <summary>Reads and validates the exact uppercase Telegram settings.</summary>
    public static TelegramOptions FromEnvironment(Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        string? botToken = Normalize(getEnvironmentVariable(arg: "TELEGRAM_BOT_TOKEN"));
        string? channelId = Normalize(getEnvironmentVariable(arg: "TELEGRAM_CHANNEL_ID"));

        bool invalidChannel = channelId is not null
            && !long.TryParse(channelId, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            && (channelId.Length < 2 || channelId[index: 0] != '@');

        return invalidChannel
            ? throw new TelegramConfigurationException(safeMessage: "TELEGRAM_CHANNEL_ID must be a numeric channel ID or an @channel_username.")
            : new(botToken, channelId);
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
