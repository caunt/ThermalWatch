using System.Globalization;
using System.Runtime.InteropServices;

namespace ThermalWatch.Core;

[StructLayout(LayoutKind.Auto)]
public readonly record struct GeographicBounds(double West, double South, double East, double North)
{
    public string ToInvariantString() => string.Join(',',
        West.ToString(format: "0.######", CultureInfo.InvariantCulture),
        South.ToString(format: "0.######", CultureInfo.InvariantCulture),
        East.ToString(format: "0.######", CultureInfo.InvariantCulture),
        North.ToString(format: "0.######", CultureInfo.InvariantCulture));
}
