namespace ThermalWatch.Core;

public static class Geography
{
    internal const double EarthRadiusKilometers = 6371.0088;
    private const double SquareExponent = 2;

    public static double HaversineKilometers(Anomaly first, Anomaly second) =>
        HaversineKilometers(first.Latitude, first.Longitude, second.Latitude, second.Longitude);

    public static double HaversineKilometers(
        double firstLatitude,
        double firstLongitude,
        double secondLatitude,
        double secondLongitude)
    {
        double latitudeDelta = DegreesToRadians(secondLatitude - firstLatitude);
        double longitudeDelta = DegreesToRadians(secondLongitude - firstLongitude);
        double firstLatitudeRadians = DegreesToRadians(firstLatitude);
        double secondLatitudeRadians = DegreesToRadians(secondLatitude);

        double haversine = Math.Pow(Math.Sin(latitudeDelta / 2), SquareExponent)
            + Math.Cos(firstLatitudeRadians) * Math.Cos(secondLatitudeRadians)
            * Math.Pow(Math.Sin(longitudeDelta / 2), SquareExponent);

        return EarthRadiusKilometers * 2 * Math.Atan2(Math.Sqrt(haversine), Math.Sqrt(1 - haversine));
    }

    public static double ClusterDiameterKilometers(IReadOnlyList<Anomaly> anomalies)
    {
        double diameter = 0d;
        for (int first = 0; first < anomalies.Count; first++)
        {
            for (int second = first + 1; second < anomalies.Count; second++)
            {
                diameter = Math.Max(
                    diameter,
                    HaversineKilometers(anomalies[first], anomalies[second]));
            }
        }

        return diameter;
    }

    public static GeographicBounds? CreatePreviewBounds(
        double latitude,
        double longitude,
        double widthKilometers,
        double heightKilometers)
    {
        (double northLatitude, double _) = DestinationPoint(latitude, longitude, heightKilometers / 2, bearingDegrees: 0);
        (double southLatitude, double _) = DestinationPoint(latitude, longitude, heightKilometers / 2, bearingDegrees: 180);
        (double _, double eastLongitude) = DestinationPoint(latitude, longitude, widthKilometers / 2, bearingDegrees: 90);
        (double _, double westLongitude) = DestinationPoint(latitude, longitude, widthKilometers / 2, bearingDegrees: 270);

        if (westLongitude >= eastLongitude)
            return null;

        return new(westLongitude, southLatitude, eastLongitude, northLatitude);
    }

    private static (double Latitude, double Longitude) DestinationPoint(
        double latitude,
        double longitude,
        double distanceKilometers,
        double bearingDegrees)
    {
        double angularDistance = distanceKilometers / EarthRadiusKilometers;
        double bearing = DegreesToRadians(bearingDegrees);
        double latitudeRadians = DegreesToRadians(latitude);
        double longitudeRadians = DegreesToRadians(longitude);

        double destinationLatitude = Math.Asin(
            Math.Sin(latitudeRadians) * Math.Cos(angularDistance)
            + Math.Cos(latitudeRadians) * Math.Sin(angularDistance) * Math.Cos(bearing));

        double destinationLongitude = longitudeRadians + Math.Atan2(
            Math.Sin(bearing) * Math.Sin(angularDistance) * Math.Cos(latitudeRadians),
            Math.Cos(angularDistance) - Math.Sin(latitudeRadians) * Math.Sin(destinationLatitude));

        destinationLongitude = (destinationLongitude + 3 * Math.PI) % (2 * Math.PI) - Math.PI;
        return (RadiansToDegrees(destinationLatitude), RadiansToDegrees(destinationLongitude));
    }

    private static double DegreesToRadians(double value) => value * Math.PI / 180;

    private static double RadiansToDegrees(double value) => value * 180 / Math.PI;
}
