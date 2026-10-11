namespace ThermalWatch.Telegram;

/// <summary>The manual operation status and its available delivery summary.</summary>
/// <param name="Status">The operation outcome.</param>
/// <param name="Response">The counts for a completed operation, otherwise null.</param>
public sealed record ManualTelegramSendResult(ManualTelegramSendStatus Status, ManualTelegramSendResponse? Response)
{
    /// <summary>The notifier has no validated Telegram connection.</summary>
    public static ManualTelegramSendResult TelegramUnavailable { get; } =
        new(ManualTelegramSendStatus.TelegramUnavailable, Response: null);

    /// <summary>Another manual send currently owns the operation.</summary>
    public static ManualTelegramSendResult AlreadyRunning { get; } =
        new(ManualTelegramSendStatus.AlreadyRunning, Response: null);

    /// <summary>The status post failed before candidate delivery.</summary>
    public static ManualTelegramSendResult StatusMessageFailed { get; } =
        new(ManualTelegramSendStatus.StatusMessageFailed, Response: null);

    /// <summary>Creates a completed operation with its delivery summary.</summary>
    public static ManualTelegramSendResult Completed(ManualTelegramSendResponse response)
    {
        return new(ManualTelegramSendStatus.Completed, response);
    }
}
