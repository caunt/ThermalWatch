namespace ThermalWatch.Core;

public enum NotificationRejectionReason
{
    Nighttime,
    InsufficientDetections,
    LowConfidence,
    LowFrp,
    LowThermalContrast,
    MissingRequiredValue,
    PreviewUnavailable
}
