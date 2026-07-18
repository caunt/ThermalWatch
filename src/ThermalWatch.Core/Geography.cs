using System.Globalization;

namespace ThermalWatch.Core;

public readonly record struct GeographicBounds(double West, double South, double East, double North)
{
    public string ToInvariantString() => string.Join(',',
        West.ToString("0.######", CultureInfo.InvariantCulture),
        South.ToString("0.######", CultureInfo.InvariantCulture),
        East.ToString("0.######", CultureInfo.InvariantCulture),
        North.ToString("0.######", CultureInfo.InvariantCulture));
}

public static class Geography
{
    private const double EarthRadiusKilometers = 6371.0088;

    public static double HaversineKilometers(Anomaly first, Anomaly second) =>
        HaversineKilometers(first.Latitude, first.Longitude, second.Latitude, second.Longitude);

    public static double HaversineKilometers(
        double firstLatitude,
        double firstLongitude,
        double secondLatitude,
        double secondLongitude)
    {
        var latitudeDelta = DegreesToRadians(secondLatitude - firstLatitude);
        var longitudeDelta = DegreesToRadians(secondLongitude - firstLongitude);
        var firstLatitudeRadians = DegreesToRadians(firstLatitude);
        var secondLatitudeRadians = DegreesToRadians(secondLatitude);

        var haversine = Math.Pow(Math.Sin(latitudeDelta / 2), 2)
            + Math.Cos(firstLatitudeRadians) * Math.Cos(secondLatitudeRadians)
            * Math.Pow(Math.Sin(longitudeDelta / 2), 2);

        return EarthRadiusKilometers * 2 * Math.Atan2(Math.Sqrt(haversine), Math.Sqrt(1 - haversine));
    }

    public static double ClusterDiameterKilometers(IReadOnlyList<Anomaly> detections)
    {
        var diameter = 0d;
        for (var first = 0; first < detections.Count; first++)
        {
            for (var second = first + 1; second < detections.Count; second++)
            {
                diameter = Math.Max(
                    diameter,
                    HaversineKilometers(detections[first], detections[second]));
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
        var (northLatitude, _) = DestinationPoint(latitude, longitude, heightKilometers / 2, 0);
        var (southLatitude, _) = DestinationPoint(latitude, longitude, heightKilometers / 2, 180);
        var (_, eastLongitude) = DestinationPoint(latitude, longitude, widthKilometers / 2, 90);
        var (_, westLongitude) = DestinationPoint(latitude, longitude, widthKilometers / 2, 270);

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
        var angularDistance = distanceKilometers / EarthRadiusKilometers;
        var bearing = DegreesToRadians(bearingDegrees);
        var latitudeRadians = DegreesToRadians(latitude);
        var longitudeRadians = DegreesToRadians(longitude);

        var destinationLatitude = Math.Asin(
            Math.Sin(latitudeRadians) * Math.Cos(angularDistance)
            + Math.Cos(latitudeRadians) * Math.Sin(angularDistance) * Math.Cos(bearing));

        var destinationLongitude = longitudeRadians + Math.Atan2(
            Math.Sin(bearing) * Math.Sin(angularDistance) * Math.Cos(latitudeRadians),
            Math.Cos(angularDistance) - Math.Sin(latitudeRadians) * Math.Sin(destinationLatitude));

        destinationLongitude = (destinationLongitude + 3 * Math.PI) % (2 * Math.PI) - Math.PI;
        return (RadiansToDegrees(destinationLatitude), RadiansToDegrees(destinationLongitude));
    }

    private static double DegreesToRadians(double value) => value * Math.PI / 180;

    private static double RadiansToDegrees(double value) => value * 180 / Math.PI;
}
