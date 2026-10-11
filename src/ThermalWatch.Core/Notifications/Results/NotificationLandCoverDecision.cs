namespace ThermalWatch.Core;

/// <summary>The land-cover policy outcome for a notification cluster.</summary>
public enum NotificationLandCoverDecision
{
    /// <summary>The available samples permit notification.</summary>
    Retained,
    /// <summary>The available samples suppress notification.</summary>
    Suppressed,
    /// <summary>The unavailable samples permit notification under the fail-open policy.</summary>
    Unavailable
}
