using Microsoft.Extensions.Logging;
using ThermalWatch.Core;

namespace ThermalWatch.Api;

internal sealed class FirmsHistoryBackfill(
    FirmsClient firmsClient,
    FirmsOptions options,
    FirmsHistoryStore historyStore,
    TimeProvider timeProvider,
    ILogger logger) : IFirmsHistoryBackfill
{
    private const int MaximumConcurrentRequests = 2;

    public async Task<FirmsHistoryBackfillResult> RefreshAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        DateOnly retainedFromDate = today.AddDays(-FirmsHistoryStore.CompletedDayCount);
        HistoryRequest[] requests =
        [
            .. options.CountryCodes
                .SelectMany(countryCode => FirmsSources.All.Select(source => new SegmentKey(countryCode, source)))
                .SelectMany(key => Enumerable.Range(start: 0, FirmsHistoryStore.CompletedDayCount / FirmsClient.MaximumDayRange)
                    .Select(index => new HistoryRequest(
                        key,
                        retainedFromDate.AddDays(index * FirmsClient.MaximumDayRange),
                        FirmsClient.MaximumDayRange)))
                .Where(request => historyStore.NeedsRefresh(
                    request.Key,
                    request.StartDate,
                    request.DayCount))
        ];
        if (requests.Length == 0)
            return new(SuccessfulRequestCount: 0, FailedRequestCount: 0);

        var results = new FirmsHistoryRangeRefreshResult[requests.Length];
        await Parallel.ForEachAsync(
            Enumerable.Range(start: 0, requests.Length),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = MaximumConcurrentRequests
            },
            async (index, token) => results[index] = await RefreshAsync(
                requests[index],
                token).ConfigureAwait(false)).ConfigureAwait(false);

        historyStore.Publish(results);
        int succeeded = results.Count(result => result.Segment.Succeeded);
        return new(succeeded, requests.Length - succeeded);
    }

    private async ValueTask<FirmsHistoryRangeRefreshResult> RefreshAsync(
        HistoryRequest request,
        CancellationToken cancellationToken)
    {
        DateTimeOffset attemptedAtUtc = timeProvider.GetUtcNow();
        DateOnly endDate = request.StartDate.AddDays(request.DayCount - 1);
        SegmentRefreshResult result;
        try
        {
            FirmsSegmentResult segment = await firmsClient.GetSegmentAsync(
                request.Key.CountryCode,
                request.Key.Source,
                request.StartDate,
                request.DayCount,
                cancellationToken).ConfigureAwait(false);
            result = SegmentRefreshResult.Success(
                request.Key,
                attemptedAtUtc,
                timeProvider.GetUtcNow(),
                segment.Anomalies,
                segment.IngestionMode);
            FirmsPollingLog.HistoryRangeRefreshed(
                logger,
                request.Key.CountryCode,
                request.Key.Source,
                request.StartDate,
                endDate,
                segment.Anomalies.Length);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FirmsRequestException exception)
        {
            result = SegmentRefreshResult.Failure(
                request.Key,
                attemptedAtUtc,
                timeProvider.GetUtcNow(),
                exception.SafeMessage);
        }
        catch (Exception)
        {
            result = SegmentRefreshResult.Failure(
                request.Key,
                attemptedAtUtc,
                timeProvider.GetUtcNow(),
                error: "Unexpected FIRMS client failure.");
        }

        if (result.Succeeded)
            return new(request.StartDate, request.DayCount, result);

        FirmsPollingLog.HistoryRangeRefreshFailed(
            logger,
            request.Key.CountryCode,
            request.Key.Source,
            request.StartDate,
            endDate,
            result.Error!);
        return new(request.StartDate, request.DayCount, result);
    }

    private readonly record struct HistoryRequest(
        SegmentKey Key,
        DateOnly StartDate,
        int DayCount);
}
