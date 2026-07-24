namespace ThermalWatch.Core;

public sealed record AutomaticNotificationProcessingResult(
    bool ContinueProcessing,
    AutomaticNotificationProcessingSummary Summary);
