namespace ThermalWatch.Core;

public sealed record SegmentStatus(
    string CountryCode,
    string Source,
    DateTimeOffset? LastAttemptAtUtc,
    DateTimeOffset? LastSuccessAtUtc,
    bool IsStale,
    string? Error,
    string IngestionMode);
