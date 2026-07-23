namespace ThermalWatch.Core;

public sealed record NearbyFeature(
    string OsmType,
    long OsmId,
    string Name,
    double Latitude,
    double Longitude,
    double DistanceKilometers,
    string OpenStreetMapUrl);
