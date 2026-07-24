using System.Collections.Immutable;

namespace ThermalWatch.Core;

public static class NotificationClustering
{
    public static ImmutableArray<NotificationCluster> Create(
        IReadOnlyList<Anomaly> anomalies,
        double radiusKilometers,
        TimeSpan timeWindow)
    {
        if (anomalies.Count == 0)
            return [];

        int[] parents = [.. Enumerable.Range(start: 0, anomalies.Count)];

        for (int first = 0; first < anomalies.Count; first++)
        {
            for (int second = first + 1; second < anomalies.Count; second++)
            {
                if ((anomalies[first].AcquiredAtUtc - anomalies[second].AcquiredAtUtc).Duration() > timeWindow)
                    continue;

                if (Geography.HaversineKilometers(anomalies[first], anomalies[second]) <= radiusKilometers)
                    Union(parents, first, second);
            }
        }

        return
        [
            .. anomalies
                .Select((anomaly, index) => (Anomaly: anomaly, Root: Find(parents, index)))
                .GroupBy(item => item.Root)
                .Select(group => BuildCluster(group.Select(item => item.Anomaly)))
                .OrderByDescending(cluster => cluster.Representative.AcquiredAtUtc)
                .ThenBy(cluster => cluster.Id, StringComparer.Ordinal)
        ];
    }

    public static bool AreRelated(
        Anomaly first,
        Anomaly second,
        double radiusKilometers,
        TimeSpan timeWindow) =>
        (first.AcquiredAtUtc - second.AcquiredAtUtc).Duration() <= timeWindow
        && Geography.HaversineKilometers(first, second) <= radiusKilometers;

    private static NotificationCluster BuildCluster(IEnumerable<Anomaly> anomalies)
    {
        var members = anomalies
            .OrderByDescending(anomaly => anomaly.AcquiredAtUtc)
            .ThenBy(anomaly => anomaly.Id, StringComparer.Ordinal)
            .ToImmutableArray();

        Anomaly representative = members
            .OrderByDescending(anomaly => anomaly.FrpMegawatts ?? double.NegativeInfinity)
            .ThenByDescending(anomaly => anomaly.AcquiredAtUtc)
            .ThenBy(anomaly => anomaly.Id, StringComparer.Ordinal)
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
