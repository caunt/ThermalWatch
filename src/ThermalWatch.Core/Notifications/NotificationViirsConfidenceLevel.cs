namespace ThermalWatch.Core;

/// <summary>The ordered confidence categories reported by VIIRS.</summary>
public enum NotificationViirsConfidenceLevel
{
    /// <summary>The lowest confidence category.</summary>
    Low,
    /// <summary>The nominal confidence category.</summary>
    Nominal,
    /// <summary>The highest confidence category.</summary>
    High
}
