namespace ThermalWatch.Api.Polling;

internal sealed record FirmsHistoryBackfillResult(int SuccessfulRequestCount, int FailedRequestCount)
{
    public int AttemptedRequestCount => SuccessfulRequestCount + FailedRequestCount;
}
