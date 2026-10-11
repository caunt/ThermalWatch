namespace ThermalWatch.Core;

/// <summary>Spherical distance, spatial-index, and imagery-coverage calculations.</summary>
public static class Geography
{
    internal const double EarthRadiusKilometers = 6371.0088;
    private const double SquareExponent = 2;

    /// <summary>Returns the greatest pairwise distance between cluster members.</summary>
    /// <typeparam name="TAnomalies">The indexed observation collection; value-type collections remain unboxed.</typeparam>
    /// <param name="anomalies">The cluster members.</param>
    public static double ClusterDiameterKilometers<TAnomalies>(TAnomalies anomalies)
        where TAnomalies : IReadOnlyList<Anomaly>
    {
        if (anomalies is null)
            throw new ArgumentNullException(nameof(anomalies));

        double diameter = 0d;

        for (int first = 0; first < anomalies.Count; first++)
        {
            for (int second = first + 1; second < anomalies.Count; second++)
                diameter = Math.Max(diameter, HaversineKilometers(anomalies[first], anomalies[second]));
        }

        return diameter;
    }

    /// <summary>Returns centered image bounds, or null for coverage crossing the antimeridian.</summary>
    /// <param name="latitude">The center latitude in degrees.</param>
    /// <param name="longitude">The center longitude in degrees.</param>
    /// <param name="widthKilometers">The full horizontal coverage.</param>
    /// <param name="heightKilometers">The full vertical coverage.</param>
    public static GeographicBounds? CreatePreviewBounds(double latitude, double longitude, double widthKilometers, double heightKilometers)
    {
        double northLatitude = DestinationPoint(latitude, longitude, heightKilometers / 2, bearingDegrees: 0).Latitude;
        double southLatitude = DestinationPoint(latitude, longitude, heightKilometers / 2, bearingDegrees: 180).Latitude;
        double eastLongitude = DestinationPoint(latitude, longitude, widthKilometers / 2, bearingDegrees: 90).Longitude;
        double westLongitude = DestinationPoint(latitude, longitude, widthKilometers / 2, bearingDegrees: 270).Longitude;

        return westLongitude >= eastLongitude ? null : new(westLongitude, southLatitude, eastLongitude, northLatitude);
    }

    /// <summary>Returns the great-circle distance between two observation coordinates.</summary>
    /// <param name="first">The first observation.</param>
    /// <param name="second">The second observation.</param>
    public static double HaversineKilometers(Anomaly first, Anomaly second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        return HaversineKilometers(first.Latitude, first.Longitude, second.Latitude, second.Longitude);
    }

    /// <summary>Returns the great-circle distance between WGS84 coordinates in kilometers.</summary>
    /// <param name="firstLatitude">The first latitude in degrees.</param>
    /// <param name="firstLongitude">The first longitude in degrees.</param>
    /// <param name="secondLatitude">The second latitude in degrees.</param>
    /// <param name="secondLongitude">The second longitude in degrees.</param>
    public static double HaversineKilometers(double firstLatitude, double firstLongitude, double secondLatitude, double secondLongitude)
    {
        double latitudeDelta = DegreesToRadians(secondLatitude - firstLatitude);
        double longitudeDelta = DegreesToRadians(secondLongitude - firstLongitude);
        double firstLatitudeRadians = DegreesToRadians(firstLatitude);
        double secondLatitudeRadians = DegreesToRadians(secondLatitude);

        double haversine = Math.Pow(Math.Sin(latitudeDelta / 2), SquareExponent)
            + (Math.Cos(firstLatitudeRadians) * Math.Cos(secondLatitudeRadians)
            * Math.Pow(Math.Sin(longitudeDelta / 2), SquareExponent));

        return EarthRadiusKilometers * 2 * Math.Atan2(Math.Sqrt(haversine), Math.Sqrt(1 - haversine));
    }

    internal static double ChordLength(double radiusKilometers)
    {
        return 2 * Math.Sin(radiusKilometers / (2 * EarthRadiusKilometers));
    }

    internal static GeographicCell GetCell(double latitude, double longitude, double cellSize)
    {
        double latitudeRadians = DegreesToRadians(latitude);
        double longitudeRadians = DegreesToRadians(longitude);
        double latitudeCosine = Math.Cos(latitudeRadians);

        return new(
            (long)Math.Floor(latitudeCosine * Math.Cos(longitudeRadians) / cellSize),
            (long)Math.Floor(latitudeCosine * Math.Sin(longitudeRadians) / cellSize),
            (long)Math.Floor(Math.Sin(latitudeRadians) / cellSize)
        );
    }

    private static double DegreesToRadians(double value)
    {
        return value * Math.PI / 180;
    }

    private static GeographicPoint DestinationPoint(double latitude, double longitude, double distanceKilometers, double bearingDegrees)
    {
        double angularDistance = distanceKilometers / EarthRadiusKilometers;
        double bearing = DegreesToRadians(bearingDegrees);
        double latitudeRadians = DegreesToRadians(latitude);
        double longitudeRadians = DegreesToRadians(longitude);

        double destinationLatitude = Math.Asin(
            (Math.Sin(latitudeRadians) * Math.Cos(angularDistance))
            + (Math.Cos(latitudeRadians) * Math.Sin(angularDistance) * Math.Cos(bearing))
        );

        double destinationLongitude = longitudeRadians + Math.Atan2(
            Math.Sin(bearing) * Math.Sin(angularDistance) * Math.Cos(latitudeRadians),
            Math.Cos(angularDistance) - (Math.Sin(latitudeRadians) * Math.Sin(destinationLatitude))
        );

        destinationLongitude = ((destinationLongitude + (3 * Math.PI)) % (2 * Math.PI)) - Math.PI;

        return new(RadiansToDegrees(destinationLatitude), RadiansToDegrees(destinationLongitude));
    }

    private static double RadiansToDegrees(double value)
    {
        return value * 180 / Math.PI;
    }

    private readonly record struct GeographicPoint(double Latitude, double Longitude);
}
