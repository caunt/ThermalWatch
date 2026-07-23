using ThermalWatch.Core;

namespace ThermalWatch.Telegram;

internal static class TelegramVisibilityFilter
{
    public static VisibilityFilterResult EvaluateMetadata(
        NotificationCluster cluster,
        TelegramVisibilityOptions options)
    {
        if (!options.Enabled)
            return VisibilityFilterResult.Accepted;

        Anomaly representative = cluster.Representative;

        if (options.RequireDaytime && !representative.DayNight.Equals(value: "D", StringComparison.Ordinal))
            return VisibilityFilterResult.Reject(VisibilityRejectionReason.Nighttime);

        if (cluster.Members.Length < options.MinimumClusterDetections)
            return VisibilityFilterResult.Reject(VisibilityRejectionReason.InsufficientDetections);

        if (EvaluateConfidence(representative, options) is { IsAccepted: false } confidenceResult)
            return confidenceResult;

        if (options.MinimumFrpMegawatts > 0)
        {
            if (representative.FrpMegawatts is not { } frp)
                return VisibilityFilterResult.Reject(VisibilityRejectionReason.MissingRequiredValue);

            if (frp < options.MinimumFrpMegawatts)
                return VisibilityFilterResult.Reject(VisibilityRejectionReason.LowFrp);
        }

        if (options.MinimumThermalContrastKelvin > 0)
        {
            if (representative.ThermalContrastKelvin is not { } thermalContrast)
                return VisibilityFilterResult.Reject(VisibilityRejectionReason.MissingRequiredValue);

            if (thermalContrast < options.MinimumThermalContrastKelvin)
                return VisibilityFilterResult.Reject(VisibilityRejectionReason.LowThermalContrast);
        }

        return VisibilityFilterResult.Accepted;
    }

    private static VisibilityFilterResult EvaluateConfidence(
        Anomaly anomaly,
        TelegramVisibilityOptions options)
    {
        if (anomaly.Source.Equals(value: "MODIS_NRT", StringComparison.Ordinal))
        {
            if (options.MinimumModisConfidencePercent == 0)
                return VisibilityFilterResult.Accepted;

            if (anomaly.ConfidencePercent is not { } modisConfidence)
                return VisibilityFilterResult.Reject(VisibilityRejectionReason.MissingRequiredValue);

            return modisConfidence >= options.MinimumModisConfidencePercent
                ? VisibilityFilterResult.Accepted
                : VisibilityFilterResult.Reject(VisibilityRejectionReason.LowConfidence);
        }

        if (anomaly.ConfidenceCategory is not { } category)
            return VisibilityFilterResult.Reject(VisibilityRejectionReason.MissingRequiredValue);

        ViirsConfidenceLevel? viirsConfidence = category.ToLowerInvariant() switch
        {
            "l" or "low" => ViirsConfidenceLevel.Low,
            "n" or "nominal" => ViirsConfidenceLevel.Nominal,
            "h" or "high" => ViirsConfidenceLevel.High,
            _ => (ViirsConfidenceLevel?)null
        };

        return viirsConfidence is { } value
            && (int)value >= (int)options.MinimumViirsConfidence
            ? VisibilityFilterResult.Accepted
            : VisibilityFilterResult.Reject(VisibilityRejectionReason.LowConfidence);
    }
}
