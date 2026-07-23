using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ThermalWatch.Core;

namespace ThermalWatch.Api;

public sealed class FirmsPollingService(
    FirmsClient firmsClient,
    FirmsOptions options,
    AnomalySnapshotStore snapshotStore,
    TimeProvider timeProvider,
    ILogger<FirmsPollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(options.PollInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            await RefreshAsync(stoppingToken).ConfigureAwait(false);
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        SegmentKey[] keys = [.. options.Countries.SelectMany(country => FirmsSources.All.Select(source => new SegmentKey(country, source)))];
        var results = new SegmentRefreshResult[keys.Length];

        await RefreshSegmentAsync(keys, results, index: 0, cancellationToken).ConfigureAwait(false);
        await Parallel.ForEachAsync(
            Enumerable.Range(start: 1, keys.Length - 1),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = options.MaxConcurrency
            },
            async (index, token) => await RefreshSegmentAsync(
                keys,
                results,
                index,
                token).ConfigureAwait(false)).ConfigureAwait(false);

        AnomalySnapshot snapshot = snapshotStore.Publish(results);
        FirmsPollingLog.SnapshotPublished(
            logger,
            snapshot.Count,
            snapshot.IsPartiallyStale);
    }

    private async ValueTask RefreshSegmentAsync(
        SegmentKey[] keys,
        SegmentRefreshResult[] results,
        int index,
        CancellationToken cancellationToken)
    {
        SegmentKey key = keys[index];
        DateTimeOffset attemptedAtUtc = timeProvider.GetUtcNow();

        try
        {
            FirmsSegmentResult segment = await firmsClient.GetSegmentAsync(
                key.CountryCode,
                key.Source,
                cancellationToken).ConfigureAwait(false);
            results[index] = SegmentRefreshResult.Success(
                key,
                attemptedAtUtc,
                timeProvider.GetUtcNow(),
                segment.Detections,
                segment.IngestionMode);
            FirmsPollingLog.SegmentRefreshed(
                logger,
                key.CountryCode,
                key.Source,
                segment.IngestionMode,
                segment.Detections.Length);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FirmsRequestException exception)
        {
            RecordFailure(results, index, key, attemptedAtUtc, exception.SafeMessage);
        }
        catch (Exception)
        {
            const string safeError = "Unexpected FIRMS client failure.";
            RecordFailure(results, index, key, attemptedAtUtc, safeError);
        }
    }

    private void RecordFailure(
        SegmentRefreshResult[] results,
        int index,
        SegmentKey key,
        DateTimeOffset attemptedAtUtc,
        string safeError)
    {
        results[index] = SegmentRefreshResult.Failure(
            key,
            attemptedAtUtc,
            timeProvider.GetUtcNow(),
            safeError);
        FirmsPollingLog.SegmentRefreshFailed(
            logger,
            key.CountryCode,
            key.Source,
            safeError);
    }
}
