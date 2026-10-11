namespace ThermalWatch.Api.Polling;

internal sealed record FirmsRefreshCycleResult(int SuccessfulSegmentCount, int FailedSegmentCount);
