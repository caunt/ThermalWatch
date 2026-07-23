using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThermalWatch.Core;

namespace ThermalWatch.Api;

public sealed class FirmsPollingService : BackgroundService
{
    private readonly ILogger<FirmsPollingService> _logger;
    private readonly FirmsOptions _options;
    private readonly IFirmsRefreshCycle _refreshCycle;
    private readonly FirmsPollingSchedule _schedule;
    private readonly TimeProvider _timeProvider;

    public FirmsPollingService(
        FirmsClient firmsClient,
        FirmsOptions options,
        AnomalySnapshotStore snapshotStore,
        TimeProvider timeProvider,
        ILogger<FirmsPollingService> logger) : this(
            new FirmsRefreshCycle(firmsClient, options, snapshotStore, timeProvider, logger),
            options,
            new FirmsPollingSchedule(),
            timeProvider,
            logger)
    {
    }

    internal FirmsPollingService(
        IFirmsRefreshCycle refreshCycle,
        FirmsOptions options,
        FirmsPollingSchedule schedule,
        TimeProvider timeProvider,
        ILogger<FirmsPollingService> logger)
    {
        _refreshCycle = refreshCycle;
        _options = options;
        _schedule = schedule;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int consecutiveTotalFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            long startedTimestamp = _timeProvider.GetTimestamp();
            FirmsRefreshCycleResult result = await _refreshCycle.RefreshAsync(stoppingToken).ConfigureAwait(false);
            consecutiveTotalFailures = result.SuccessfulSegmentCount == 0
                ? consecutiveTotalFailures + 1
                : 0;
            TimeSpan delay = _schedule.CalculateDelay(_options.PollInterval, consecutiveTotalFailures);
            TimeSpan elapsed = _timeProvider.GetElapsedTime(startedTimestamp);

            FirmsPollingLog.CycleCompleted(
                _logger,
                elapsed,
                result.SuccessfulSegmentCount,
                result.FailedSegmentCount,
                delay,
                isTotalFailureBackoffActive: consecutiveTotalFailures > 0);

            await Task.Delay(delay, _timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }
}
