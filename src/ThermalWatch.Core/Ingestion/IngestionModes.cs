namespace ThermalWatch.Core;

/// <summary>The stable acquisition-mode strings exposed by segment status.</summary>
public static class IngestionModes
{
    /// <summary>The complete country segment was acquired through verified area fallback.</summary>
    public const string AreaFallback = "areaFallback";
    /// <summary>The complete country segment was acquired through the country endpoint.</summary>
    public const string Country = "country";
    /// <summary>No successful acquisition has occurred.</summary>
    public const string None = "none";
}
