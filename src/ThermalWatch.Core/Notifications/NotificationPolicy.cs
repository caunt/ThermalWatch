using System.Collections.Immutable;
using System.Globalization;

namespace ThermalWatch.Core;

/// <summary>Evaluates notification metadata without changing the public anomaly snapshot.</summary>
public static class NotificationPolicy
{
    /// <summary>Returns the first blocking metadata criterion, preserving policy evaluation order.</summary>
    public static NotificationMetadataEvaluation EvaluateMetadata(NotificationCluster cluster, NotificationVisibilityOptions options)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
            return NotificationMetadataEvaluation.Eligible;

        NotificationMetadataEvaluation result = EvaluateDaytime(cluster.Representative, options);

        if (!result.IsEligible)
            return result;

        result = EvaluateDetectionCount(cluster, options);

        if (!result.IsEligible)
            return result;

        result = EvaluateConfidence(cluster.Representative, options);

        if (!result.IsEligible)
            return result;

        if (options.MinimumFrpMegawatts > 0)
        {
            result = EvaluateFrp(cluster.Representative, options);

            if (!result.IsEligible)
                return result;
        }

        if (options.MinimumClusterTotalFrpMegawatts > 0)
        {
            result = EvaluateClusterTotalFrp(cluster, options);

            if (!result.IsEligible)
                return result;
        }

