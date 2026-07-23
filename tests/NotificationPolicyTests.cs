using ThermalWatch.Core;

namespace ThermalWatch.Tests;

public sealed class NotificationPolicyTests
{
    private static readonly DateTimeOffset s_observedAt = new(
        year: 2026,
        month: 7,
        day: 23,
        hour: 8,
        minute: 0,
        second: 0,
        TimeSpan.Zero);

    [Fact]
    public void ExplainMetadataReportsEveryFailure()
    {
        NotificationCluster cluster = Assert.Single(NotificationClustering.Create(
            [Detection(id: "missing", dayNight: "N", frpMegawatts: null, brightnessKelvin: null)],
            radiusKilometers: 5,
            timeWindow: TimeSpan.FromMinutes(minutes: 90)));

        NotificationCriterionResult[] criteria = [.. NotificationPolicy.ExplainMetadata(
            cluster,
            DefaultVisibility())];

        Assert.Equal(5, criteria.Length);
        Assert.All(criteria, criterion => Assert.Equal(NotificationCriterionOutcomes.Failed, criterion.Outcome));
        Assert.All(criteria, criterion => Assert.True(criterion.IsBlocking));
        Assert.Equal(
            ["daytime", "cluster-detections", "confidence", "frp", "thermal-contrast"],
            criteria.Select(criterion => criterion.Code));
    }

    [Fact]
    public void ExplainMetadataMarksEveryCriterionDisabledWithTheVisibilityPolicy()
    {
        NotificationCluster cluster = Assert.Single(NotificationClustering.Create(
            [Detection(id: "detection")],
            radiusKilometers: 5,
            timeWindow: TimeSpan.FromMinutes(minutes: 90)));

        NotificationCriterionResult[] criteria = [.. NotificationPolicy.ExplainMetadata(
            cluster,
            DefaultVisibility() with { Enabled = false })];

        Assert.Equal(5, criteria.Length);
        Assert.All(criteria, criterion => Assert.Equal(NotificationCriterionOutcomes.Disabled, criterion.Outcome));
        Assert.All(criteria, criterion => Assert.False(criterion.IsBlocking));
    }

    private static NotificationVisibilityOptions DefaultVisibility() =>
        new(
            Enabled: true,
            MinimumFrpMegawatts: 50,
            MinimumThermalContrastKelvin: 20,
            MinimumClusterDetections: 2,
            MinimumModisConfidencePercent: 60,
            MinimumViirsConfidence: NotificationViirsConfidenceLevel.Nominal,
            RequireDaytime: true,
            RequirePreview: true);

    private static Anomaly Detection(
        string id,
        string dayNight = "D",
        double? frpMegawatts = 100,
        double? brightnessKelvin = 350) =>
        new(
            id,
            CountryCode: "RUS",
            Source: "VIIRS_SNPP_NRT",
            Satellite: "N",
            Instrument: "VIIRS",
            Latitude: 50,
            Longitude: 30,
            s_observedAt,
            dayNight,
            brightnessKelvin,
            SecondaryBrightnessKelvin: 300,
            frpMegawatts,
            ScanKilometers: 0.4,
            TrackKilometers: 0.4,
            ConfidenceRaw: null,
            ConfidencePercent: null,
            ConfidenceCategory: null,
            Version: "2.0NRT",
            GoogleMapsUrl: $"https://example.test/{id}");
}
