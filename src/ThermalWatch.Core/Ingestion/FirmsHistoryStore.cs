using System.Collections.Immutable;

using Nito.AsyncEx;

namespace ThermalWatch.Core;

/// <summary>Retains immutable daily observations and completeness state across a rolling UTC history window.</summary>
public sealed class FirmsHistoryStore
{
    /// <summary>The number of completed dates required by the historical baseline.</summary>
    public const int CompletedDayCount = 30;
    /// <summary>The completed baseline plus today's live date.</summary>
    public const int RetainedDayCount = CompletedDayCount + 1;

    private Dictionary<HistorySegmentKey, HistorySegmentState> _segments = [];
    private readonly NotificationOptions _notificationOptions;
    private readonly SegmentKey[] _orderedKeys;
    private readonly TimeProvider _timeProvider;
    private FirmsHistory _current;
    private DateOnly? _retainedThroughDate;
    private readonly AsyncLock _historyGate = new();

    /// <summary>Initializes missing history segments for the configured countries and sources.</summary>
    /// <param name="firmsOptions">The configured ingestion countries.</param>
    /// <param name="notificationOptions">The raw-cluster radius and time window.</param>
    /// <param name="timeProvider">The UTC retention and publication clock.</param>
    public FirmsHistoryStore(FirmsOptions firmsOptions, NotificationOptions notificationOptions, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(firmsOptions);
        ArgumentNullException.ThrowIfNull(notificationOptions);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _notificationOptions = notificationOptions;
        _timeProvider = timeProvider;
        var orderedKeys = new List<SegmentKey>();

        foreach (string countryCode in firmsOptions.CountryCodes)
        {
            foreach (string source in FirmsSources.All)
                orderedKeys.Add(new(countryCode, source));
        }

        _orderedKeys = [.. orderedKeys];

        DateTimeOffset now = _timeProvider.GetUtcNow();
        EnsureRetainedDates(GetUtcDate(now));
        _current = CreateHistory(now);
    }

    /// <summary>Gets the current immutable history, advancing retention at UTC rollover.</summary>
    public FirmsHistory Current
    {
        get
        {
            using (_historyGate.Lock())
            {
                DateTimeOffset now = _timeProvider.GetUtcNow();

                if (_retainedThroughDate != GetUtcDate(now))
                {
                    EnsureRetainedDates(GetUtcDate(now));
                    _current = CreateHistory(now);
                }

                return _current;
            }
        }
    }

    /// <summary>Checks whether any requested date/source/country slice is missing or stale.</summary>
    /// <param name="key">The ingestion segment.</param>
    /// <param name="startDate">The first UTC date.</param>
    /// <param name="dayCount">The number of consecutive dates.</param>
    public bool NeedsRefresh(SegmentKey key, DateOnly startDate, int dayCount)
    {
        using (_historyGate.Lock())
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();

            if (_retainedThroughDate != GetUtcDate(now))
            {
                EnsureRetainedDates(GetUtcDate(now));
                _current = CreateHistory(now);
            }

            for (int offset = 0; offset < dayCount; offset++)
            {
                DateOnly date = startDate.AddDays(offset);

                if (!_segments.TryGetValue(new(date, key), out HistorySegmentState? state))
                    return true;

                if (state.Status.LastSuccessAtUtc is null || state.Status.IsStale)
                    return true;
            }

            return false;
        }
    }

    /// <summary>Publishes a set of segment results covering one inclusive UTC-date range.</summary>
    /// <param name="startDate">The first covered date.</param>
    /// <param name="dayCount">The number of covered dates.</param>
    /// <param name="results">The complete segment refresh results.</param>
    public FirmsHistory Publish(DateOnly startDate, int dayCount, IReadOnlyCollection<SegmentRefreshResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentOutOfRangeException.ThrowIfLessThan(dayCount, other: 1);

        FirmsHistoryRangeRefreshResult[] ranges =
        [
            .. results.Select(result => new FirmsHistoryRangeRefreshResult(startDate, dayCount, result))
        ];

        return Publish(ranges);
    }

    /// <summary>Atomically publishes a batch of independently completed history ranges.</summary>
    /// <param name="ranges">The covered ranges and their segment results.</param>
    public FirmsHistory Publish(IReadOnlyCollection<FirmsHistoryRangeRefreshResult> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);

        using (_historyGate.Lock())
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            EnsureRetainedDates(GetUtcDate(now));

            foreach (FirmsHistoryRangeRefreshResult range in ranges)
                ApplyRange(range);

            _current = CreateHistory(now);

            return _current;
        }
    }

    private static DateOnly GetUtcDate(DateTimeOffset value)
    {
        return DateOnly.FromDateTime(value.UtcDateTime);
    }

    private void ApplyRange(FirmsHistoryRangeRefreshResult range)
    {
        int dayCount = range.DayCount;
        DateOnly endDate = range.StartDate.AddDays(dayCount - 1);
        SegmentRefreshResult result = range.Segment;

        for (DateOnly date = range.StartDate; date <= endDate; date = date.AddDays(value: 1))
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
                        result.IngestionMode
                    )
                );

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

    private FirmsHistoryDay CreateDay(DateOnly date)
    {
        ImmutableArray<SegmentStatus> statuses =
        [
            .. _orderedKeys.Select(key => _segments[new(date, key)].Status)
        ];

        var retainedAnomalies = new List<Anomaly>();

        foreach (SegmentKey key in _orderedKeys)
        {
            foreach (Anomaly anomaly in _segments[new(date, key)].Anomalies)
                retainedAnomalies.Add(anomaly);
        }

        ImmutableArray<Anomaly> anomalies =
        [
            .. retainedAnomalies
                .DistinctBy(anomaly => anomaly.Id, StringComparer.Ordinal)
                .OrderByDescending(anomaly => anomaly.AcquiredAtUtc)
                .ThenBy(anomaly => anomaly.Id, StringComparer.Ordinal)
        ];

        ImmutableArray<NotificationClusterSummary> clusters =
        [
            .. NotificationClustering.Create(anomalies, _notificationOptions.ClusterRadiusKilometers, _notificationOptions.ClusterTimeWindow)
                .Select(NotificationClusterSummary.FromCluster)
        ];

        bool isComplete = statuses.All(status => status.LastSuccessAtUtc is not null && !status.IsStale);
        bool isPartiallyStale = statuses.Any(status => status.LastSuccessAtUtc is not null && status.IsStale);

        return new(date, isComplete, isPartiallyStale, statuses, anomalies.Length, anomalies, clusters.Length, clusters);
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

        return new(now, CompletedDayCount, retainedFromDate, today, retainedFromDate, today, isReady, isPartiallyStale, days);
    }

    private void EnsureRetainedDates(DateOnly today)
    {
        if (_retainedThroughDate == today)
            return;

        DateOnly retainedFromDate = today.AddDays(-CompletedDayCount);
        _segments = _segments
            .Where(segment => segment.Key.Date >= retainedFromDate && segment.Key.Date <= today)
            .ToDictionary();

        for (DateOnly date = retainedFromDate; date <= today; date = date.AddDays(value: 1))
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
                        new(key.CountryCode, key.Source, LastAttemptAtUtc: null, LastSuccessAtUtc: null, IsStale: true, Error: null, IngestionModes.None)
                    )
                );
            }
        }

        _retainedThroughDate = today;
    }

    private readonly record struct HistorySegmentKey(DateOnly Date, SegmentKey Segment);

    private sealed record HistorySegmentState(ImmutableArray<Anomaly> Anomalies, SegmentStatus Status);
}
