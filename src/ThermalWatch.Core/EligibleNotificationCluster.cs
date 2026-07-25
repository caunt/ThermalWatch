namespace ThermalWatch.Core;

public sealed record EligibleNotificationCluster(
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
    double ClusterDiameterKilometers);
