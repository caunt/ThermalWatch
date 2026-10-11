namespace ThermalWatch.Core;

/// <summary>Ranked OpenStreetMap presentation context; proximity does not establish an observation's cause.</summary>
/// <param name="OsmType">The node, way, or relation element kind.</param>
/// <param name="OsmId">The OpenStreetMap element identifier.</param>
/// <param name="Name">The trimmed mapped feature name.</param>
/// <param name="Latitude">The node latitude or supplied center latitude in degrees.</param>
/// <param name="Longitude">The node longitude or supplied center longitude in degrees.</param>
/// <param name="DistanceKilometers">The distance from the selected observation.</param>
/// <param name="OpenStreetMapUrl">The mapped element's navigation address.</param>
public sealed record NearbyFeature(string OsmType, long OsmId, string Name, double Latitude, double Longitude, double DistanceKilometers, Uri OpenStreetMapUrl);
