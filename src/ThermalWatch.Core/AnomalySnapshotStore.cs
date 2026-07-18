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
            .. options.Countries
                .SelectMany(country => FirmsSources.All.Select(source => new SegmentKey(country, source)))
        ];
        _segments = _orderedKeys.ToDictionary(
            key => key,
            key => new SegmentState(
                [],
                new(key.CountryCode, key.Source, null, null, true, null, IngestionModes.None)));
        _updates = Channel.CreateBounded<AnomalySnapshot>(new BoundedChannelOptions(1)
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
            foreach (var result in results)
            {
                if (!_segments.TryGetValue(result.Key, out var existing))
                    continue;

                _segments[result.Key] = result.Succeeded
                    ? new(
                        result.Detections,
                        new(
                            result.Key.CountryCode,
                            result.Key.Source,
                            result.AttemptedAtUtc,
                            result.CompletedAtUtc,
                            false,
                            null,
                            result.IngestionMode))
                    : existing with
                    {
                        Status = existing.Status with
                        {
                            LastAttemptUtc = result.AttemptedAtUtc,
                            Stale = true,
                            Error = result.Error
                        }
                    };
            }

            var snapshot = CreateSnapshot(_timeProvider.GetUtcNow());
            Volatile.Write(ref _current, snapshot);
            _updates.Writer.TryWrite(snapshot);
            return snapshot;
        }
    }

    private AnomalySnapshot CreateSnapshot(DateTimeOffset now)
    {
        var cutoff = now - _options.ActiveWindow;
        var statuses = _orderedKeys
            .Select(key => _segments[key].Status)
            .ToImmutableArray();
        var items = _orderedKeys
            .SelectMany(key => _segments[key].Detections)
            .Where(detection => detection.AcquiredAtUtc >= cutoff && detection.AcquiredAtUtc <= now)
            .DistinctBy(detection => detection.Id)
            .OrderByDescending(detection => detection.AcquiredAtUtc)
            .ThenBy(detection => detection.Id, StringComparer.Ordinal)
            .ToImmutableArray();
        var isReady = statuses.Any(status => status.LastSuccessUtc is not null);

        return new(
            now,
            _options.ActiveWindow.TotalHours,
            isReady,
            isReady && statuses.Any(status => status.Stale),
            _options.Countries,
            statuses,
            items.Length,
            items);
    }

    private sealed record SegmentState(
        ImmutableArray<Anomaly> Detections,
        SourceStatus Status);
}
