using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThermalWatch.Core;

namespace ThermalWatch.Api;

public sealed class FirmsPollingService : BackgroundService
{
    private readonly ILogger<FirmsPollingService> _logger;
    private readonly FirmsOptions _options;
    private readonly IFirmsHistoryBackfill _historyBackfill;
    private readonly IFirmsRefreshCycle _refreshCycle;
    private readonly FirmsPollingSchedule _schedule;
    private readonly TimeProvider _timeProvider;

    public FirmsPollingService(
        FirmsClient firmsClient,
        FirmsOptions options,
        AnomalySnapshotStore snapshotStore,
        FirmsHistoryStore historyStore,
        TimeProvider timeProvider,
        ILogger<FirmsPollingService> logger) : this(
            new FirmsRefreshCycle(firmsClient, options, snapshotStore, historyStore, timeProvider, logger),
            new FirmsHistoryBackfill(firmsClient, options, historyStore, timeProvider, logger),
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
        ILogger<FirmsPollingService> logger) : this(
            refreshCycle,
            NoOpHistoryBackfill.Instance,
            options,
            schedule,
            timeProvider,
            logger)
    {
    }

    internal FirmsPollingService(
        IFirmsRefreshCycle refreshCycle,
        IFirmsHistoryBackfill historyBackfill,
        FirmsOptions options,
        FirmsPollingSchedule schedule,
        TimeProvider timeProvider,
        ILogger<FirmsPollingService> logger)
    {
        _refreshCycle = refreshCycle;
        _historyBackfill = historyBackfill;
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
            FirmsHistoryBackfillResult historyResult = await _historyBackfill.RefreshAsync(stoppingToken).ConfigureAwait(false);
            if (historyResult.AttemptedRequestCount > 0)
            {
                FirmsPollingLog.HistoryBackfillCompleted(
                    _logger,
                    historyResult.SuccessfulRequestCount,
                    historyResult.FailedRequestCount);
            }
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

    private sealed class NoOpHistoryBackfill : IFirmsHistoryBackfill
    {
        public static NoOpHistoryBackfill Instance { get; } = new();

        public Task<FirmsHistoryBackfillResult> RefreshAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new FirmsHistoryBackfillResult(
                SuccessfulRequestCount: 0,
                FailedRequestCount: 0));
        }
    }
}
