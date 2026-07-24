using System.Collections.Immutable;
using ThermalWatch.Core;

namespace ThermalWatch.Tests;

public sealed class NotificationLandCoverPolicyTests
{
    private static readonly DateTimeOffset s_observedAt = new(year: 2026, month: 7, day: 19, hour: 12, minute: 0, second: 0, TimeSpan.Zero);

    [Fact]
    public void EvaluateSuppressesStrongMultiSatelliteLargeVegetationClusterByDefault()
    {
        var members = Enumerable.Range(start: 0, count: 12)
            .Select(index => CreateAnomaly(
                index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                index == 0 ? 10_000 : 100,
                index % 2 == 0 ? "Suomi-NPP" : "NOAA-20"))
            .ToImmutableArray();
        var cluster = new NotificationCluster(Id: "cluster", members[0], members);

        NotificationLandCoverResult result = NotificationLandCoverPolicy.Evaluate(
            cluster,
            DefaultOptions(),
            AvailableLandCover([1, 2, 3, 10]));

        Assert.Equal(NotificationLandCoverDecision.Suppressed, result.Decision);
        Assert.True(result.IsSuppressed);
        Assert.Equal(100, result.VegetationPercent);
        Assert.False(result.HasBuiltUpWithinProximity);
        Assert.Equal(2024, result.LandCoverYear);
        Assert.Contains("no NASA class 13", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluateSuppressesVegetationWhenFrpIsMissing()
    {
        Anomaly anomaly = CreateAnomaly(id: "missing-frp", frpMegawatts: null, satellite: "Suomi-NPP");

        NotificationLandCoverResult result = NotificationLandCoverPolicy.Evaluate(
            new(Id: "cluster", anomaly, [anomaly]),
            DefaultOptions(),
            AvailableLandCover([1, 15]));

        Assert.Equal(NotificationLandCoverDecision.Suppressed, result.Decision);
        Assert.Equal(50, result.VegetationPercent);
    }

    [Fact]
    public void EvaluateRetainsClusterWithBuiltUpPixelNearby()
    {
        Anomaly anomaly = CreateAnomaly(id: "urban", frpMegawatts: 100, satellite: "Suomi-NPP");

        NotificationLandCoverResult result = NotificationLandCoverPolicy.Evaluate(
            new(Id: "cluster", anomaly, [anomaly]),
            DefaultOptions(),
            AvailableLandCover([1, 2, 13], hasBuiltUp: true));

        Assert.Equal(NotificationLandCoverDecision.Retained, result.Decision);
        Assert.True(result.HasBuiltUpWithinProximity);
        Assert.Contains("class 13", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluateRetainsClusterBelowVegetationThreshold()
    {
        Anomaly anomaly = CreateAnomaly(id: "mixed", frpMegawatts: 100, satellite: "Suomi-NPP");

        NotificationLandCoverResult result = NotificationLandCoverPolicy.Evaluate(
            new(Id: "cluster", anomaly, [anomaly]),
            DefaultOptions(),
            AvailableLandCover([1, 15, 16]));

        Assert.Equal(NotificationLandCoverDecision.Retained, result.Decision);
        Assert.Equal(100d / 3, result.VegetationPercent!.Value, 8);
        Assert.Contains("below 50%", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluateUsesExactlyTheConfiguredIgbpVegetationClasses()
    {
        Anomaly anomaly = CreateAnomaly(id: "classes", frpMegawatts: 100, satellite: "Suomi-NPP");

        NotificationLandCoverResult result = NotificationLandCoverPolicy.Evaluate(
            new(Id: "cluster", anomaly, [anomaly]),
            DefaultOptions(vegetationPercentThreshold: 75),
            AvailableLandCover([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17]));

        Assert.Equal(NotificationLandCoverDecision.Suppressed, result.Decision);
        Assert.Equal(13d * 100 / 17, result.VegetationPercent!.Value, 8);
    }

    [Fact]
    public void EvaluateHonorsExplicitHighFrpVegetationException()
    {
        Anomaly anomaly = CreateAnomaly(id: "strong", frpMegawatts: 500, satellite: "Suomi-NPP");
        NotificationLandCoverOptions options = DefaultOptions() with { KeepHighFrpVegetation = true };

        NotificationLandCoverResult result = NotificationLandCoverPolicy.Evaluate(
            new(Id: "cluster", anomaly, [anomaly]),
            options,
            AvailableLandCover([1]));

        Assert.Equal(NotificationLandCoverDecision.Retained, result.Decision);
        Assert.Contains("high-FRP", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluateHonorsExplicitMultiSatelliteVegetationException()
    {
        Anomaly first = CreateAnomaly(id: "first", frpMegawatts: 100, satellite: "Suomi-NPP");
        Anomaly second = CreateAnomaly(id: "second", frpMegawatts: 90, satellite: "NOAA-20");
        NotificationLandCoverOptions options = DefaultOptions() with { KeepMultiSatelliteVegetation = true };

        NotificationLandCoverResult result = NotificationLandCoverPolicy.Evaluate(
            new(Id: "cluster", first, [first, second]),
            options,
            AvailableLandCover([1]));

        Assert.Equal(NotificationLandCoverDecision.Retained, result.Decision);
        Assert.Contains("multi-satellite", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, 2024, new byte[] { 1 })]
    [InlineData(true, null, new byte[] { 1 })]
    [InlineData(true, 2024, new byte[] { })]
    [InlineData(true, 2024, new byte[] { 254 })]
    public void EvaluateFailsOpenOnlyForUnavailableOrInvalidLandCover(
        bool isAvailable,
        int? year,
        byte[] sampledClasses)
    {
        Anomaly anomaly = CreateAnomaly(id: "unavailable", frpMegawatts: 100, satellite: "Suomi-NPP");

        NotificationLandCoverResult result = NotificationLandCoverPolicy.Evaluate(
            new(Id: "cluster", anomaly, [anomaly]),
            DefaultOptions(),
            new(isAvailable, year, [.. sampledClasses], false));

        Assert.Equal(NotificationLandCoverDecision.Unavailable, result.Decision);
        Assert.False(result.IsSuppressed);
        Assert.Null(result.VegetationPercent);
        Assert.Null(result.HasBuiltUpWithinProximity);
    }

    private static NotificationLandCoverOptions DefaultOptions(
        double vegetationPercentThreshold = 50) =>
        new(
            Enabled: true,
            vegetationPercentThreshold,
            BuiltUpProximityKilometers: 2,
            VegetationMaximumFrpMegawatts: 300,
            KeepHighFrpVegetation: false,
            KeepMultiSatelliteVegetation: false);

    private static GibsLandCoverResult AvailableLandCover(
        byte[] sampledClasses,
        bool hasBuiltUp = false) =>
        new(IsAvailable: true, Year: 2024, [.. sampledClasses], hasBuiltUp);

    private static Anomaly CreateAnomaly(
        string id,
        double? frpMegawatts,
        string satellite) =>
        new(
            id,
            CountryCode: "UKR",
            Source: "VIIRS_SNPP_NRT",
            satellite,
            Instrument: "VIIRS",
            Latitude: 50,
            Longitude: 30,
            s_observedAt,
            DayNight: "D",
            BrightnessKelvin: 330,
            SecondaryBrightnessKelvin: 300,
            frpMegawatts,
            ScanKilometers: 0.4,
            TrackKilometers: 0.4,
            ConfidenceRaw: "n",
            ConfidencePercent: null,
            ConfidenceCategory: "nominal",
            Version: "2.0NRT",
            GoogleMapsUrl: $"https://www.google.com/maps?q=50,30&id={id}");
}
