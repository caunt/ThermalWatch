using System.Collections.Immutable;
using System.Globalization;

namespace ThermalWatch.Core;

public static class NotificationPolicy
{
    public static NotificationMetadataEvaluation EvaluateMetadata(
        NotificationCluster cluster,
        NotificationVisibilityOptions options)
    {
        if (!options.Enabled)
            return NotificationMetadataEvaluation.Eligible;

        if (EvaluateDaytime(cluster.Representative, options) is { IsEligible: false } daytimeResult)
            return daytimeResult;

        if (EvaluateDetectionCount(cluster, options) is { IsEligible: false } detectionCountResult)
            return detectionCountResult;

        if (EvaluateConfidence(cluster.Representative, options) is { IsEligible: false } confidenceResult)
            return confidenceResult;

        if (options.MinimumFrpMegawatts > 0
            && EvaluateFrp(cluster.Representative, options) is { IsEligible: false } frpResult)
        {
            return frpResult;
        }

        if (options.MinimumThermalContrastKelvin > 0
            && EvaluateThermalContrast(cluster.Representative, options) is { IsEligible: false } thermalContrastResult)
        {
            return thermalContrastResult;
        }

        return NotificationMetadataEvaluation.Eligible;
    }

    public static ImmutableArray<NotificationCriterionResult> ExplainMetadata(
        NotificationCluster cluster,
        NotificationVisibilityOptions options)
    {
        if (!options.Enabled)
        {
            return
            [
                NotificationCriterionResult.Disabled(code: "daytime", label: "Daytime pass"),
                NotificationCriterionResult.Disabled(code: "cluster-detections", label: "Cluster detections"),
                NotificationCriterionResult.Disabled(code: "confidence", label: "Representative confidence"),
                NotificationCriterionResult.Disabled(code: "frp", label: "Representative FRP"),
                NotificationCriterionResult.Disabled(code: "thermal-contrast", label: "Thermal contrast")
            ];
        }

        Anomaly representative = cluster.Representative;
        return
        [
            ExplainDaytime(representative, options),
            ExplainDetectionCount(cluster, options),
            ExplainConfidence(representative, options),
            ExplainFrp(representative, options),
            ExplainThermalContrast(representative, options)
        ];
    }

    private static NotificationMetadataEvaluation EvaluateConfidence(
        Anomaly anomaly,
        NotificationVisibilityOptions options)
    {
        if (anomaly.Source.Equals(value: "MODIS_NRT", StringComparison.Ordinal))
        {
            if (options.MinimumModisConfidencePercent == 0)
                return NotificationMetadataEvaluation.Eligible;

            if (anomaly.ConfidencePercent is not { } modisConfidence)
                return NotificationMetadataEvaluation.Reject(NotificationRejectionReason.MissingRequiredValue);

            return modisConfidence >= options.MinimumModisConfidencePercent
                ? NotificationMetadataEvaluation.Eligible
                : NotificationMetadataEvaluation.Reject(NotificationRejectionReason.LowConfidence);
        }

        if (anomaly.ConfidenceCategory is not { } category)
            return NotificationMetadataEvaluation.Reject(NotificationRejectionReason.MissingRequiredValue);

        NotificationViirsConfidenceLevel? viirsConfidence = ParseViirsConfidence(category);
        return viirsConfidence is { } value
            && (int)value >= (int)options.MinimumViirsConfidence
                ? NotificationMetadataEvaluation.Eligible
                : NotificationMetadataEvaluation.Reject(NotificationRejectionReason.LowConfidence);
    }

    private static NotificationMetadataEvaluation EvaluateDaytime(
        Anomaly anomaly,
        NotificationVisibilityOptions options) =>
        !options.RequireDaytime || anomaly.DayNight.Equals(value: "D", StringComparison.Ordinal)
            ? NotificationMetadataEvaluation.Eligible
            : NotificationMetadataEvaluation.Reject(NotificationRejectionReason.Nighttime);

    private static NotificationMetadataEvaluation EvaluateDetectionCount(
        NotificationCluster cluster,
        NotificationVisibilityOptions options) =>
        cluster.Members.Length >= options.MinimumClusterDetections
            ? NotificationMetadataEvaluation.Eligible
            : NotificationMetadataEvaluation.Reject(NotificationRejectionReason.InsufficientDetections);

    private static NotificationMetadataEvaluation EvaluateFrp(
        Anomaly anomaly,
        NotificationVisibilityOptions options) =>
        EvaluateMinimumRequiredValue(
            anomaly.FrpMegawatts,
            options.MinimumFrpMegawatts,
            NotificationRejectionReason.LowFrp);

    private static NotificationMetadataEvaluation EvaluateThermalContrast(
        Anomaly anomaly,
        NotificationVisibilityOptions options) =>
        EvaluateMinimumRequiredValue(
            anomaly.ThermalContrastKelvin,
            options.MinimumThermalContrastKelvin,
            NotificationRejectionReason.LowThermalContrast);

    private static NotificationMetadataEvaluation EvaluateMinimumRequiredValue(
        double? actual,
        double minimum,
        NotificationRejectionReason lowValueReason)
    {
        if (actual is not { } value)
            return NotificationMetadataEvaluation.Reject(NotificationRejectionReason.MissingRequiredValue);

        return value >= minimum
            ? NotificationMetadataEvaluation.Eligible
            : NotificationMetadataEvaluation.Reject(lowValueReason);
    }

