using System.Collections.Immutable;

namespace ThermalWatch.Core;

public static class NotificationClustering
{
    public static ImmutableArray<NotificationCluster> Create(
        IReadOnlyList<Anomaly> detections,
        double radiusKilometers,
        TimeSpan timeWindow)
    {
        if (detections.Count == 0)
            return [];

        int[] parents = [.. Enumerable.Range(start: 0, detections.Count)];

        for (int first = 0; first < detections.Count; first++)
        {
            for (int second = first + 1; second < detections.Count; second++)
            {
                if ((detections[first].AcquiredAtUtc - detections[second].AcquiredAtUtc).Duration() > timeWindow)
                    continue;

                if (Geography.HaversineKilometers(detections[first], detections[second]) <= radiusKilometers)
                    Union(parents, first, second);
            }
        }

        return
        [
            .. detections
                .Select((detection, index) => (Detection: detection, Root: Find(parents, index)))
                .GroupBy(item => item.Root)
                .Select(group => BuildCluster(group.Select(item => item.Detection)))
                .OrderByDescending(cluster => cluster.Representative.AcquiredAtUtc)
                .ThenBy(cluster => cluster.Id, StringComparer.Ordinal)
        ];
    }

    public static ImmutableArray<NotificationCluster> CreateCandidates(
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
        Anomaly[] clusteringDetections = includeActiveContext
            ? [.. activeDetections
                .Where(detection => newIds.Contains(detection.Id)
                    || newDetections.Any(newDetection => AreRelated(
                        detection,
                        newDetection,
                        radiusKilometers,
                        timeWindow)))
                .DistinctBy(detection => detection.Id)]
            : [.. newDetections.DistinctBy(detection => detection.Id)];

        return
        [
            .. Create(clusteringDetections, radiusKilometers, timeWindow)
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
            throw new ArgumentException(message: "At least one cluster is required.", nameof(clusters));

        if (clusters.Count == 1)
            return clusters[0];

        ImmutableArray<NotificationCluster> components = Create(
            clusters
                .SelectMany(cluster => cluster.Members)
                .DistinctBy(detection => detection.Id)
                .ToArray(),
            radiusKilometers,
            timeWindow);

        return components.Length == 1
            ? components[0]
            : throw new InvalidOperationException(message: "Only connected notification clusters can be merged.");
    }

    private static NotificationCluster BuildCluster(IEnumerable<Anomaly> detections)
    {
        var members = detections
            .OrderByDescending(detection => detection.AcquiredAtUtc)
            .ThenBy(detection => detection.Id, StringComparer.Ordinal)
            .ToImmutableArray();

        Anomaly representative = members
            .OrderByDescending(detection => detection.FrpMegawatts ?? double.NegativeInfinity)
            .ThenByDescending(detection => detection.AcquiredAtUtc)
            .ThenBy(detection => detection.Id, StringComparer.Ordinal)
            .First();

        return new(AnomalyId.CreateClusterId(members.Select(member => member.Id)), representative, members);
    }

    private static int Find(int[] parents, int index)
    {
        while (parents[index] != index)
        {
            parents[index] = parents[parents[index]];
            index = parents[index];
        }

        return index;
    }

    private static void Union(int[] parents, int first, int second)
    {
        int firstRoot = Find(parents, first);
        int secondRoot = Find(parents, second);

        if (firstRoot != secondRoot)
            parents[secondRoot] = firstRoot;
    }
}
