namespace ThermalWatch.Api.Polling;

internal interface IFirmsRefreshCycle
{
    Task<FirmsRefreshCycleResult> RefreshAsync(CancellationToken cancellationToken);
}
