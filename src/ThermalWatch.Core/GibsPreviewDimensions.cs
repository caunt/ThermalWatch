using System.Runtime.InteropServices;

namespace ThermalWatch.Core;

[StructLayout(LayoutKind.Auto)]
public readonly record struct GibsPreviewDimensions(
    double WidthKilometers,
    double HeightKilometers,
    int PixelWidth,
    int PixelHeight);
