using System.Collections.Immutable;

namespace ThermalWatch.Core;

/// <summary>The public, compact representation of a notification cluster.</summary>
/// <param name="ClusterId">The stable cluster identifier.</param>
/// <param name="RepresentativeId">The observation used for presentation and previews.</param>
/// <param name="CountryCode">The representative's country code.</param>
/// <param name="Source">The representative's FIRMS source.</param>
/// <param name="Satellite">The representative's satellite.</param>
/// <param name="Latitude">The representative's latitude in degrees.</param>
/// <param name="Longitude">The representative's longitude in degrees.</param>
/// <param name="AcquiredAtUtc">The representative's acquisition time.</param>
/// <param name="FrpMegawatts">The representative's available radiative power.</param>
/// <param name="TotalFrpMegawatts">The sum of available member radiative power.</param>
/// <param name="DetectionCount">The number of observations in the cluster.</param>
/// <param name="ClusterDiameterKilometers">The largest pairwise member distance.</param>
/// <param name="MemberIds">The ordered observation identifiers.</param>
public sealed record NotificationClusterSummary(
    string ClusterId,
    string RepresentativeId,
    string CountryCode,
    string Source,
    string Satellite,
    double Latitude,
    double Longitude,
    DateTimeOffset AcquiredAtUtc,
    double? FrpMegawatts,
    double? TotalFrpMegawatts,
    int DetectionCount,
    double ClusterDiameterKilometers,
    ImmutableArray<string> MemberIds
)
{
    /// <summary>Builds a summary including the largest pairwise member distance.</summary>
    public static NotificationClusterSummary FromCluster(NotificationCluster cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        return FromCluster(cluster, Geography.ClusterDiameterKilometers(cluster.Members));
    }

    internal static NotificationClusterSummary FromCluster(NotificationCluster cluster, double clusterDiameterKilometers)
    {
        Anomaly representative = cluster.Representative;

        return new(
            cluster.Id,
            representative.Id,
            representative.CountryCode,
            representative.Source,
            representative.Satellite,
            representative.Latitude,
            representative.Longitude,
            representative.AcquiredAtUtc,
            representative.FrpMegawatts,
            cluster.TotalFrpMegawatts,
            cluster.Members.Length,
            clusterDiameterKilometers,
            [.. cluster.Members.Select(static member => member.Id)]
        );
    }
}
