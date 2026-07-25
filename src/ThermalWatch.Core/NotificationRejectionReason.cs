namespace ThermalWatch.Core;

public enum NotificationRejectionReason
{
    Nighttime,
    InsufficientDetections,
    LowConfidence,
    LowFrp,
    LowClusterTotalFrp,
    LowThermalContrast,
    MissingRequiredValue,
    PreviewUnavailable
}
