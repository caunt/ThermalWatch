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

        var parents = Enumerable.Range(0, detections.Count).ToArray();

        for (var first = 0; first < detections.Count; first++)
        {
            for (var second = first + 1; second < detections.Count; second++)
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

    private static NotificationCluster BuildCluster(IEnumerable<Anomaly> detections)
    {
        var members = detections
            .OrderByDescending(detection => detection.AcquiredAtUtc)
            .ThenBy(detection => detection.Id, StringComparer.Ordinal)
            .ToImmutableArray();

        var representative = members
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
        var firstRoot = Find(parents, first);
        var secondRoot = Find(parents, second);

        if (firstRoot != secondRoot)
            parents[secondRoot] = firstRoot;
    }
}
