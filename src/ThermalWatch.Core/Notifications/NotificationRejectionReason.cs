namespace ThermalWatch.Core;

/// <summary>The first blocking reason encountered while selecting a notification candidate.</summary>
public enum NotificationRejectionReason
{
    /// <summary>The representative was acquired at night when nighttime notifications are disabled.</summary>
    Nighttime,
    /// <summary>The cluster contains fewer detections than required.</summary>
    InsufficientDetections,
    /// <summary>The representative's confidence is below the configured minimum.</summary>
    LowConfidence,
    /// <summary>The representative's FRP is below the configured minimum.</summary>
    LowFrp,
    /// <summary>The cluster's total available FRP is below the configured minimum.</summary>
    LowClusterTotalFrp,
    /// <summary>The thermal contrast is below the configured minimum.</summary>
    LowThermalContrast,
    /// <summary>The cluster's FRP does not exceed the historical thresholds.</summary>
    HistoricalFrpNotHigher,
    /// <summary>The required complete historical baseline is unavailable.</summary>
    HistoryUnavailable,
    /// <summary>A measurement required by an enabled filter is unavailable.</summary>
    MissingRequiredValue,
    /// <summary>The required exact-date preview could not be obtained.</summary>
    PreviewUnavailable
}
