namespace ThermalWatch.Core;

internal sealed class NotificationDeliveryHistory(
    double radiusKilometers,
    TimeSpan timeWindow,
    TimeSpan retention)
{
    private const int MaximumDeliveredDetections = 100_000;
    private readonly Dictionary<string, DeliveredDetection> _delivered = new(StringComparer.Ordinal);

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

    public bool TrySuppressAndExtend(NotificationCluster cluster, DateTimeOffset now)
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

    public void RecordDelivered(NotificationCluster cluster, DateTimeOffset now)
    {
        Expire(now);
        Track(cluster, now);
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
