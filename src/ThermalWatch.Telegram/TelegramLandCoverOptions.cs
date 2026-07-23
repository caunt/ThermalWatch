namespace ThermalWatch.Telegram;

public sealed record TelegramLandCoverOptions(
    bool Enabled,
    double VegetationPercentThreshold,
    double BuiltUpProximityKilometers,
    double VegetationMaximumFrpMegawatts,
    bool KeepHighFrpVegetation,
    bool KeepMultiSatelliteVegetation);
