using System.Collections.Immutable;
using System.Threading.Channels;

using Nito.AsyncEx;

namespace ThermalWatch.Core;

/// <summary>Retains complete ingestion segments and atomically publishes immutable active snapshots.</summary>
public sealed class AnomalySnapshotStore
{
    private readonly FirmsOptions _options;
    private readonly SegmentKey[] _orderedKeys;
    private readonly AsyncLock _publicationGate = new();
    private readonly Dictionary<SegmentKey, SegmentState> _segments;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<AnomalySnapshot> _updates;
    private AnomalySnapshot _current;

    /// <summary>Initializes an unready snapshot for the configured countries and sources.</summary>
    /// <param name="options">The ingestion and active-window configuration.</param>
    /// <param name="timeProvider">The UTC clock used for publication and retention.</param>
    public AnomalySnapshotStore(FirmsOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _options = options;
        _timeProvider = timeProvider;
        var orderedKeys = new List<SegmentKey>();

        foreach (string countryCode in options.CountryCodes)
        {
            foreach (string source in FirmsSources.All)
                orderedKeys.Add(new(countryCode, source));
        }

        _orderedKeys = [.. orderedKeys];
        _segments = _orderedKeys.ToDictionary(
            static key => key,
            static key => new SegmentState(
                [],
                new(key.CountryCode, key.Source, LastAttemptAtUtc: null, LastSuccessAtUtc: null, IsStale: true, Error: null, IngestionModes.None)
            )
        );
        _updates = Channel.CreateBounded<AnomalySnapshot>(
            new BoundedChannelOptions(capacity: 1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            }
        );
        _current = CreateSnapshot(_timeProvider.GetUtcNow());
    }

    /// <summary>Gets the latest immutable publication without consuming notifications.</summary>
    public AnomalySnapshot Current => Volatile.Read(ref _current);

    /// <summary>Applies successful segments, retains failed segments as stale, and notifies the snapshot consumer.</summary>
    /// <param name="results">The complete results of the polling cycle.</param>
    public AnomalySnapshot Publish(IReadOnlyCollection<SegmentRefreshResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        using (_publicationGate.Lock())
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
                            result.IngestionMode
                        )
                    )
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

            return !_updates.Writer.TryWrite(snapshot)
                ? throw new InvalidOperationException(message: "The snapshot update channel unexpectedly closed.")
                : snapshot;
        }
    }

    /// <summary>Reads publications through the bounded, single-consumer, drop-oldest notification stream.</summary>
    /// <param name="cancellationToken">Cancels the consumer's wait.</param>
    public IAsyncEnumerable<AnomalySnapshot> ReadUpdatesAsync(CancellationToken cancellationToken)
    {
        return _updates.Reader.ReadAllAsync(cancellationToken);
    }

    private AnomalySnapshot CreateSnapshot(DateTimeOffset now)
    {
        DateTimeOffset cutoff = now - _options.ActiveWindow;

        var segmentStatuses = _orderedKeys
            .Select(key => _segments[key].Status)
            .ToImmutableArray();

        var retainedAnomalies = new List<Anomaly>();

        foreach (SegmentKey key in _orderedKeys)
        {
            foreach (Anomaly anomaly in _segments[key].Anomalies)
                retainedAnomalies.Add(anomaly);
        }

        var anomalies = retainedAnomalies
            .Where(anomaly => anomaly.AcquiredAtUtc >= cutoff && anomaly.AcquiredAtUtc <= now)
            .DistinctBy(anomaly => anomaly.Id, StringComparer.Ordinal)
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
            anomalies
        );
    }

    private sealed record SegmentState(ImmutableArray<Anomaly> Anomalies, SegmentStatus Status);
}
