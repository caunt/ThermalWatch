using System.Collections.Immutable;

namespace ThermalWatch.Core;

/// <summary>A complete polling attempt for one independently retained segment.</summary>
/// <param name="Key">The country/source segment identity.</param>
/// <param name="AttemptedAtUtc">The start of the refresh attempt.</param>
/// <param name="CompletedAtUtc">The end of the refresh attempt.</param>
/// <param name="Succeeded">Whether a complete validated result was obtained.</param>
/// <param name="Anomalies">The replacement observations on success.</param>
/// <param name="Error">The credential-free failure description.</param>
/// <param name="IngestionMode">The successful acquisition mode, or none on failure.</param>
public sealed record SegmentRefreshResult(
    SegmentKey Key,
    DateTimeOffset AttemptedAtUtc,
    DateTimeOffset CompletedAtUtc,
    bool Succeeded,
    ImmutableArray<Anomaly> Anomalies,
    string? Error,
    string IngestionMode
)
{
    /// <summary>Creates a failed attempt that leaves previously complete segment data retained.</summary>
    /// <param name="key">The segment identity.</param>
    /// <param name="attemptedAtUtc">The attempt start.</param>
    /// <param name="completedAtUtc">The attempt completion.</param>
    /// <param name="error">The credential-free failure description.</param>
    public static SegmentRefreshResult Failure(SegmentKey key, DateTimeOffset attemptedAtUtc, DateTimeOffset completedAtUtc, string error)
    {
        return new(key, attemptedAtUtc, completedAtUtc, Succeeded: false, [], error, IngestionModes.None);
    }

    /// <summary>Creates a complete segment replacement.</summary>
    /// <param name="key">The segment identity.</param>
    /// <param name="attemptedAtUtc">The attempt start.</param>
    /// <param name="completedAtUtc">The successful completion.</param>
    /// <param name="anomalies">The validated observations, including an empty successful result.</param>
    /// <param name="ingestionMode">The country or area-fallback acquisition mode.</param>
    public static SegmentRefreshResult Success(
        SegmentKey key,
        DateTimeOffset attemptedAtUtc,
        DateTimeOffset completedAtUtc,
        ImmutableArray<Anomaly> anomalies,
        string ingestionMode
    )
    {
        return new(key, attemptedAtUtc, completedAtUtc, Succeeded: true, anomalies, Error: null, ingestionMode);
    }
}
