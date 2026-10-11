namespace ThermalWatch.Core;

/// <summary>The vegetation-suppression thresholds and explicitly enabled exceptions.</summary>
/// <param name="Enabled">Whether NASA land-cover selection is active.</param>
/// <param name="VegetationPercentThreshold">The vegetation percentage at which suppression begins.</param>
/// <param name="BuiltUpProximityKilometers">The sampling distance for nearby urban/built-up class 13.</param>
/// <param name="VegetationMaximumFrpMegawatts">The representative FRP threshold for the high-FRP exception.</param>
/// <param name="KeepHighFrpVegetation">Whether high-FRP vegetation clusters bypass suppression.</param>
/// <param name="KeepMultiSatelliteVegetation">Whether observations from multiple satellites bypass suppression.</param>
public sealed record NotificationLandCoverOptions(
    bool Enabled,
    double VegetationPercentThreshold,
    double BuiltUpProximityKilometers,
    double VegetationMaximumFrpMegawatts,
    bool KeepHighFrpVegetation,
    bool KeepMultiSatelliteVegetation
);
