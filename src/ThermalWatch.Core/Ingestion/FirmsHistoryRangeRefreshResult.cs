namespace ThermalWatch.Core;

/// <summary>A segment result covering a complete requested UTC-date range.</summary>
/// <param name="StartDate">The first covered UTC date.</param>
/// <param name="DayCount">The number of consecutive covered dates.</param>
/// <param name="Segment">The complete success or failure for the country/source segment.</param>
public sealed record FirmsHistoryRangeRefreshResult(DateOnly StartDate, int DayCount, SegmentRefreshResult Segment);
