namespace ThermalWatch.Core;

/// <summary>A validated FIRMS thermal observation; it does not establish a wildfire or ongoing event.</summary>
/// <param name="Id">The deterministic observation identifier.</param>
/// <param name="CountryCode">The ingestion country's three-letter code.</param>
/// <param name="Source">The FIRMS dataset identifier.</param>
/// <param name="Satellite">The satellite reported by FIRMS.</param>
/// <param name="Instrument">The observing instrument.</param>
/// <param name="Latitude">The WGS84 latitude in degrees.</param>
/// <param name="Longitude">The WGS84 longitude in degrees.</param>
/// <param name="AcquiredAtUtc">The acquisition timestamp in UTC.</param>
/// <param name="DayNight">The FIRMS daytime or nighttime classification.</param>
/// <param name="BrightnessKelvin">The primary thermal-channel brightness temperature, when available.</param>
/// <param name="SecondaryBrightnessKelvin">The secondary thermal-channel brightness temperature, when available.</param>
/// <param name="FrpMegawatts">The reported fire radiative power, when available.</param>
/// <param name="ScanKilometers">The scan footprint dimension, when available.</param>
/// <param name="TrackKilometers">The track footprint dimension, when available.</param>
/// <param name="ConfidenceRaw">The original FIRMS confidence value.</param>
/// <param name="ConfidencePercent">The numeric MODIS confidence, when available.</param>
/// <param name="ConfidenceCategory">The normalized VIIRS confidence category, when available.</param>
/// <param name="Version">The upstream processing version.</param>
/// <param name="GoogleMapsUrl">The coordinate navigation address.</param>
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
    Uri GoogleMapsUrl
)
{
    /// <summary>Gets the primary minus secondary brightness temperature, or null when either is unavailable.</summary>
    public double? ThermalContrastKelvin =>
        BrightnessKelvin is { } brightness && SecondaryBrightnessKelvin is { } secondaryBrightness
            ? brightness - secondaryBrightness
            : null;
}
