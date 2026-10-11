using System.Collections.Immutable;

namespace ThermalWatch.Core;

internal sealed class NotificationEpisodeHistory(double radiusKilometers, TimeSpan timeWindow, TimeSpan retention)
{
    private const int MaximumTrackedAnomalies = 100_000;
    private ImmutableDictionary<string, TrackedAnomaly> _tracked =
        ImmutableDictionary.Create<string, TrackedAnomaly>(StringComparer.Ordinal);

    public void Expire(DateTimeOffset now)
    {
        DateTimeOffset cutoff = now - retention;

        _tracked = _tracked.RemoveRange(_tracked.Where(pair => pair.Value.TrackedAtUtc < cutoff).Select(pair => pair.Key));
    }

    public void RecordIncident(NotificationCluster cluster, DateTimeOffset now)
    {
        Expire(now);
        Track(cluster, now);
    }

    public bool TrySuppressAndExtend(NotificationCluster cluster, DateTimeOffset now)
    {
        Expire(now);

        bool isContinuation = cluster.Members.Any(
            candidate =>
            _tracked.Values.Any(
                tracked =>
                (candidate.AcquiredAtUtc - tracked.AcquiredAtUtc).Duration() <= timeWindow
                && Geography.HaversineKilometers(candidate.Latitude, candidate.Longitude, tracked.Latitude, tracked.Longitude) <= radiusKilometers
            )
        );

        if (!isContinuation)
            return false;

        Track(cluster, now);

        return true;
    }

    private void Track(NotificationCluster cluster, DateTimeOffset now)
    {
        foreach (Anomaly anomaly in cluster.Members)
            _tracked = _tracked.SetItem(anomaly.Id, new(anomaly.Latitude, anomaly.Longitude, anomaly.AcquiredAtUtc, now));

        int excess = _tracked.Count - MaximumTrackedAnomalies;

        if (excess <= 0)
            return;

        _tracked = _tracked.RemoveRange(
            _tracked
            .OrderBy(static pair => pair.Value.TrackedAtUtc)
            .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
            .Take(excess)
            .Select(static pair => pair.Key)
        );
    }

    private sealed record TrackedAnomaly(double Latitude, double Longitude, DateTimeOffset AcquiredAtUtc, DateTimeOffset TrackedAtUtc);
}
