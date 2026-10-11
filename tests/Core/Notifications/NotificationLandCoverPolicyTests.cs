using System.Collections.Immutable;

using ThermalWatch.Core;

namespace ThermalWatch.Tests.Core.Notifications;

/// <summary>Verifies vegetation filtering, explicit exceptions, and unavailable imagery.</summary>
public sealed class NotificationLandCoverPolicyTests
{
    private static readonly DateTimeOffset ObservedAt = new(year: 2026, month: 7, day: 19, hour: 12, minute: 0, second: 0, TimeSpan.Zero);

    /// <summary>Provides unavailable, incomplete, empty, and invalid land-cover inputs.</summary>
    public static TheoryData<bool, int?, byte[]> UnavailableLandCoverCases => new()
    {
        { false, 2024, [1] },
        { true, null, [1] },
        { true, 2024, [] },
        { true, 2024, [254] }
    };

    /// <summary>Fails open when land-cover data cannot support a valid decision.</summary>
    [Theory]
    [MemberData(nameof(UnavailableLandCoverCases))]
    public void EvaluateFailsOpenOnlyForUnavailableOrInvalidLandCover(bool isAvailable, int? year, byte[] sampledClasses)
    {
        Anomaly anomaly = CreateAnomaly(id: "unavailable", frpMegawatts: 100, satellite: "Suomi-NPP");

        NotificationLandCoverResult result = NotificationLandCoverPolicy.Evaluate(
            new(Id: "cluster", anomaly, [anomaly]),
            DefaultOptions(),
            new(isAvailable, year, [.. sampledClasses], HasBuiltUpWithinProximity: false)
        );

        Assert.Equal(NotificationLandCoverDecision.Unavailable, result.Decision);
        Assert.False(result.VegetationPercent.HasValue);
        Assert.False(result.HasBuiltUpWithinProximity.HasValue);
    }

