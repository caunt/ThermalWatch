namespace ThermalWatch.Core;

public sealed record NotificationLandCoverOptions(
    bool Enabled,
    double VegetationPercentThreshold,
    double BuiltUpProximityKilometers,
    double VegetationMaximumFrpMegawatts,
    bool KeepHighFrpVegetation,
    bool KeepMultiSatelliteVegetation);
