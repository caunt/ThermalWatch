using System.Collections.Immutable;
using System.Threading.Channels;

namespace ThermalWatch.Core;

public sealed class AnomalySnapshotStore
{
    private readonly TimeProvider _timeProvider;
    private readonly FirmsOptions _options;
    private readonly SegmentKey[] _orderedKeys;
    private readonly Dictionary<SegmentKey, SegmentState> _segments;
    private readonly Channel<AnomalySnapshot> _updates;
    private readonly Lock _sync = new();
    private AnomalySnapshot _current;

    public AnomalySnapshotStore(FirmsOptions options, TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
        _orderedKeys =
        [
            .. options.CountryCodes
                .SelectMany(countryCode => FirmsSources.All.Select(source => new SegmentKey(countryCode, source)))
        ];
        _segments = _orderedKeys.ToDictionary(
            key => key,
            key => new SegmentState(
                [],
                new(key.CountryCode, key.Source, null, null, true, null, IngestionModes.None)));
        _updates = Channel.CreateBounded<AnomalySnapshot>(new BoundedChannelOptions(capacity: 1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
        _current = CreateSnapshot(_timeProvider.GetUtcNow());
    }

    public AnomalySnapshot Current => Volatile.Read(ref _current);

    public IAsyncEnumerable<AnomalySnapshot> ReadUpdatesAsync(CancellationToken cancellationToken) =>
        _updates.Reader.ReadAllAsync(cancellationToken);

    public AnomalySnapshot Publish(IReadOnlyCollection<SegmentRefreshResult> results)
    {
        lock (_sync)
        {
            foreach (SegmentRefreshResult result in results)
            {
                if (!_segments.TryGetValue(result.Key, out SegmentState? existing))
                    continue;

                _segments[result.Key] = result.Succeeded
                    ? new(
                        result.Anomalies,
                        new(
                            result.Key.CountryCode,
                            result.Key.Source,
                            result.AttemptedAtUtc,
                            result.CompletedAtUtc,
                            IsStale: false,
                            Error: null,
                            result.IngestionMode))
                    : existing with
                    {
                        Status = existing.Status with
                        {
                            LastAttemptAtUtc = result.AttemptedAtUtc,
                            IsStale = true,
                            Error = result.Error
                        }
                    };
            }

            AnomalySnapshot snapshot = CreateSnapshot(_timeProvider.GetUtcNow());
            Volatile.Write(ref _current, snapshot);
            _updates.Writer.TryWrite(snapshot);
            return snapshot;
        }
    }

    private AnomalySnapshot CreateSnapshot(DateTimeOffset now)
    {
        DateTimeOffset cutoff = now - _options.ActiveWindow;
        var segmentStatuses = _orderedKeys
            .Select(key => _segments[key].Status)
            .ToImmutableArray();
        var anomalies = _orderedKeys
            .SelectMany(key => _segments[key].Anomalies)
            .Where(anomaly => anomaly.AcquiredAtUtc >= cutoff && anomaly.AcquiredAtUtc <= now)
            .DistinctBy(anomaly => anomaly.Id)
            .OrderByDescending(anomaly => anomaly.AcquiredAtUtc)
            .ThenBy(anomaly => anomaly.Id, StringComparer.Ordinal)
            .ToImmutableArray();
        bool isReady = segmentStatuses.Any(status => status.LastSuccessAtUtc is not null);

        return new(
            now,
            _options.ActiveWindow.TotalHours,
            isReady,
            isReady && segmentStatuses.Any(status => status.IsStale),
            _options.CountryCodes,
            segmentStatuses,
            anomalies.Length,
            anomalies);
    }

    private sealed record SegmentState(
        ImmutableArray<Anomaly> Anomalies,
        SegmentStatus Status);
}
