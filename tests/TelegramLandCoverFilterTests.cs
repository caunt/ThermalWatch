using System.Collections.Immutable;
using ThermalWatch.Core;
using ThermalWatch.Telegram;

namespace ThermalWatch.Tests;

public sealed class TelegramLandCoverFilterTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Evaluate_SuppressesStrongMultiSatelliteLargeVegetationClusterByDefault()
    {
        var members = Enumerable.Range(0, 12)
            .Select(index => Detection(
                index.ToString(),
                index == 0 ? 10_000 : 100,
                index % 2 == 0 ? "Suomi-NPP" : "NOAA-20"))
            .ToImmutableArray();
        var cluster = new NotificationCluster("cluster", members[0], members);

        var result = TelegramLandCoverFilter.Evaluate(
            cluster,
            DefaultOptions(),
            AvailableLandCover([1, 2, 3, 10]));

        Assert.Equal(LandCoverFilterDecision.Suppressed, result.Decision);
        Assert.True(result.IsSuppressed);
        Assert.Equal(100, result.VegetationPercent);
        Assert.False(result.HasBuiltUpWithinProximity);
        Assert.Equal(2024, result.LandCoverYear);
        Assert.Contains("no NASA class 13", result.Reason);
    }

    [Fact]
    public void Evaluate_SuppressesVegetationWhenFrpIsMissing()
    {
        var detection = Detection("missing-frp", null, "Suomi-NPP");

        var result = TelegramLandCoverFilter.Evaluate(
            new("cluster", detection, [detection]),
            DefaultOptions(),
            AvailableLandCover([1, 15]));

        Assert.Equal(LandCoverFilterDecision.Suppressed, result.Decision);
        Assert.Equal(50, result.VegetationPercent);
    }

    [Fact]
    public void Evaluate_RetainsClusterWithBuiltUpPixelNearby()
    {
        var detection = Detection("urban", 100, "Suomi-NPP");

        var result = TelegramLandCoverFilter.Evaluate(
            new("cluster", detection, [detection]),
            DefaultOptions(),
            AvailableLandCover([1, 2, 13], hasBuiltUp: true));

        Assert.Equal(LandCoverFilterDecision.Retained, result.Decision);
        Assert.True(result.HasBuiltUpWithinProximity);
        Assert.Contains("class 13", result.Reason);
    }

    [Fact]
    public void Evaluate_RetainsClusterBelowVegetationThreshold()
    {
        var detection = Detection("mixed", 100, "Suomi-NPP");

        var result = TelegramLandCoverFilter.Evaluate(
            new("cluster", detection, [detection]),
            DefaultOptions(),
            AvailableLandCover([1, 15, 16]));

        Assert.Equal(LandCoverFilterDecision.Retained, result.Decision);
        Assert.Equal(100d / 3, result.VegetationPercent!.Value, 8);
        Assert.Contains("below 50%", result.Reason);
    }

    [Fact]
    public void Evaluate_UsesExactlyTheConfiguredIgbpVegetationClasses()
    {
        var detection = Detection("classes", 100, "Suomi-NPP");

        var result = TelegramLandCoverFilter.Evaluate(
            new("cluster", detection, [detection]),
            DefaultOptions(vegetationPercentThreshold: 75),
            AvailableLandCover([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17]));

        Assert.Equal(LandCoverFilterDecision.Suppressed, result.Decision);
        Assert.Equal(13d * 100 / 17, result.VegetationPercent!.Value, 8);
    }

    [Fact]
    public void Evaluate_HonorsExplicitHighFrpVegetationException()
    {
        var detection = Detection("strong", 500, "Suomi-NPP");
        var options = DefaultOptions() with { KeepHighFrpVegetation = true };

        var result = TelegramLandCoverFilter.Evaluate(
            new("cluster", detection, [detection]),
            options,
            AvailableLandCover([1]));

        Assert.Equal(LandCoverFilterDecision.Retained, result.Decision);
        Assert.Contains("high-FRP", result.Reason);
    }

    [Fact]
    public void Evaluate_HonorsExplicitMultiSatelliteVegetationException()
    {
        var first = Detection("first", 100, "Suomi-NPP");
        var second = Detection("second", 90, "NOAA-20");
        var options = DefaultOptions() with { KeepMultiSatelliteVegetation = true };

        var result = TelegramLandCoverFilter.Evaluate(
            new("cluster", first, [first, second]),
            options,
            AvailableLandCover([1]));

        Assert.Equal(LandCoverFilterDecision.Retained, result.Decision);
        Assert.Contains("multi-satellite", result.Reason);
    }

    [Theory]
    [InlineData(false, 2024, new byte[] { 1 })]
    [InlineData(true, null, new byte[] { 1 })]
    [InlineData(true, 2024, new byte[] { })]
    [InlineData(true, 2024, new byte[] { 254 })]
    public void Evaluate_FailsOpenOnlyForUnavailableOrInvalidLandCover(
        bool isAvailable,
        int? year,
        byte[] sampledClasses)
    {
        var detection = Detection("unavailable", 100, "Suomi-NPP");

        var result = TelegramLandCoverFilter.Evaluate(
            new("cluster", detection, [detection]),
            DefaultOptions(),
            new(isAvailable, year, [.. sampledClasses], false));

        Assert.Equal(LandCoverFilterDecision.Unavailable, result.Decision);
        Assert.False(result.IsSuppressed);
        Assert.Null(result.VegetationPercent);
        Assert.Null(result.HasBuiltUpWithinProximity);
    }

    private static TelegramLandCoverOptions DefaultOptions(
        double vegetationPercentThreshold = 50) =>
        new(
            true,
            vegetationPercentThreshold,
            2,
            300,
            false,
            false);

    private static GibsLandCoverResult AvailableLandCover(
        byte[] sampledClasses,
        bool hasBuiltUp = false) =>
        new(true, 2024, [.. sampledClasses], hasBuiltUp);

    private static Anomaly Detection(
        string id,
        double? frpMegawatts,
        string satellite) =>
        new(
            id,
            "UKR",
            "VIIRS_SNPP_NRT",
            satellite,
            "VIIRS",
            50,
            30,
            ObservedAt,
            "D",
            330,
            300,
            frpMegawatts,
            0.4,
            0.4,
            "n",
            null,
            "nominal",
            "2.0NRT",
            $"https://www.google.com/maps?q=50,30&id={id}");
}
