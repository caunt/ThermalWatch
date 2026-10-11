using ThermalWatch.Core;

namespace ThermalWatch.Tests.Core.Notifications;

/// <summary>Verifies metadata eligibility and exhaustive diagnostic criteria.</summary>
public sealed class NotificationPolicyTests
{
    private static readonly DateTimeOffset ObservedAt = new(year: 2026, month: 7, day: 23, hour: 8, minute: 0, second: 0, TimeSpan.Zero);

    /// <summary>Provides cases below and exactly at the minimum cluster FRP.</summary>
    public static TheoryData<double, bool, string> ClusterTotalFrpCases => new()
    {
        { 24, false, "48 MW" },
        { 25, true, "50 MW" }
    };

    /// <summary>Provides each metadata rejection and its first blocking criterion.</summary>
    public static TheoryData<string, NotificationRejectionReason?, string?> MetadataCases => new()
    {
        { "passed", null, null },
        { "nighttime", NotificationRejectionReason.Nighttime, "daytime" },
        { "insufficient", NotificationRejectionReason.InsufficientDetections, "cluster-detections" },
        { "viirs-missing-confidence", NotificationRejectionReason.MissingRequiredValue, "confidence" },
        { "viirs-low-confidence", NotificationRejectionReason.LowConfidence, "confidence" },
        { "modis-missing-confidence", NotificationRejectionReason.MissingRequiredValue, "confidence" },
        { "modis-low-confidence", NotificationRejectionReason.LowConfidence, "confidence" },
        { "missing-frp", NotificationRejectionReason.MissingRequiredValue, "frp" },
        { "low-frp", NotificationRejectionReason.LowFrp, "frp" },
        { "missing-contrast", NotificationRejectionReason.MissingRequiredValue, "thermal-contrast" },
        { "low-contrast", NotificationRejectionReason.LowThermalContrast, "thermal-contrast" }
    };

    /// <summary>Accepts observations when metadata visibility filtering is disabled.</summary>
    [Fact]
    public void EvaluateMetadataAcceptsWhenVisibilityPolicyIsDisabled()
    {
        NotificationCluster cluster = Cluster(
            CreateAnomaly(id: "disabled", dayNight: "N", frpMegawatts: null, brightnessKelvin: null, confidenceCategory: null),
            detectionCount: 1
        );

        NotificationMetadataEvaluation evaluation = NotificationPolicy.EvaluateMetadata(cluster, DefaultVisibility() with { Enabled = false });

        Assert.True(evaluation.IsEligible);
        Assert.False(evaluation.RejectionReason.HasValue);
    }

    /// <summary>Applies the total-FRP threshold to all cluster members.</summary>
    [Theory]
    [MemberData(nameof(ClusterTotalFrpCases))]
    public void EvaluateMetadataAppliesMinimumClusterTotalFrp(double memberFrpMegawatts, bool expectedEligible, string expectedActualValue)
    {
        NotificationCluster cluster = Cluster(CreateAnomaly(id: "total-frp", frpMegawatts: memberFrpMegawatts), detectionCount: 2);

        NotificationVisibilityOptions options = DefaultVisibility() with
        {
            MinimumFrpMegawatts = 0,
            MinimumClusterTotalFrpMegawatts = 50
        };

        NotificationMetadataEvaluation evaluation = NotificationPolicy.EvaluateMetadata(cluster, options);

        NotificationCriterionResult criterion = Assert.Single(
            NotificationPolicy.ExplainMetadata(cluster, options).ToArray(),
            candidate => candidate.Code.Equals(value: "cluster-total-frp", StringComparison.Ordinal)
        );

        Assert.Equal(expectedEligible, evaluation.IsEligible);
        Assert.Equal(expectedEligible ? null : NotificationRejectionReason.LowClusterTotalFrp, evaluation.RejectionReason);
        Assert.Equal(expectedEligible, !criterion.IsBlocking);
        Assert.Equal(expectedActualValue, criterion.ActualValue);
    }

