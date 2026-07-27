namespace ThermalWatch.Core;

public sealed record FirmsHistoryRangeRefreshResult(
    DateOnly StartDate,
    int DayCount,
    SegmentRefreshResult Segment);
