namespace ThermalWatch.Telegram;

/// <summary>The manual-send states mapped by the HTTP endpoint.</summary>
public enum ManualTelegramSendStatus
{
    /// <summary>The selection and delivery pass completed.</summary>
    Completed,
    /// <summary>The notifier is disabled or unvalidated.</summary>
    TelegramUnavailable,
    /// <summary>Another manual operation is running.</summary>
    AlreadyRunning,
    /// <summary>The initial status post failed.</summary>
    StatusMessageFailed
}