        return options.MinimumThermalContrastKelvin > 0
            ? EvaluateThermalContrast(cluster.Representative, options)
            : NotificationMetadataEvaluation.Eligible;
    }

    /// <summary>Explains every metadata criterion, including criteria disabled by configuration.</summary>
    public static ImmutableArray<NotificationCriterionResult> ExplainMetadata(NotificationCluster cluster, NotificationVisibilityOptions options)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return
            [
                NotificationCriterionResult.Disabled(code: "daytime", label: "Daytime pass"),
                NotificationCriterionResult.Disabled(code: "cluster-detections", label: "Cluster detections"),
                NotificationCriterionResult.Disabled(code: "confidence", label: "Representative confidence"),
                NotificationCriterionResult.Disabled(code: "frp", label: "Representative FRP"),
                NotificationCriterionResult.Disabled(code: "cluster-total-frp", label: "Total cluster FRP"),
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
            ExplainClusterTotalFrp(cluster, options),
            ExplainThermalContrast(representative, options)
        ];
    }

    internal static string FormatNumber(double value)
    {
        return value.ToString(format: "0.##", CultureInfo.InvariantCulture);
    }

    private static NotificationCriterionResult Criterion(string code, string label, bool passed, string actualValue, string requirement, string explanation)
    {
        return new(
            code,
            label,
            passed ? NotificationCriterionOutcomes.Passed : NotificationCriterionOutcomes.Failed,
            actualValue,
            requirement,
            explanation,
            !passed
        );
    }

    private static NotificationMetadataEvaluation EvaluateClusterTotalFrp(NotificationCluster cluster, NotificationVisibilityOptions options)
    {
        return EvaluateMinimumRequiredValue(cluster.TotalFrpMegawatts, options.MinimumClusterTotalFrpMegawatts, NotificationRejectionReason.LowClusterTotalFrp);
    }

    private static NotificationMetadataEvaluation EvaluateConfidence(Anomaly anomaly, NotificationVisibilityOptions options)
    {
        if (anomaly.Source.Equals(value: "MODIS_NRT", StringComparison.Ordinal))
        {
            return options.MinimumModisConfidencePercent == 0
                ? NotificationMetadataEvaluation.Eligible
                : anomaly.ConfidencePercent is not { } modisConfidence
                ? NotificationMetadataEvaluation.Reject(NotificationRejectionReason.MissingRequiredValue)
                : modisConfidence >= options.MinimumModisConfidencePercent
                ? NotificationMetadataEvaluation.Eligible
                : NotificationMetadataEvaluation.Reject(NotificationRejectionReason.LowConfidence);
        }

        if (anomaly.ConfidenceCategory is not { } category)
            return NotificationMetadataEvaluation.Reject(NotificationRejectionReason.MissingRequiredValue);

        NotificationViirsConfidenceLevel? viirsConfidence = ParseViirsConfidence(category);

        return viirsConfidence is { } value && (int)value >= (int)options.MinimumViirsConfidence
                ? NotificationMetadataEvaluation.Eligible
                : NotificationMetadataEvaluation.Reject(NotificationRejectionReason.LowConfidence);
    }

    private static NotificationMetadataEvaluation EvaluateDaytime(Anomaly anomaly, NotificationVisibilityOptions options)
    {
        return !options.RequireDaytime || anomaly.DayNight.Equals(value: "D", StringComparison.Ordinal)
            ? NotificationMetadataEvaluation.Eligible
            : NotificationMetadataEvaluation.Reject(NotificationRejectionReason.Nighttime);
    }

    private static NotificationMetadataEvaluation EvaluateDetectionCount(NotificationCluster cluster, NotificationVisibilityOptions options)
    {
        return cluster.Members.Length >= options.MinimumClusterDetections
            ? NotificationMetadataEvaluation.Eligible
            : NotificationMetadataEvaluation.Reject(NotificationRejectionReason.InsufficientDetections);
    }

    private static NotificationMetadataEvaluation EvaluateFrp(Anomaly anomaly, NotificationVisibilityOptions options)
    {
        return EvaluateMinimumRequiredValue(anomaly.FrpMegawatts, options.MinimumFrpMegawatts, NotificationRejectionReason.LowFrp);
    }

    private static NotificationMetadataEvaluation EvaluateMinimumRequiredValue(double? actual, double minimum, NotificationRejectionReason lowValueReason)
    {
        return actual is not { } value
            ? NotificationMetadataEvaluation.Reject(NotificationRejectionReason.MissingRequiredValue)
            : value >= minimum
            ? NotificationMetadataEvaluation.Eligible
            : NotificationMetadataEvaluation.Reject(lowValueReason);
    }

    private static NotificationMetadataEvaluation EvaluateThermalContrast(Anomaly anomaly, NotificationVisibilityOptions options)
    {
        return EvaluateMinimumRequiredValue(anomaly.ThermalContrastKelvin, options.MinimumThermalContrastKelvin, NotificationRejectionReason.LowThermalContrast);
    }

    private static NotificationCriterionResult ExplainClusterTotalFrp(NotificationCluster cluster, NotificationVisibilityOptions options)
    {
        if (options.MinimumClusterTotalFrpMegawatts == 0)
            return NotificationCriterionResult.Disabled(code: "cluster-total-frp", label: "Total cluster FRP");

        bool passed = EvaluateClusterTotalFrp(cluster, options).IsEligible;

        return Criterion(
            code: "cluster-total-frp",
            label: "Total cluster FRP",
            passed,
            cluster.TotalFrpMegawatts is { } value
                ? $"{FormatNumber(value)} MW"
                : "Not available",
            $"At least {FormatNumber(options.MinimumClusterTotalFrpMegawatts)} MW",
            cluster.TotalFrpMegawatts is null
                ? "No cluster member contains an available FRP value."
                : passed
                    ? "The sum of available member FRP values meets the threshold."
                    : "The sum of available member FRP values is below the threshold."
        );
    }

    private static NotificationCriterionResult ExplainConfidence(Anomaly representative, NotificationVisibilityOptions options)
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
                representative.ConfidencePercent is { } confidencePercent
                    ? $"{FormatNumber(confidencePercent)}%"
                    : "Not available",
                $"At least {FormatNumber(options.MinimumModisConfidencePercent)}% (MODIS)",
                representative.ConfidencePercent is null
                    ? "The representative does not contain the required MODIS confidence value."
                    : passed
                        ? "The representative meets the MODIS confidence threshold."
                        : "The representative is below the MODIS confidence threshold."
            );
        }

        NotificationViirsConfidenceLevel? actualLevel = representative.ConfidenceCategory is { } category
            ? ParseViirsConfidence(category)
            : null;

        bool viirsPassed = EvaluateConfidence(representative, options).IsEligible;

        return Criterion(
            code: "confidence",
            label: "Representative confidence",
            viirsPassed,
            actualLevel?.ToString() ?? "Not available",
            $"At least {options.MinimumViirsConfidence} (VIIRS)",
            actualLevel is null
                ? "The representative does not contain a recognized VIIRS confidence category."
                : viirsPassed
                    ? "The representative meets the VIIRS confidence threshold."
                    : "The representative is below the VIIRS confidence threshold."
        );
    }

    private static NotificationCriterionResult ExplainDaytime(Anomaly representative, NotificationVisibilityOptions options)
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
            passed
                ? "The representative anomaly is from a daytime pass."
                : "The representative anomaly is not from a daytime pass."
        );
    }

    private static NotificationCriterionResult ExplainDetectionCount(NotificationCluster cluster, NotificationVisibilityOptions options)
    {
        bool passed = EvaluateDetectionCount(cluster, options).IsEligible;

        return Criterion(
            code: "cluster-detections",
            label: "Cluster detections",
            passed,
            cluster.Members.Length.ToString(CultureInfo.InvariantCulture),
            $"At least {options.MinimumClusterDetections.ToString(CultureInfo.InvariantCulture)}",
            passed
                ? "The cluster contains enough detections."
                : "The cluster contains too few detections."
        );
    }

    private static NotificationCriterionResult ExplainFrp(Anomaly representative, NotificationVisibilityOptions options)
    {
        if (options.MinimumFrpMegawatts == 0)
            return NotificationCriterionResult.Disabled(code: "frp", label: "Representative FRP");

        bool passed = EvaluateFrp(representative, options).IsEligible;

        return Criterion(
            code: "frp",
            label: "Representative FRP",
            passed,
            representative.FrpMegawatts is { } value
                ? $"{FormatNumber(value)} MW"
                : "Not available",
            $"At least {FormatNumber(options.MinimumFrpMegawatts)} MW",
            representative.FrpMegawatts is null
                ? "The representative does not contain the required FRP value."
                : passed
                    ? "The representative meets the FRP threshold."
                    : "The representative is below the FRP threshold."
        );
    }

    private static NotificationCriterionResult ExplainThermalContrast(Anomaly representative, NotificationVisibilityOptions options)
    {
        if (options.MinimumThermalContrastKelvin == 0)
            return NotificationCriterionResult.Disabled(code: "thermal-contrast", label: "Thermal contrast");

        bool passed = EvaluateThermalContrast(representative, options).IsEligible;

        return Criterion(
            code: "thermal-contrast",
            label: "Thermal contrast",
            passed,
            representative.ThermalContrastKelvin is { } value
                ? $"{FormatNumber(value)} K"
                : "Not available",
            $"At least {FormatNumber(options.MinimumThermalContrastKelvin)} K",
            representative.ThermalContrastKelvin is null
                ? "The representative does not contain the values required to calculate thermal contrast."
                : passed
                    ? "The representative meets the thermal-contrast threshold."
                    : "The representative is below the thermal-contrast threshold."
        );
    }

    private static NotificationViirsConfidenceLevel? ParseViirsConfidence(string category)
    {
        return category.ToUpperInvariant() switch
        {
            "L" or "LOW" => NotificationViirsConfidenceLevel.Low,
            "N" or "NOMINAL" => NotificationViirsConfidenceLevel.Nominal,
            "H" or "HIGH" => NotificationViirsConfidenceLevel.High,
            _ => null
        };
    }
}
