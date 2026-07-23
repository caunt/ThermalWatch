namespace ThermalWatch.Core;

public sealed record Anomaly(
    string Id,
    string CountryCode,
    string Source,
    string Satellite,
    string Instrument,
    double Latitude,
    double Longitude,
    DateTimeOffset AcquiredAtUtc,
    string DayNight,
    double? BrightnessKelvin,
    double? SecondaryBrightnessKelvin,
    double? FrpMegawatts,
    double? ScanKilometers,
    double? TrackKilometers,
    string? ConfidenceRaw,
    double? ConfidencePercent,
    string? ConfidenceCategory,
    string? Version,
    string GoogleMapsUrl)
{
    public double? ThermalContrastKelvin =>
        BrightnessKelvin is { } brightness && SecondaryBrightnessKelvin is { } secondaryBrightness
            ? brightness - secondaryBrightness
            : null;
}
