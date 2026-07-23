namespace ThermalWatch.Telegram;

public sealed record ManualTelegramSendResponse(
    int RequestedCount,
    int EligibleCount,
    int SelectedCount,
    int SentCount,
    int FailedCount,
    string[] FailedIds);
