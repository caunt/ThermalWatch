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

        Anomaly[] orderedAnomalies =
        [
            .. anomalies
                .OrderBy(anomaly => anomaly.AcquiredAtUtc)
                .ThenBy(anomaly => anomaly.Id, StringComparer.Ordinal)
        ];
        int[] parents = [.. Enumerable.Range(start: 0, orderedAnomalies.Length)];
        double cellSize = Geography.ChordLength(radiusKilometers);
        if (!(cellSize > 0) || !double.IsFinite(cellSize))
        {
            JoinTimeBoundedPairs(
                orderedAnomalies,
                parents,
                radiusKilometers,
                timeWindow);
        }
        else
        {
            JoinSpatiotemporallyBoundedPairs(
                orderedAnomalies,
                parents,
                radiusKilometers,
                timeWindow,
                cellSize);
        }

        return
        [
            .. orderedAnomalies
                .Select((anomaly, index) => (Anomaly: anomaly, Root: Find(parents, index)))
                .GroupBy(item => item.Root)
                .Select(group => BuildCluster(group.Select(item => item.Anomaly)))
                .OrderByDescending(cluster => cluster.Representative.AcquiredAtUtc)
                .ThenBy(cluster => cluster.Id, StringComparer.Ordinal)
        ];
    }

    private static void JoinTimeBoundedPairs(
        Anomaly[] orderedAnomalies,
        int[] parents,
        double radiusKilometers,
        TimeSpan timeWindow)
    {
        for (int first = 0; first < orderedAnomalies.Length; first++)
        {
            for (int second = first + 1; second < orderedAnomalies.Length; second++)
            {
                if (orderedAnomalies[second].AcquiredAtUtc - orderedAnomalies[first].AcquiredAtUtc > timeWindow)
                    break;

                if (Geography.HaversineKilometers(orderedAnomalies[first], orderedAnomalies[second]) <= radiusKilometers)
                    Union(parents, first, second);
            }
        }
    }

    private static void JoinSpatiotemporallyBoundedPairs(
        Anomaly[] orderedAnomalies,
        int[] parents,
        double radiusKilometers,
        TimeSpan timeWindow,
        double cellSize)
    {
        var activeByCell = new Dictionary<GeographicCell, Queue<int>>();
        var activeInTimeOrder = new Queue<(int Index, GeographicCell Cell)>();
        for (int current = 0; current < orderedAnomalies.Length; current++)
        {
            Anomaly currentAnomaly = orderedAnomalies[current];
            while (activeInTimeOrder.TryPeek(out (int Index, GeographicCell Cell) oldest)
                && currentAnomaly.AcquiredAtUtc - orderedAnomalies[oldest.Index].AcquiredAtUtc > timeWindow)
            {
                activeInTimeOrder.Dequeue();
                Queue<int> occupants = activeByCell[oldest.Cell];
                occupants.Dequeue();
                if (occupants.Count == 0)
                    activeByCell.Remove(oldest.Cell);
            }

            GeographicCell cell = Geography.GetCell(
                currentAnomaly.Latitude,
                currentAnomaly.Longitude,
                cellSize);
            for (long x = cell.X - 1; x <= cell.X + 1; x++)
            {
                for (long y = cell.Y - 1; y <= cell.Y + 1; y++)
                {
                    for (long z = cell.Z - 1; z <= cell.Z + 1; z++)
                    {
                        if (!activeByCell.TryGetValue(new(x, y, z), out Queue<int>? candidates))
                            continue;

                        foreach (int candidate in candidates)
                        {
                            if (Geography.HaversineKilometers(orderedAnomalies[candidate], currentAnomaly) <= radiusKilometers)
                                Union(parents, candidate, current);
                        }
                    }
                }
            }

            if (!activeByCell.TryGetValue(cell, out Queue<int>? currentCell))
            {
                currentCell = new();
                activeByCell.Add(cell, currentCell);
            }

            currentCell.Enqueue(current);
            activeInTimeOrder.Enqueue((current, cell));
        }
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
