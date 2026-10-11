namespace ThermalWatch.Core;

/// <summary>The stable outcome strings exposed by notification diagnostics.</summary>
public static class NotificationCriterionOutcomes
{
    /// <summary>The criterion is disabled by configuration.</summary>
    public const string Disabled = "disabled";
    /// <summary>The evaluated value does not meet the requirement.</summary>
    public const string Failed = "failed";
    /// <summary>The criterion permits notification.</summary>
    public const string Passed = "passed";
    /// <summary>The required provider data could not be obtained.</summary>
    public const string Unavailable = "unavailable";
}
