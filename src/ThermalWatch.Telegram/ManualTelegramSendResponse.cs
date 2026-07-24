namespace ThermalWatch.Telegram;

public sealed record ManualTelegramSendResponse(
    int RequestedClusterCount,
    int EligibleClusterCount,
    int SelectedClusterCount,
    int SentClusterCount,
    int FailedClusterCount,
    string[] FailedClusterIds);
