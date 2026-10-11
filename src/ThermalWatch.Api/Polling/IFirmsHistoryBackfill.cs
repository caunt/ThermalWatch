namespace ThermalWatch.Api.Polling;

internal interface IFirmsHistoryBackfill
{
    Task<FirmsHistoryBackfillResult> RefreshAsync(CancellationToken cancellationToken);
}
