using System.Collections.Immutable;

namespace ThermalWatch.Core;

public sealed class FirmsHistoryStore
{
    public const int CompletedDayCount = 30;
    public const int RetainedDayCount = CompletedDayCount + 1;

    private readonly TimeProvider _timeProvider;
    private readonly NotificationOptions _notificationOptions;
    private readonly SegmentKey[] _orderedKeys;
    private readonly Dictionary<HistorySegmentKey, HistorySegmentState> _segments = [];
    private readonly Lock _sync = new();
    private FirmsHistory _current;

    public FirmsHistoryStore(
        FirmsOptions firmsOptions,
        NotificationOptions notificationOptions,
        TimeProvider timeProvider)
    {
        _notificationOptions = notificationOptions;
        _timeProvider = timeProvider;
        _orderedKeys =
        [
            .. firmsOptions.CountryCodes
                .SelectMany(countryCode => FirmsSources.All.Select(source => new SegmentKey(countryCode, source)))
        ];

        DateTimeOffset now = _timeProvider.GetUtcNow();
        EnsureRetainedDates(GetUtcDate(now));
        _current = CreateHistory(now);
    }

    public FirmsHistory Current
    {
        get
        {
            lock (_sync)
            {
                DateTimeOffset now = _timeProvider.GetUtcNow();
                if (EnsureRetainedDates(GetUtcDate(now)))
                    _current = CreateHistory(now);

                return _current;
            }
        }
    }

    public bool NeedsRefresh(SegmentKey key, DateOnly startDate, int dayCount)
    {
        lock (_sync)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (EnsureRetainedDates(GetUtcDate(now)))
                _current = CreateHistory(now);

            for (int offset = 0; offset < dayCount; offset++)
            {
                DateOnly date = startDate.AddDays(offset);
                if (!_segments.TryGetValue(new(date, key), out HistorySegmentState? state)
                    || state.Status.LastSuccessAtUtc is null
                    || state.Status.IsStale)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public FirmsHistory Publish(
        DateOnly startDate,
        int dayCount,
        IReadOnlyCollection<SegmentRefreshResult> results)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dayCount, other: 1);
        FirmsHistoryRangeRefreshResult[] ranges =
        [
            .. results.Select(result => new FirmsHistoryRangeRefreshResult(startDate, dayCount, result))
        ];
        return Publish(ranges);
    }

