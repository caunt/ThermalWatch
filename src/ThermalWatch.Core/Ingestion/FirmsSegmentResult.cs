using System.Collections.Immutable;

namespace ThermalWatch.Core;

/// <summary>A complete, validated segment returned by the FIRMS client.</summary>
/// <param name="Anomalies">The segment's observations.</param>
/// <param name="IngestionMode">The successful country or area-fallback acquisition mode.</param>
public sealed record FirmsSegmentResult(ImmutableArray<Anomaly> Anomalies, string IngestionMode);
