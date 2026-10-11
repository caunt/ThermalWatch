using System.Runtime.InteropServices;

namespace ThermalWatch.Core;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct GeographicCell(long Horizontal, long Vertical, long Depth);
