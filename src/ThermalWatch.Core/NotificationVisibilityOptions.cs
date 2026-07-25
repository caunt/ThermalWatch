namespace ThermalWatch.Core;

public sealed record NotificationVisibilityOptions(
    bool Enabled,
    double MinimumFrpMegawatts,
    double MinimumClusterTotalFrpMegawatts,
    double MinimumThermalContrastKelvin,
    int MinimumClusterDetections,
    double MinimumModisConfidencePercent,
    NotificationViirsConfidenceLevel MinimumViirsConfidence,
    bool RequireDaytime,
    bool RequirePreview);