    /// <summary>Honors the explicitly enabled high-FRP vegetation exception.</summary>
    [Fact]
    public void EvaluateHonorsExplicitHighFrpVegetationException()
    {
        Anomaly anomaly = CreateAnomaly(id: "strong", frpMegawatts: 500, satellite: "Suomi-NPP");
        NotificationLandCoverOptions options = DefaultOptions() with { KeepHighFrpVegetation = true };

        NotificationLandCoverResult result = NotificationLandCoverPolicy.Evaluate(new(Id: "cluster", anomaly, [anomaly]), options, AvailableLandCover([1]));

        Assert.Equal(NotificationLandCoverDecision.Retained, result.Decision);
        Assert.Contains(expectedSubstring: "high-FRP", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>Honors the explicitly enabled multi-satellite vegetation exception.</summary>
    [Fact]
    public void EvaluateHonorsExplicitMultiSatelliteVegetationException()
    {
        Anomaly first = CreateAnomaly(id: "first", frpMegawatts: 100, satellite: "Suomi-NPP");
        Anomaly second = CreateAnomaly(id: "second", frpMegawatts: 90, satellite: "NOAA-20");
        NotificationLandCoverOptions options = DefaultOptions() with { KeepMultiSatelliteVegetation = true };

        NotificationLandCoverResult result = NotificationLandCoverPolicy.Evaluate(new(Id: "cluster", first, [first, second]), options, AvailableLandCover([1]));

        Assert.Equal(NotificationLandCoverDecision.Retained, result.Decision);
        Assert.Contains(expectedSubstring: "multi-satellite", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>Retains observations when vegetation coverage is below the threshold.</summary>
    [Fact]
    public void EvaluateRetainsClusterBelowVegetationThreshold()
    {
        Anomaly anomaly = CreateAnomaly(id: "mixed", frpMegawatts: 100, satellite: "Suomi-NPP");

        NotificationLandCoverResult result = NotificationLandCoverPolicy.Evaluate(new(Id: "cluster", anomaly, [anomaly]), DefaultOptions(), AvailableLandCover([1, 15, 16]));

        Assert.Equal(NotificationLandCoverDecision.Retained, result.Decision);
        Assert.True(result.VegetationPercent.HasValue);
        Assert.Equal(100d / 3, result.VegetationPercent.GetValueOrDefault(), precision: 8);
        Assert.Contains(expectedSubstring: "below 50%", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>Retains vegetation clusters with built-up pixels nearby.</summary>
    [Fact]
    public void EvaluateRetainsClusterWithBuiltUpPixelNearby()
    {
        Anomaly anomaly = CreateAnomaly(id: "urban", frpMegawatts: 100, satellite: "Suomi-NPP");

        NotificationLandCoverResult result = NotificationLandCoverPolicy.Evaluate(new(Id: "cluster", anomaly, [anomaly]), DefaultOptions(), AvailableLandCover([1, 2, 13], hasBuiltUp: true));

        Assert.Equal(NotificationLandCoverDecision.Retained, result.Decision);
        Assert.True(result.HasBuiltUpWithinProximity);
        Assert.Contains(expectedSubstring: "class 13", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>Suppresses strong multi-satellite vegetation clusters by default.</summary>
    [Fact]
    public void EvaluateSuppressesStrongMultiSatelliteLargeVegetationClusterByDefault()
    {
        var members = Enumerable.Range(start: 0, count: 12)
            .Select(
                index => CreateAnomaly(
                    index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    index == 0 ? 10_000 : 100,
                    index % 2 == 0 ? "Suomi-NPP" : "NOAA-20"
                )
            )
            .ToImmutableArray();

        var cluster = new NotificationCluster(Id: "cluster", members[index: 0], members);

        NotificationLandCoverResult result = NotificationLandCoverPolicy.Evaluate(cluster, DefaultOptions(), AvailableLandCover([1, 2, 3, 10]));

        Assert.Equal(NotificationLandCoverDecision.Suppressed, result.Decision);
        Assert.Equal(expected: 100, result.VegetationPercent);
        Assert.False(result.HasBuiltUpWithinProximity);
        Assert.Equal(expected: 2024, result.LandCoverYear);
        Assert.Contains(expectedSubstring: "no NASA class 13", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>Does not require FRP to suppress an otherwise eligible vegetation cluster.</summary>
    [Fact]
    public void EvaluateSuppressesVegetationWhenFrpIsMissing()
    {
        Anomaly anomaly = CreateAnomaly(id: "missing-frp", frpMegawatts: null, satellite: "Suomi-NPP");

        NotificationLandCoverResult result = NotificationLandCoverPolicy.Evaluate(new(Id: "cluster", anomaly, [anomaly]), DefaultOptions(), AvailableLandCover([1, 15]));

        Assert.Equal(NotificationLandCoverDecision.Suppressed, result.Decision);
        Assert.Equal(expected: 50, result.VegetationPercent);
    }

    /// <summary>Counts exactly the supported IGBP vegetation classes.</summary>
    [Fact]
    public void EvaluateUsesExactlyTheConfiguredIgbpVegetationClasses()
    {
        Anomaly anomaly = CreateAnomaly(id: "classes", frpMegawatts: 100, satellite: "Suomi-NPP");

        NotificationLandCoverResult result = NotificationLandCoverPolicy.Evaluate(
            new(Id: "cluster", anomaly, [anomaly]),
            DefaultOptions(vegetationPercentThreshold: 75),
            AvailableLandCover([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17])
        );

        Assert.Equal(NotificationLandCoverDecision.Suppressed, result.Decision);
        Assert.True(result.VegetationPercent.HasValue);
        Assert.Equal(13d * 100 / 17, result.VegetationPercent.GetValueOrDefault(), precision: 8);
    }

    private static GibsLandCoverResult AvailableLandCover(byte[] sampledClasses, bool hasBuiltUp = false)
    {
        return new(IsAvailable: true, Year: 2024, [.. sampledClasses], hasBuiltUp);
    }

    private static Anomaly CreateAnomaly(string id, double? frpMegawatts, string satellite)
    {
        return new(
            id,
            CountryCode: "UKR",
            Source: "VIIRS_SNPP_NRT",
            satellite,
            Instrument: "VIIRS",
            Latitude: 50,
            Longitude: 30,
            ObservedAt,
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
            new Uri($"https://www.google.com/maps?q=50,30&id={id}")
        );
    }

    private static NotificationLandCoverOptions DefaultOptions(double vegetationPercentThreshold = 50)
    {
        return new(
            Enabled: true,
            vegetationPercentThreshold,
            BuiltUpProximityKilometers: 2,
            VegetationMaximumFrpMegawatts: 300,
            KeepHighFrpVegetation: false,
            KeepMultiSatelliteVegetation: false
        );
    }
}
