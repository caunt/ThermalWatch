using System.Runtime.InteropServices;

namespace ThermalWatch.Core;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct TrueTypeTextMask(byte[] Pixels, int Width, int Height);
