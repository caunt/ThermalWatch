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
        await RefreshAsync(stoppingToken);

        using var timer = new PeriodicTimer(options.PollInterval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RefreshAsync(stoppingToken);
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var keys = options.Countries
            .SelectMany(country => FirmsSources.All.Select(source => new SegmentKey(country, source)))
            .ToArray();
        var results = new SegmentRefreshResult[keys.Length];

        await RefreshSegmentAsync(0, cancellationToken);
        await Parallel.ForEachAsync(
            Enumerable.Range(1, keys.Length - 1),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = options.MaxConcurrency
            },
            async (index, token) => await RefreshSegmentAsync(index, token));

        var snapshot = snapshotStore.Publish(results);
        logger.LogInformation(
            "Published FIRMS snapshot with {DetectionCount} detections; partially stale: {IsPartiallyStale}",
            snapshot.Count,
            snapshot.IsPartiallyStale);

        async ValueTask RefreshSegmentAsync(int index, CancellationToken token)
        {
            var key = keys[index];
            var attemptedAtUtc = timeProvider.GetUtcNow();

            try
            {
                var segment = await firmsClient.GetSegmentAsync(
                    key.CountryCode,
                    key.Source,
                    token);
                results[index] = SegmentRefreshResult.Success(
                    key,
                    attemptedAtUtc,
                    timeProvider.GetUtcNow(),
                    segment.Detections,
                    segment.IngestionMode);
                logger.LogInformation(
                    "Refreshed FIRMS segment {Country} {Source} in {IngestionMode} mode with {DetectionCount} detections",
                    key.CountryCode,
                    key.Source,
                    segment.IngestionMode,
                    segment.Detections.Length);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (FirmsRequestException exception)
            {
                results[index] = SegmentRefreshResult.Failure(
                    key,
                    attemptedAtUtc,
                    timeProvider.GetUtcNow(),
                    exception.SafeMessage);
                logger.LogWarning(
                    "FIRMS segment refresh failed for {Country} {Source}: {SafeError}",
                    key.CountryCode,
                    key.Source,
                    exception.SafeMessage);
            }
            catch (Exception)
            {
                const string safeError = "Unexpected FIRMS client failure.";
                results[index] = SegmentRefreshResult.Failure(
                    key,
                    attemptedAtUtc,
                    timeProvider.GetUtcNow(),
                    safeError);
                logger.LogWarning(
                    "FIRMS segment refresh failed for {Country} {Source}: {SafeError}",
                    key.CountryCode,
                    key.Source,
                    safeError);
            }
        }
    }
}
