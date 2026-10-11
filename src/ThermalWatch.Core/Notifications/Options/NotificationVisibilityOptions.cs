namespace ThermalWatch.Core;

/// <summary>The metadata and exact-preview requirements for notification eligibility.</summary>
/// <param name="Enabled">Whether metadata selection is active.</param>
/// <param name="MinimumFrpMegawatts">The representative FRP threshold; zero disables this criterion.</param>
/// <param name="MinimumClusterTotalFrpMegawatts">The total cluster FRP threshold; zero disables this criterion.</param>
/// <param name="MinimumThermalContrastKelvin">The representative thermal-contrast threshold; zero disables this criterion.</param>
/// <param name="MinimumClusterDetections">The required cluster member count.</param>
/// <param name="MinimumModisConfidencePercent">The MODIS numeric confidence threshold.</param>
/// <param name="MinimumViirsConfidence">The VIIRS categorical confidence threshold.</param>
/// <param name="RequireDaytime">Whether the representative must be a daytime observation.</param>
/// <param name="RequirePreview">Whether unavailable exact-date imagery blocks eligibility.</param>
public sealed record NotificationVisibilityOptions(
    bool Enabled,
    double MinimumFrpMegawatts,
    double MinimumClusterTotalFrpMegawatts,
    double MinimumThermalContrastKelvin,
    int MinimumClusterDetections,
    double MinimumModisConfidencePercent,
    NotificationViirsConfidenceLevel MinimumViirsConfidence,
    bool RequireDaytime,
    bool RequirePreview
);
