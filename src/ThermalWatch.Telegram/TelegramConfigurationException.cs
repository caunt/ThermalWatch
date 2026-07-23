namespace ThermalWatch.Telegram;

public sealed class TelegramConfigurationException(string safeMessage) : Exception(safeMessage);
