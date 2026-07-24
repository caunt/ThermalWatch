namespace ThermalWatch.Core;

internal sealed class NotificationEpisodeHistory(
    double radiusKilometers,
    TimeSpan timeWindow,
    TimeSpan retention)
{
    private const int MaximumTrackedAnomalies = 100_000;
    private readonly Dictionary<string, TrackedAnomaly> _tracked = new(StringComparer.Ordinal);

    public void Expire(DateTimeOffset now)
    {
        DateTimeOffset cutoff = now - retention;
        foreach (string id in _tracked
            .Where(pair => pair.Value.TrackedAtUtc < cutoff)
            .Select(pair => pair.Key)
            .ToArray())
        {
            _tracked.Remove(id);
        }
    }

    public bool TrySuppressAndExtend(NotificationCluster cluster, DateTimeOffset now)
    {
        Expire(now);
        bool isContinuation = cluster.Members.Any(candidate =>
            _tracked.Values.Any(tracked =>
                (candidate.AcquiredAtUtc - tracked.AcquiredAtUtc).Duration() <= timeWindow
                && Geography.HaversineKilometers(
                    candidate.Latitude,
                    candidate.Longitude,
                    tracked.Latitude,
                    tracked.Longitude) <= radiusKilometers));
        if (!isContinuation)
            return false;

        Track(cluster, now);
        return true;
    }

    public void RecordIncident(NotificationCluster cluster, DateTimeOffset now)
    {
        Expire(now);
        Track(cluster, now);
    }

    private void Track(NotificationCluster cluster, DateTimeOffset now)
    {
        foreach (Anomaly anomaly in cluster.Members)
        {
            _tracked[anomaly.Id] = new(
                anomaly.Latitude,
                anomaly.Longitude,
                anomaly.AcquiredAtUtc,
                now);
        }

        int excess = _tracked.Count - MaximumTrackedAnomalies;
        if (excess <= 0)
            return;

        foreach (string id in _tracked
            .OrderBy(pair => pair.Value.TrackedAtUtc)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(excess)
            .Select(pair => pair.Key)
            .ToArray())
        {
            _tracked.Remove(id);
        }
    }

    private sealed record TrackedAnomaly(
        double Latitude,
        double Longitude,
        DateTimeOffset AcquiredAtUtc,
        DateTimeOffset TrackedAtUtc);
}
