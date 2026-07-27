namespace ThermalWatch.Api;

internal sealed record FirmsHistoryBackfillResult(
    int SuccessfulRequestCount,
    int FailedRequestCount)
{
    public int AttemptedRequestCount => SuccessfulRequestCount + FailedRequestCount;
}
