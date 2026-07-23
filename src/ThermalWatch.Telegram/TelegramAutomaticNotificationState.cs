using ThermalWatch.Core;

namespace ThermalWatch.Telegram;

internal sealed class TelegramAutomaticNotificationState(
    double radiusKilometers,
    TimeSpan timeWindow,
    TimeSpan retention)
{
    private const int MaximumDeliveredDetections = 100_000;
    private readonly Dictionary<string, DeliveredDetection> _delivered = new(StringComparer.Ordinal);
    private readonly List<PendingTelegramNotification> _pending = [];

    public int PendingCount => _pending.Count;

    public void Expire(DateTimeOffset now)
    {
        DateTimeOffset cutoff = now - retention;
        foreach (string id in _delivered
            .Where(pair => pair.Value.TrackedAtUtc < cutoff)
            .Select(pair => pair.Key)
            .ToArray())
        {
            _delivered.Remove(id);
        }
    }

    public TelegramCandidatePreparation PrepareCandidate(
        NotificationCluster cluster,
        DateTimeOffset now)
    {
        Expire(now);

        var clusters = new List<NotificationCluster> { cluster };
        DateTimeOffset firstSeenUtc = now;
        bool foundRelatedPending;
        do
        {
            foundRelatedPending = false;
            for (int index = _pending.Count - 1; index >= 0; index--)
            {
                PendingTelegramNotification pending = _pending[index];
                if (!clusters.Any(existing => TelegramNotificationClustering.AreRelated(
                    existing,
                    pending.Cluster,
                    radiusKilometers,
                    timeWindow)))
                {
                    continue;
                }

                clusters.Add(pending.Cluster);
                firstSeenUtc = firstSeenUtc < pending.FirstSeenUtc
                    ? firstSeenUtc
                    : pending.FirstSeenUtc;
                _pending.RemoveAt(index);
                foundRelatedPending = true;
            }
        }
        while (foundRelatedPending);

        NotificationCluster preparedCluster = TelegramNotificationClustering.MergeRelated(
            clusters,
            radiusKilometers,
            timeWindow);
        bool continuesDeliveredEpisode = TryExtendDeliveredEpisode(preparedCluster, now);

        return new(preparedCluster, firstSeenUtc, continuesDeliveredEpisode);
    }

    public void AddPending(PendingTelegramNotification pending) =>
        _pending.Add(pending);

    public PendingTelegramNotification GetPending(int index) =>
        _pending[index];

    public bool TrySuppressPending(int index, DateTimeOffset now)
    {
        PendingTelegramNotification pending = _pending[index];
        if (!TryExtendDeliveredEpisode(pending.Cluster, now))
            return false;

        _pending.RemoveAt(index);
        return true;
    }

    public void RecordDelivered(NotificationCluster cluster, DateTimeOffset now)
    {
        Expire(now);
        Track(cluster, now);
    }

    public void RemovePendingAt(int index) =>
        _pending.RemoveAt(index);

    private bool TryExtendDeliveredEpisode(NotificationCluster cluster, DateTimeOffset now)
    {
        Expire(now);
        bool isContinuation = cluster.Members.Any(candidate =>
            _delivered.Values.Any(delivered =>
                (candidate.AcquiredAtUtc - delivered.AcquiredAtUtc).Duration() <= timeWindow
                && Geography.HaversineKilometers(
                    candidate.Latitude,
                    candidate.Longitude,
                    delivered.Latitude,
                    delivered.Longitude) <= radiusKilometers));
        if (!isContinuation)
            return false;

        Track(cluster, now);
        return true;
    }

    private void Track(NotificationCluster cluster, DateTimeOffset now)
    {
        foreach (Anomaly detection in cluster.Members)
        {
            _delivered[detection.Id] = new(
                detection.Latitude,
                detection.Longitude,
                detection.AcquiredAtUtc,
                now);
        }

        int excess = _delivered.Count - MaximumDeliveredDetections;
        if (excess <= 0)
            return;

        foreach (string id in _delivered
            .OrderBy(pair => pair.Value.TrackedAtUtc)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(excess)
            .Select(pair => pair.Key)
            .ToArray())
        {
            _delivered.Remove(id);
        }
    }

    private sealed record DeliveredDetection(
        double Latitude,
        double Longitude,
        DateTimeOffset AcquiredAtUtc,
        DateTimeOffset TrackedAtUtc);
}
