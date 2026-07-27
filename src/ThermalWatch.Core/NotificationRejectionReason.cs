namespace ThermalWatch.Core;

public enum NotificationRejectionReason
{
    Nighttime,
    InsufficientDetections,
    LowConfidence,
    LowFrp,
    LowClusterTotalFrp,
    LowThermalContrast,
    HistoricalFrpNotHigher,
    HistoryUnavailable,
    MissingRequiredValue,
    PreviewUnavailable
}
