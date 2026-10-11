using System.Collections.Immutable;

namespace ThermalWatch.Core;

/// <summary>Builds connected observation components within the configured space and time windows.</summary>
public static class NotificationClustering
{
    /// <summary>Clusters observations and selects each component's highest-FRP representative.</summary>
    /// <typeparam name="TAnomalies">The indexed observation collection, including immutable value-type collections.</typeparam>
    /// <param name="anomalies">The observations to cluster without applying notification filters.</param>
    /// <param name="radiusKilometers">The maximum distance for a connecting pair.</param>
    /// <param name="timeWindow">The maximum acquisition-time separation for a connecting pair.</param>
    public static ImmutableArray<NotificationCluster> Create<TAnomalies>(TAnomalies anomalies, double radiusKilometers, TimeSpan timeWindow)
        where TAnomalies : IReadOnlyList<Anomaly>
    {
        if (anomalies is null)
            throw new ArgumentNullException(nameof(anomalies));

        if (anomalies.Count == 0)
            return [];

        Anomaly[] orderedAnomalies =
        [
            .. Enumerable.Range(start: 0, anomalies.Count)
                .Select(index => anomalies[index])
                .OrderBy(anomaly => anomaly.AcquiredAtUtc)
                .ThenBy(anomaly => anomaly.Id, StringComparer.Ordinal)
        ];

        int[] parents = [.. Enumerable.Range(start: 0, orderedAnomalies.Length)];
        double cellSize = Geography.ChordLength(radiusKilometers);

        if (!(cellSize > 0) || !double.IsFinite(cellSize))
            JoinTimeBoundedPairs(orderedAnomalies, parents, radiusKilometers, timeWindow);
        else
            JoinSpatiotemporallyBoundedPairs(orderedAnomalies, parents, radiusKilometers, timeWindow, cellSize);

        return
        [
            .. Enumerable.Range(start: 0, orderedAnomalies.Length)
                .GroupBy(index => Find(parents, index))
                .Select(group => BuildCluster(group.Select(index => orderedAnomalies[index])))
                .OrderByDescending(cluster => cluster.Representative.AcquiredAtUtc)
                .ThenBy(cluster => cluster.Id, StringComparer.Ordinal)
        ];
    }

    private static NotificationCluster BuildCluster(IEnumerable<Anomaly> anomalies)
    {
        var members = anomalies
            .OrderByDescending(static anomaly => anomaly.AcquiredAtUtc)
            .ThenBy(static anomaly => anomaly.Id, StringComparer.Ordinal)
            .ToImmutableArray();

        Anomaly representative = members[index: 0];

        // Members already have the acquisition-time and ID tie-break order.
        foreach (Anomaly anomaly in members)
        {
            double candidateFrp = anomaly.FrpMegawatts ?? double.NegativeInfinity;
            double representativeFrp = representative.FrpMegawatts ?? double.NegativeInfinity;

            if (candidateFrp.CompareTo(representativeFrp) > 0)
                representative = anomaly;
        }

        return new(AnomalyId.CreateClusterId(members.Select(static member => member.Id)), representative, members);
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

    private static void JoinSpatiotemporallyBoundedPairs(Anomaly[] orderedAnomalies, int[] parents, double radiusKilometers, TimeSpan timeWindow, double cellSize)
    {
        var activeByCell = new Dictionary<GeographicCell, LinkedList<int>>();
        var activeInTimeOrder = new Queue<(int Index, GeographicCell Cell)>();

        for (int current = 0; current < orderedAnomalies.Length; current++)
        {
            Anomaly currentAnomaly = orderedAnomalies[current];

            while (activeInTimeOrder.TryPeek(out (int Index, GeographicCell Cell) oldest))
            {
                if (currentAnomaly.AcquiredAtUtc - orderedAnomalies[oldest.Index].AcquiredAtUtc <= timeWindow)
                    break;

                (int Index, GeographicCell Cell) expired = activeInTimeOrder.Dequeue();
                activeByCell[expired.Cell].RemoveFirst();
            }

            GeographicCell cell = Geography.GetCell(currentAnomaly.Latitude, currentAnomaly.Longitude, cellSize);

            for (long horizontal = cell.Horizontal - 1; horizontal <= cell.Horizontal + 1; horizontal++)
            {
                for (long vertical = cell.Vertical - 1; vertical <= cell.Vertical + 1; vertical++)
                {
                    for (long depth = cell.Depth - 1; depth <= cell.Depth + 1; depth++)
                    {
                        if (!activeByCell.TryGetValue(new(horizontal, vertical, depth), out LinkedList<int>? candidates))
                            continue;

                        foreach (int candidate in candidates)
                        {
                            if (Geography.HaversineKilometers(orderedAnomalies[candidate], currentAnomaly) <= radiusKilometers)
                                Union(parents, candidate, current);
                        }
                    }
                }
            }

            // Empty cells can be reused; this index lives only for the current clustering pass.
            if (!activeByCell.TryGetValue(cell, out LinkedList<int>? currentCell))
            {
                currentCell = new();
                activeByCell.Add(cell, currentCell);
            }

            currentCell.AddLast(new LinkedListNode<int>(current));
            activeInTimeOrder.Enqueue((current, cell));
        }
    }

    private static void JoinTimeBoundedPairs(Anomaly[] orderedAnomalies, int[] parents, double radiusKilometers, TimeSpan timeWindow)
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

    private static void Union(int[] parents, int first, int second)
    {
        int firstRoot = Find(parents, first);
        int secondRoot = Find(parents, second);

        if (firstRoot != secondRoot)
            parents[secondRoot] = firstRoot;
    }
}
