namespace ThermalWatch.Api;

internal interface IFirmsRefreshCycle
{
    Task<FirmsRefreshCycleResult> RefreshAsync(CancellationToken cancellationToken);
}
