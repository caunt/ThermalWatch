namespace ThermalWatch.Core;

/// <summary>The transport outcome used to update delivery history and processing lifecycle.</summary>
public enum NotificationDeliveryOutcome
{
    /// <summary>The candidate was successfully delivered.</summary>
    Delivered,
    /// <summary>Delivery failed temporarily and may be attempted after another publication.</summary>
    RetryLater,
    /// <summary>The transport requires notification processing to stop.</summary>
    Stop
}
