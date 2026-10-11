namespace ThermalWatch.Core;

/// <summary>The refresh state of one independently retained country/source segment.</summary>
/// <param name="CountryCode">The ingestion country's three-letter code.</param>
/// <param name="Source">The FIRMS dataset identifier.</param>
/// <param name="LastAttemptAtUtc">The most recent refresh attempt, if any.</param>
/// <param name="LastSuccessAtUtc">The most recent complete refresh, if any.</param>
/// <param name="IsStale">Whether the segment is missing or its latest refresh failed.</param>
/// <param name="Error">The latest credential-free failure description.</param>
/// <param name="IngestionMode">The acquisition mode of the retained data.</param>
public sealed record SegmentStatus(
    string CountryCode,
    string Source,
    DateTimeOffset? LastAttemptAtUtc,
    DateTimeOffset? LastSuccessAtUtc,
    bool IsStale,
    string? Error,
    string IngestionMode
);
