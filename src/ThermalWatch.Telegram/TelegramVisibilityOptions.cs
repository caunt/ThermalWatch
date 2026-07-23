namespace ThermalWatch.Telegram;

public sealed record TelegramVisibilityOptions(
    bool Enabled,
    double MinimumFrpMegawatts,
    double MinimumThermalContrastKelvin,
    int MinimumClusterDetections,
    double MinimumModisConfidencePercent,
    ViirsConfidenceLevel MinimumViirsConfidence,
    bool RequireDaytime,
    bool RequirePreview);
