namespace ThermalWatch.Api;

internal sealed record FirmsRefreshCycleResult(
    int SuccessfulSegmentCount,
    int FailedSegmentCount);
