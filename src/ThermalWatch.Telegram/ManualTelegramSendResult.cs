namespace ThermalWatch.Telegram;

public sealed record ManualTelegramSendResult(
    ManualTelegramSendStatus Status,
    ManualTelegramSendResponse? Response)
{
    public static ManualTelegramSendResult TelegramUnavailable { get; } =
        new(ManualTelegramSendStatus.TelegramUnavailable, Response: null);

    public static ManualTelegramSendResult AlreadyRunning { get; } =
        new(ManualTelegramSendStatus.AlreadyRunning, Response: null);

    public static ManualTelegramSendResult StatusMessageFailed { get; } =
        new(ManualTelegramSendStatus.StatusMessageFailed, Response: null);

    public static ManualTelegramSendResult Completed(ManualTelegramSendResponse response) =>
        new(ManualTelegramSendStatus.Completed, response);
}
