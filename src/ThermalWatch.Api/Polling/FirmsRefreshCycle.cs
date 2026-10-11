using ThermalWatch.Core;

namespace ThermalWatch.Api.Polling;

internal sealed class FirmsRefreshCycle(
    FirmsClient firmsClient,
    FirmsOptions options,
    AnomalySnapshotStore snapshotStore,
    FirmsHistoryStore historyStore,
    TimeProvider timeProvider,
    ILogger logger
) : IFirmsRefreshCycle
{
    private const int MaximumConcurrentSegments = 2;

    public async Task<FirmsRefreshCycleResult> RefreshAsync(CancellationToken cancellationToken)
    {
        DateOnly endDate = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        int dayCount = checked((int)Math.Ceiling(options.ActiveWindow.TotalDays) + 1);
        DateOnly startDate = endDate.AddDays(1 - dayCount);
        List<SegmentKey> segmentKeys = [];

        foreach (string countryCode in options.CountryCodes)
        {
            foreach (string source in FirmsSources.All)
                segmentKeys.Add(new(countryCode, source));
        }

        SegmentKey[] keys = [.. segmentKeys];
        SegmentRefreshResult[] results = new SegmentRefreshResult[keys.Length];

        await RefreshSegmentAsync(keys, results, index: 0, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        await Parallel.ForEachAsync(
            Enumerable.Range(start: 1, keys.Length - 1),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = MaximumConcurrentSegments
            },
            async (index, token) => await RefreshSegmentAsync(keys, results, index, token).ConfigureAwait(continueOnCapturedContext: false)
        ).ConfigureAwait(continueOnCapturedContext: false);

        FirmsHistory history = historyStore.Publish(startDate, dayCount, results);
        FirmsPollingLog.HistoryPublished(logger, history.IsReady, history.IsPartiallyStale);
        AnomalySnapshot snapshot = snapshotStore.Publish(results);
        FirmsPollingLog.SnapshotPublished(logger, snapshot.AnomalyCount, snapshot.IsPartiallyStale);

        int successfulSegmentCount = results.Count(result => result.Succeeded);

        return new(successfulSegmentCount, results.Length - successfulSegmentCount);
    }

    private void RecordFailure(SegmentRefreshResult[] results, int index, SegmentKey key, DateTimeOffset attemptedAtUtc, string safeError)
    {
        results[index] = SegmentRefreshResult.Failure(key, attemptedAtUtc, timeProvider.GetUtcNow(), safeError);
        FirmsPollingLog.SegmentRefreshFailed(logger, key.CountryCode, key.Source, safeError);
    }

    private async ValueTask RefreshSegmentAsync(SegmentKey[] keys, SegmentRefreshResult[] results, int index, CancellationToken cancellationToken)
    {
        SegmentKey key = keys[index];
        DateTimeOffset attemptedAtUtc = timeProvider.GetUtcNow();

        try
        {
            FirmsSegmentResult segment = await firmsClient.GetSegmentAsync(key.CountryCode, key.Source, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            results[index] = SegmentRefreshResult.Success(key, attemptedAtUtc, timeProvider.GetUtcNow(), segment.Anomalies, segment.IngestionMode);
            FirmsPollingLog.SegmentRefreshed(logger, key.CountryCode, key.Source, segment.IngestionMode, segment.Anomalies.Length);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FirmsRequestException exception)
        {
            RecordFailure(results, index, key, attemptedAtUtc, exception.SafeMessage);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException or OperationCanceledException)
        {
            const string safeError = "Unexpected FIRMS client failure.";
            RecordFailure(results, index, key, attemptedAtUtc, safeError);
        }
    }
}