    /// <summary>Keeps eligibility rejection consistent with the first blocking diagnostic.</summary>
    [Theory]
    [MemberData(nameof(MetadataCases))]
    public void EvaluateMetadataMatchesFirstBlockingDiagnostic(string scenario, NotificationRejectionReason? expectedReason, string? expectedCriterionCode)
    {
        Anomaly anomaly = scenario switch
        {
            "nighttime" => CreateAnomaly(scenario, dayNight: "N"),
            "viirs-missing-confidence" => CreateAnomaly(id: scenario, confidenceCategory: null),
            "viirs-low-confidence" => CreateAnomaly(id: scenario, confidenceCategory: "low"),
            "modis-missing-confidence" => CreateAnomaly(id: scenario, source: "MODIS_NRT", confidenceCategory: null, confidencePercent: null),
            "modis-low-confidence" => CreateAnomaly(id: scenario, source: "MODIS_NRT", confidenceCategory: null, confidencePercent: 59),
            "missing-frp" => CreateAnomaly(id: scenario, frpMegawatts: null),
            "low-frp" => CreateAnomaly(id: scenario, frpMegawatts: 49),
            "missing-contrast" => CreateAnomaly(id: scenario, brightnessKelvin: null),
            "low-contrast" => CreateAnomaly(id: scenario, brightnessKelvin: 319),
            _ => CreateAnomaly(scenario)
        };

        int detectionCount = scenario.Equals(value: "insufficient", StringComparison.Ordinal) ? 1 : 2;
        NotificationCluster cluster = Cluster(anomaly, detectionCount);
        NotificationVisibilityOptions options = DefaultVisibility();

        NotificationMetadataEvaluation evaluation = NotificationPolicy.EvaluateMetadata(cluster, options);

        NotificationCriterionResult? firstBlocking = NotificationPolicy.ExplainMetadata(cluster, options)
            .FirstOrDefault(criterion => criterion.IsBlocking);

        Assert.Equal(expectedReason is null, evaluation.IsEligible);
        Assert.Equal(expectedReason, evaluation.RejectionReason);
        Assert.Equal(expectedCriterionCode, firstBlocking?.Code);
    }

    /// <summary>Rejects a required but unavailable cluster FRP total.</summary>
    [Fact]
    public void EvaluateMetadataRequiresAnAvailableClusterTotalFrp()
    {
        NotificationCluster cluster = Cluster(CreateAnomaly(id: "missing-total-frp", frpMegawatts: null), detectionCount: 2);

        NotificationVisibilityOptions options = DefaultVisibility() with
        {
            MinimumFrpMegawatts = 0,
            MinimumClusterTotalFrpMegawatts = 50
        };

        NotificationMetadataEvaluation evaluation = NotificationPolicy.EvaluateMetadata(cluster, options);

        NotificationCriterionResult criterion = Assert.Single(
            NotificationPolicy.ExplainMetadata(cluster, options).ToArray(),
            candidate => candidate.Code.Equals(value: "cluster-total-frp", StringComparison.Ordinal)
        );

        Assert.False(evaluation.IsEligible);
        Assert.Equal(NotificationRejectionReason.MissingRequiredValue, evaluation.RejectionReason);
        Assert.Equal(expected: "Not available", criterion.ActualValue);
        Assert.True(criterion.IsBlocking);
    }

    /// <summary>Disables the cluster FRP criterion when its minimum is zero.</summary>
    [Fact]
    public void ExplainMetadataDisablesZeroMinimumClusterTotalFrp()
    {
        NotificationCluster cluster = Cluster(CreateAnomaly(id: "disabled-total-frp", frpMegawatts: null), detectionCount: 2);

        NotificationVisibilityOptions options = DefaultVisibility() with
        {
            MinimumFrpMegawatts = 0,
            MinimumClusterTotalFrpMegawatts = 0
        };

        NotificationCriterionResult criterion = Assert.Single(
            NotificationPolicy.ExplainMetadata(cluster, options).ToArray(),
            candidate => candidate.Code.Equals(value: "cluster-total-frp", StringComparison.Ordinal)
        );

        Assert.Equal(NotificationCriterionOutcomes.Disabled, criterion.Outcome);
        Assert.False(criterion.IsBlocking);
    }

