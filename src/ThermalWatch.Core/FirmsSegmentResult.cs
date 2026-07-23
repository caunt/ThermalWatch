using System.Collections.Immutable;

namespace ThermalWatch.Core;

public sealed record FirmsSegmentResult(
    ImmutableArray<Anomaly> Detections,
    string IngestionMode);
