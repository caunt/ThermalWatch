using System.Globalization;
using System.Runtime.InteropServices;

namespace ThermalWatch.Core;

/// <summary>A WGS84 rectangle expressed in degrees.</summary>
/// <param name="West">The western longitude.</param>
/// <param name="South">The southern latitude.</param>
/// <param name="East">The eastern longitude.</param>
/// <param name="North">The northern latitude.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct GeographicBounds(double West, double South, double East, double North)
{
    /// <summary>Formats west, south, east, and north for FIRMS and GIBS requests.</summary>
    public string ToInvariantString()
    {
        return string.Join(
        separator: ',',
        West.ToString(format: "0.######", CultureInfo.InvariantCulture),
        South.ToString(format: "0.######", CultureInfo.InvariantCulture),
        East.ToString(format: "0.######", CultureInfo.InvariantCulture),
        North.ToString(format: "0.######", CultureInfo.InvariantCulture)
    );
    }
}
