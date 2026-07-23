namespace ThermalWatch.Telegram;

internal enum VisibilityRejectionReason
{
    Nighttime,
    InsufficientDetections,
    LowConfidence,
    LowFrp,
    LowThermalContrast,
    MissingRequiredValue,
    PreviewUnavailable
}
