namespace ThermalWatch.Core;

public sealed record NotificationAutomaticProcessingResult(
    bool ContinueProcessing,
    NotificationProcessingSummary Summary);