    /// <summary>Reports all criteria disabled when visibility filtering is disabled.</summary>
    [Fact]
    public void ExplainMetadataMarksEveryCriterionDisabledWithTheVisibilityPolicy()
    {
        NotificationCluster cluster = Assert.Single(
            NotificationClustering.Create<Anomaly[]>([CreateAnomaly(id: "anomaly")], radiusKilometers: 5, TimeSpan.FromMinutes(minutes: 90)).ToArray()
        );

        NotificationCriterionResult[] criteria = [.. NotificationPolicy.ExplainMetadata(cluster, DefaultVisibility() with { Enabled = false })];

        Assert.Equal(expected: 6, criteria.Length);
        Assert.All(criteria, criterion => Assert.Equal(NotificationCriterionOutcomes.Disabled, criterion.Outcome));
        Assert.All(criteria, criterion => Assert.False(criterion.IsBlocking));
    }

    /// <summary>Reports every failure in policy order instead of stopping at the first rejection.</summary>
    [Fact]
    public void ExplainMetadataReportsEveryFailure()
    {
        NotificationCluster cluster = Assert.Single(
            NotificationClustering.Create<Anomaly[]>(
                [CreateAnomaly(id: "missing", dayNight: "N", frpMegawatts: null, brightnessKelvin: null, confidenceCategory: null)],
                radiusKilometers: 5,
                TimeSpan.FromMinutes(minutes: 90)
            ).ToArray()
        );

        NotificationCriterionResult[] criteria = [.. NotificationPolicy.ExplainMetadata(cluster, DefaultVisibility())];

        Assert.Equal(expected: 6, criteria.Length);
        Assert.All(criteria, criterion => Assert.Equal(NotificationCriterionOutcomes.Failed, criterion.Outcome));
        Assert.All(criteria, criterion => Assert.True(criterion.IsBlocking));
        Assert.Equal(
            ["daytime", "cluster-detections", "confidence", "frp", "cluster-total-frp", "thermal-contrast"],
            criteria.Select(criterion => criterion.Code),
            StringComparer.Ordinal
        );
    }

    private static NotificationCluster Cluster(Anomaly anomaly, int detectionCount)
    {
        Anomaly[] anomalies = detectionCount == 1
            ? [anomaly]
            :
            [
                anomaly,
                anomaly with
                {
                    Id = $"{anomaly.Id}-context",
                    AcquiredAtUtc = anomaly.AcquiredAtUtc.AddMinutes(minutes: -1)
                }
            ];

        return Assert.Single(NotificationClustering.Create(anomalies, radiusKilometers: 5, TimeSpan.FromMinutes(minutes: 90)).ToArray());
    }

    private static Anomaly CreateAnomaly(
        string id,
        string dayNight = "D",
        double? frpMegawatts = 100,
        double? brightnessKelvin = 350,
        string source = "VIIRS_SNPP_NRT",
        double? confidencePercent = null,
        string? confidenceCategory = "nominal"
    )
    {
        return new(
            id,
            CountryCode: "RUS",
            source,
            Satellite: "N",
            source.Equals(value: "MODIS_NRT", StringComparison.Ordinal) ? "MODIS" : "VIIRS",
            Latitude: 50,
            Longitude: 30,
            ObservedAt,
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
            new Uri($"https://example.test/{id}")
        );
    }

    private static NotificationVisibilityOptions DefaultVisibility()
    {
        return new(
            Enabled: true,
            MinimumFrpMegawatts: 50,
            MinimumClusterTotalFrpMegawatts: 50,
            MinimumThermalContrastKelvin: 20,
            MinimumClusterDetections: 2,
            MinimumModisConfidencePercent: 60,
            NotificationViirsConfidenceLevel.Nominal,
            RequireDaytime: true,
            RequirePreview: true
        );
    }
}
