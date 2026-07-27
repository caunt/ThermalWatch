namespace ThermalWatch.Api;

internal interface IFirmsHistoryBackfill
{
    Task<FirmsHistoryBackfillResult> RefreshAsync(CancellationToken cancellationToken);
}
