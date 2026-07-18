using System.Collections.Immutable;

namespace ThermalWatch.Core;

public static class FirmsSources
{
    public static readonly ImmutableArray<string> All =
    [
        "MODIS_NRT",
        "VIIRS_SNPP_NRT",
        "VIIRS_NOAA20_NRT",
        "VIIRS_NOAA21_NRT"
    ];
}

public readonly record struct SegmentKey(string CountryCode, string Source);

public static class IngestionModes
{
    public const string Country = "country";
    public const string AreaFallback = "areaFallback";
    public const string None = "none";
}

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

public sealed record SourceStatus(
    string Country,
    string Source,
    DateTimeOffset? LastAttemptUtc,
    DateTimeOffset? LastSuccessUtc,
    bool Stale,
    string? Error,
    string IngestionMode);

public sealed record AnomalySnapshot(
    DateTimeOffset GeneratedAtUtc,
    double ActiveWindowHours,
    bool IsReady,
    bool IsPartiallyStale,
    ImmutableArray<string> ConfiguredCountries,
    ImmutableArray<SourceStatus> Sources,
    int Count,
    ImmutableArray<Anomaly> Items);

public sealed record SegmentRefreshResult(
    SegmentKey Key,
    DateTimeOffset AttemptedAtUtc,
    DateTimeOffset CompletedAtUtc,
    bool Succeeded,
    ImmutableArray<Anomaly> Detections,
    string? Error,
    string IngestionMode)
{
    public static SegmentRefreshResult Success(
        SegmentKey key,
        DateTimeOffset attemptedAtUtc,
        DateTimeOffset completedAtUtc,
        ImmutableArray<Anomaly> detections,
        string ingestionMode) =>
        new(key, attemptedAtUtc, completedAtUtc, true, detections, null, ingestionMode);

    public static SegmentRefreshResult Failure(
        SegmentKey key,
        DateTimeOffset attemptedAtUtc,
        DateTimeOffset completedAtUtc,
        string error) =>
        new(key, attemptedAtUtc, completedAtUtc, false, [], error, IngestionModes.None);
}

public sealed record FirmsSegmentResult(
    ImmutableArray<Anomaly> Detections,
    string IngestionMode);

public sealed record NotificationCluster(
    string Id,
    Anomaly Representative,
    ImmutableArray<Anomaly> Members);

public sealed record GibsPreview(byte[]? PngBytes)
{
    public bool IsAvailable => PngBytes is { Length: > 0 };

    public static GibsPreview Unavailable { get; } = new((byte[]?)null);
}
