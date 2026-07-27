using System.Collections.Immutable;

namespace ThermalWatch.Core;

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
    ImmutableArray<string> MemberIds)
{
    public static NotificationClusterSummary FromCluster(NotificationCluster cluster)
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
            Geography.ClusterDiameterKilometers(cluster.Members),
            [.. cluster.Members.Select(member => member.Id)]);
    }
}
