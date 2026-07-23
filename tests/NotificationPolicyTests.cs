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
            [Detection(
                id: "missing",
                dayNight: "N",
                frpMegawatts: null,
                brightnessKelvin: null,
                confidenceCategory: null)],
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

    [Theory]
    [InlineData("passed", null, null)]
    [InlineData("nighttime", NotificationRejectionReason.Nighttime, "daytime")]
    [InlineData("insufficient", NotificationRejectionReason.InsufficientDetections, "cluster-detections")]
    [InlineData("viirs-missing-confidence", NotificationRejectionReason.MissingRequiredValue, "confidence")]
    [InlineData("viirs-low-confidence", NotificationRejectionReason.LowConfidence, "confidence")]
    [InlineData("modis-missing-confidence", NotificationRejectionReason.MissingRequiredValue, "confidence")]
    [InlineData("modis-low-confidence", NotificationRejectionReason.LowConfidence, "confidence")]
    [InlineData("missing-frp", NotificationRejectionReason.MissingRequiredValue, "frp")]
    [InlineData("low-frp", NotificationRejectionReason.LowFrp, "frp")]
    [InlineData("missing-contrast", NotificationRejectionReason.MissingRequiredValue, "thermal-contrast")]
    [InlineData("low-contrast", NotificationRejectionReason.LowThermalContrast, "thermal-contrast")]
    public void EvaluateMetadataMatchesFirstBlockingDiagnostic(
        string scenario,
        NotificationRejectionReason? expectedReason,
        string? expectedCriterionCode)
    {
        Anomaly detection = scenario switch
        {
            "nighttime" => Detection(id: scenario, dayNight: "N"),
            "viirs-missing-confidence" => Detection(id: scenario, confidenceCategory: null),
            "viirs-low-confidence" => Detection(id: scenario, confidenceCategory: "low"),
            "modis-missing-confidence" => Detection(
                id: scenario,
                source: "MODIS_NRT",
                confidenceCategory: null,
                confidencePercent: null),
            "modis-low-confidence" => Detection(
                id: scenario,
                source: "MODIS_NRT",
                confidenceCategory: null,
                confidencePercent: 59),
            "missing-frp" => Detection(id: scenario, frpMegawatts: null),
            "low-frp" => Detection(id: scenario, frpMegawatts: 49),
            "missing-contrast" => Detection(id: scenario, brightnessKelvin: null),
            "low-contrast" => Detection(id: scenario, brightnessKelvin: 319),
            _ => Detection(id: scenario)
        };
        int detectionCount = scenario.Equals(value: "insufficient", StringComparison.Ordinal) ? 1 : 2;
        NotificationCluster cluster = Cluster(detection, detectionCount);
        NotificationVisibilityOptions options = DefaultVisibility();

        NotificationMetadataEvaluation evaluation = NotificationPolicy.EvaluateMetadata(cluster, options);
        NotificationCriterionResult? firstBlocking = NotificationPolicy.ExplainMetadata(cluster, options)
            .FirstOrDefault(criterion => criterion.IsBlocking);

        Assert.Equal(expectedReason is null, evaluation.IsAccepted);
        Assert.Equal(expectedReason, evaluation.RejectionReason);
        Assert.Equal(expectedCriterionCode, firstBlocking?.Code);
    }

    [Fact]
    public void EvaluateMetadataAcceptsWhenVisibilityPolicyIsDisabled()
    {
        NotificationCluster cluster = Cluster(
            Detection(
                id: "disabled",
                dayNight: "N",
                frpMegawatts: null,
                brightnessKelvin: null,
                confidenceCategory: null),
            detectionCount: 1);

        NotificationMetadataEvaluation evaluation = NotificationPolicy.EvaluateMetadata(
            cluster,
            DefaultVisibility() with { Enabled = false });

        Assert.True(evaluation.IsAccepted);
        Assert.Null(evaluation.RejectionReason);
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
        double? brightnessKelvin = 350,
        string source = "VIIRS_SNPP_NRT",
        double? confidencePercent = null,
        string? confidenceCategory = "nominal") =>
        new(
            id,
            CountryCode: "RUS",
            source,
            Satellite: "N",
            Instrument: source.Equals(value: "MODIS_NRT", StringComparison.Ordinal) ? "MODIS" : "VIIRS",
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
            confidencePercent,
            confidenceCategory,
            Version: "2.0NRT",
            GoogleMapsUrl: $"https://example.test/{id}");

    private static NotificationCluster Cluster(Anomaly detection, int detectionCount)
    {
        Anomaly[] detections = detectionCount == 1
            ? [detection]
            :
            [
                detection,
                detection with
                {
                    Id = $"{detection.Id}-context",
                    AcquiredAtUtc = detection.AcquiredAtUtc.AddMinutes(-1)
                }
            ];
        return Assert.Single(NotificationClustering.Create(
            detections,
            radiusKilometers: 5,
            timeWindow: TimeSpan.FromMinutes(minutes: 90)));
    }
}
