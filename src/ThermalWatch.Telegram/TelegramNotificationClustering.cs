using System.Collections.Immutable;
using ThermalWatch.Core;

namespace ThermalWatch.Telegram;

internal static class TelegramNotificationClustering
{
    public static ImmutableArray<NotificationCluster> Create(
        IReadOnlyList<Anomaly> activeDetections,
        IReadOnlyList<Anomaly> newDetections,
        double radiusKilometers,
        TimeSpan timeWindow,
        bool includeActiveContext)
    {
        if (newDetections.Count == 0)
            return [];

        var newIds = newDetections
            .Select(detection => detection.Id)
            .ToHashSet(StringComparer.Ordinal);
        var clusteringDetections = includeActiveContext
            ? activeDetections
                .Where(detection => newIds.Contains(detection.Id)
                    || newDetections.Any(newDetection => AreRelated(
                        detection,
                        newDetection,
                        radiusKilometers,
                        timeWindow)))
                .DistinctBy(detection => detection.Id)
                .ToArray()
            : newDetections
                .DistinctBy(detection => detection.Id)
                .ToArray();

        return
        [
            .. NotificationClustering.Create(
                    clusteringDetections,
                    radiusKilometers,
                    timeWindow)
                .Where(cluster => cluster.Members.Any(member => newIds.Contains(member.Id)))
        ];
    }

    public static bool AreRelated(
        NotificationCluster first,
        NotificationCluster second,
        double radiusKilometers,
        TimeSpan timeWindow) =>
        first.Members.Any(firstMember =>
            second.Members.Any(secondMember => AreRelated(
                firstMember,
                secondMember,
                radiusKilometers,
                timeWindow)));

    public static bool AreRelated(
        Anomaly first,
        Anomaly second,
        double radiusKilometers,
        TimeSpan timeWindow) =>
        (first.AcquiredAtUtc - second.AcquiredAtUtc).Duration() <= timeWindow
        && Geography.HaversineKilometers(first, second) <= radiusKilometers;

    public static NotificationCluster MergeRelated(
        IReadOnlyList<NotificationCluster> clusters,
        double radiusKilometers,
        TimeSpan timeWindow)
    {
        if (clusters.Count == 0)
            throw new ArgumentException("At least one cluster is required.", nameof(clusters));

        if (clusters.Count == 1)
            return clusters[0];

        var components = NotificationClustering.Create(
            clusters
                .SelectMany(cluster => cluster.Members)
                .DistinctBy(detection => detection.Id)
                .ToArray(),
            radiusKilometers,
            timeWindow);

        return components.Length == 1
            ? components[0]
            : throw new InvalidOperationException("Only connected Telegram clusters can be merged.");
    }
}
