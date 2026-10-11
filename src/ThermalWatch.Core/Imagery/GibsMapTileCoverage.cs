namespace ThermalWatch.Core;

/// <summary>The usable imagery coverage of a composed map tile.</summary>
public enum GibsMapTileCoverage
{
    /// <summary>Every pixel has usable imagery.</summary>
    Complete,
    /// <summary>Some pixels remain transparent.</summary>
    Partial,
    /// <summary>No usable imagery was available.</summary>
    None
}