    public FirmsHistory Publish(IReadOnlyCollection<FirmsHistoryRangeRefreshResult> ranges)
    {
        lock (_sync)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            EnsureRetainedDates(GetUtcDate(now));
            foreach (FirmsHistoryRangeRefreshResult range in ranges)
                ApplyRange(range);

            _current = CreateHistory(now);
            return _current;
        }
    }

    private void ApplyRange(FirmsHistoryRangeRefreshResult range)
    {
        int dayCount = range.DayCount;
        DateOnly endDate = range.StartDate.AddDays(dayCount - 1);
        SegmentRefreshResult result = range.Segment;
        for (DateOnly date = range.StartDate; date <= endDate; date = date.AddDays(1))
        {
            var historyKey = new HistorySegmentKey(date, result.Key);
            if (!_segments.TryGetValue(historyKey, out HistorySegmentState? existing))
                continue;

            if (result.Succeeded)
            {
                ImmutableArray<Anomaly> anomalies =
                [
                    .. result.Anomalies
                        .Where(anomaly => GetUtcDate(anomaly.AcquiredAtUtc) == date)
                        .DistinctBy(anomaly => anomaly.Id, StringComparer.Ordinal)
                        .OrderByDescending(anomaly => anomaly.AcquiredAtUtc)
                        .ThenBy(anomaly => anomaly.Id, StringComparer.Ordinal)
                ];
                _segments[historyKey] = new(
                    anomalies,
                    new(
                        result.Key.CountryCode,
                        result.Key.Source,
                        result.AttemptedAtUtc,
                        result.CompletedAtUtc,
                        IsStale: false,
                        Error: null,
                        result.IngestionMode));
                continue;
            }

            _segments[historyKey] = existing with
            {
                Status = existing.Status with
                {
                    LastAttemptAtUtc = result.AttemptedAtUtc,
                    IsStale = true,
                    Error = result.Error
                }
            };
        }
    }

    private bool EnsureRetainedDates(DateOnly today)
    {
        DateOnly retainedFromDate = today.AddDays(-CompletedDayCount);
        bool changed = false;
        HistorySegmentKey[] expired =
        [
            .. _segments.Keys.Where(key => key.Date < retainedFromDate || key.Date > today)
        ];
        foreach (HistorySegmentKey key in expired)
        {
            _segments.Remove(key);
            changed = true;
        }

        for (DateOnly date = retainedFromDate; date <= today; date = date.AddDays(1))
        {
            foreach (SegmentKey key in _orderedKeys)
            {
                var historyKey = new HistorySegmentKey(date, key);
                if (_segments.ContainsKey(historyKey))
                    continue;

                _segments.Add(
                    historyKey,
                    new(
                        [],
                        new(
                            key.CountryCode,
                            key.Source,
                            LastAttemptAtUtc: null,
                            LastSuccessAtUtc: null,
                            IsStale: true,
                            Error: null,
                            IngestionModes.None)));
                changed = true;
            }
        }

        return changed;
    }

    private FirmsHistory CreateHistory(DateTimeOffset now)
    {
        DateOnly today = GetUtcDate(now);
        DateOnly retainedFromDate = today.AddDays(-CompletedDayCount);
        ImmutableArray<FirmsHistoryDay> days =
        [
            .. Enumerable.Range(start: 0, RetainedDayCount)
                .Select(offset => CreateDay(retainedFromDate.AddDays(offset)))
        ];
        bool isReady = days
            .Where(day => day.Date < today)
            .All(day => day.IsComplete);
        bool isPartiallyStale = days.Any(day => day.IsPartiallyStale || !day.IsComplete);

        return new(
            now,
            CompletedDayCount,
            retainedFromDate,
            today,
            retainedFromDate,
            today,
            isReady,
            isPartiallyStale,
            days);
    }

    private FirmsHistoryDay CreateDay(DateOnly date)
    {
        ImmutableArray<SegmentStatus> statuses =
        [
            .. _orderedKeys.Select(key => _segments[new(date, key)].Status)
        ];
        ImmutableArray<Anomaly> anomalies =
        [
            .. _orderedKeys
                .SelectMany(key => _segments[new(date, key)].Anomalies)
                .DistinctBy(anomaly => anomaly.Id, StringComparer.Ordinal)
                .OrderByDescending(anomaly => anomaly.AcquiredAtUtc)
                .ThenBy(anomaly => anomaly.Id, StringComparer.Ordinal)
        ];
        ImmutableArray<NotificationClusterSummary> clusters =
        [
            .. NotificationClustering.Create(
                    anomalies,
                    _notificationOptions.ClusterRadiusKilometers,
                    _notificationOptions.ClusterTimeWindow)
                .Select(NotificationClusterSummary.FromCluster)
        ];
        bool isComplete = statuses.All(status => status.LastSuccessAtUtc is not null && !status.IsStale);
        bool isPartiallyStale = statuses.Any(status => status.LastSuccessAtUtc is not null && status.IsStale);

        return new(
            date,
            isComplete,
            isPartiallyStale,
            statuses,
            anomalies.Length,
            anomalies,
            clusters.Length,
            clusters);
    }

    private static DateOnly GetUtcDate(DateTimeOffset value) =>
        DateOnly.FromDateTime(value.UtcDateTime);

    private readonly record struct HistorySegmentKey(DateOnly Date, SegmentKey Segment);

    private sealed record HistorySegmentState(
        ImmutableArray<Anomaly> Anomalies,
        SegmentStatus Status);
}
