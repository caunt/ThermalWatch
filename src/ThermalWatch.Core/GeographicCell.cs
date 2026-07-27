using System.Runtime.InteropServices;

namespace ThermalWatch.Core;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct GeographicCell(long X, long Y, long Z);
