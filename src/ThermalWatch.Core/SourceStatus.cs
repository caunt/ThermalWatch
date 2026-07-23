namespace ThermalWatch.Core;

public sealed record SourceStatus(
    string Country,
    string Source,
    DateTimeOffset? LastAttemptUtc,
    DateTimeOffset? LastSuccessUtc,
    bool Stale,
    string? Error,
    string IngestionMode);