    private static NotificationCriterionResult ExplainDaytime(
        Anomaly representative,
        NotificationVisibilityOptions options)
    {
        if (!options.RequireDaytime)
            return NotificationCriterionResult.Disabled(code: "daytime", label: "Daytime pass");

        bool passed = EvaluateDaytime(representative, options).IsEligible;
        string actual = representative.DayNight switch
        {
            "D" => "Daytime",
            "N" => "Nighttime",
            _ => "Not available"
        };
        return Criterion(
            code: "daytime",
            label: "Daytime pass",
            passed,
            actual,
            requirement: "Daytime",
            explanation: passed
                ? "The representative anomaly is from a daytime pass."
                : "The representative anomaly is not from a daytime pass.");
    }

    private static NotificationCriterionResult ExplainDetectionCount(
        NotificationCluster cluster,
        NotificationVisibilityOptions options)
    {
        bool passed = EvaluateDetectionCount(cluster, options).IsEligible;
        return Criterion(
            code: "cluster-detections",
            label: "Cluster detections",
            passed,
            actualValue: cluster.Members.Length.ToString(CultureInfo.InvariantCulture),
            requirement: $"At least {options.MinimumClusterDetections.ToString(CultureInfo.InvariantCulture)}",
            explanation: passed
                ? "The cluster contains enough detections."
                : "The cluster contains too few detections.");
    }

    private static NotificationCriterionResult ExplainConfidence(
        Anomaly representative,
        NotificationVisibilityOptions options)
    {
        if (representative.Source.Equals(value: "MODIS_NRT", StringComparison.Ordinal))
        {
            if (options.MinimumModisConfidencePercent == 0)
                return NotificationCriterionResult.Disabled(code: "confidence", label: "Representative confidence");

            bool passed = EvaluateConfidence(representative, options).IsEligible;
            return Criterion(
                code: "confidence",
                label: "Representative confidence",
                passed,
                actualValue: representative.ConfidencePercent is { } confidencePercent
                    ? $"{FormatNumber(confidencePercent)}%"
                    : "Not available",
                requirement: $"At least {FormatNumber(options.MinimumModisConfidencePercent)}% (MODIS)",
                explanation: representative.ConfidencePercent is null
                    ? "The representative does not contain the required MODIS confidence value."
                    : passed
                        ? "The representative meets the MODIS confidence threshold."
                        : "The representative is below the MODIS confidence threshold.");
        }

        NotificationViirsConfidenceLevel? actualLevel = representative.ConfidenceCategory is { } category
            ? ParseViirsConfidence(category)
            : null;
        bool viirsPassed = EvaluateConfidence(representative, options).IsEligible;
        return Criterion(
            code: "confidence",
            label: "Representative confidence",
            viirsPassed,
            actualValue: actualLevel?.ToString() ?? "Not available",
            requirement: $"At least {options.MinimumViirsConfidence} (VIIRS)",
            explanation: actualLevel is null
                ? "The representative does not contain a recognized VIIRS confidence category."
                : viirsPassed
                    ? "The representative meets the VIIRS confidence threshold."
                    : "The representative is below the VIIRS confidence threshold.");
    }

    private static NotificationCriterionResult ExplainFrp(
        Anomaly representative,
        NotificationVisibilityOptions options)
    {
        if (options.MinimumFrpMegawatts == 0)
            return NotificationCriterionResult.Disabled(code: "frp", label: "Representative FRP");

        bool passed = EvaluateFrp(representative, options).IsEligible;
        return Criterion(
            code: "frp",
            label: "Representative FRP",
            passed,
            actualValue: representative.FrpMegawatts is { } value
                ? $"{FormatNumber(value)} MW"
                : "Not available",
            requirement: $"At least {FormatNumber(options.MinimumFrpMegawatts)} MW",
            explanation: representative.FrpMegawatts is null
                ? "The representative does not contain the required FRP value."
                : passed
                    ? "The representative meets the FRP threshold."
                    : "The representative is below the FRP threshold.");
    }

    private static NotificationCriterionResult ExplainThermalContrast(
        Anomaly representative,
        NotificationVisibilityOptions options)
    {
        if (options.MinimumThermalContrastKelvin == 0)
            return NotificationCriterionResult.Disabled(code: "thermal-contrast", label: "Thermal contrast");

        bool passed = EvaluateThermalContrast(representative, options).IsEligible;
        return Criterion(
            code: "thermal-contrast",
            label: "Thermal contrast",
            passed,
            actualValue: representative.ThermalContrastKelvin is { } value
                ? $"{FormatNumber(value)} K"
                : "Not available",
            requirement: $"At least {FormatNumber(options.MinimumThermalContrastKelvin)} K",
            explanation: representative.ThermalContrastKelvin is null
                ? "The representative does not contain the values required to calculate thermal contrast."
                : passed
                    ? "The representative meets the thermal-contrast threshold."
                    : "The representative is below the thermal-contrast threshold.");
    }

    private static NotificationCriterionResult Criterion(
        string code,
        string label,
        bool passed,
        string actualValue,
        string requirement,
        string explanation) =>
        new(
            code,
            label,
            passed ? NotificationCriterionOutcomes.Passed : NotificationCriterionOutcomes.Failed,
            actualValue,
            requirement,
            explanation,
            IsBlocking: !passed);

    private static NotificationViirsConfidenceLevel? ParseViirsConfidence(string category) =>
        category.ToLowerInvariant() switch
        {
            "l" or "low" => NotificationViirsConfidenceLevel.Low,
            "n" or "nominal" => NotificationViirsConfidenceLevel.Nominal,
            "h" or "high" => NotificationViirsConfidenceLevel.High,
            _ => (NotificationViirsConfidenceLevel?)null
        };

    internal static string FormatNumber(double value) =>
        value.ToString(format: "0.##", CultureInfo.InvariantCulture);
}
