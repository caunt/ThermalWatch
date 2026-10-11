using System.Runtime.InteropServices;

namespace ThermalWatch.Core;

/// <summary>The physical coverage and pixel size of a GIBS image request.</summary>
/// <param name="WidthKilometers">The full horizontal coverage.</param>
/// <param name="HeightKilometers">The full vertical coverage.</param>
/// <param name="PixelWidth">The output width in pixels.</param>
/// <param name="PixelHeight">The output height in pixels.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct GibsPreviewDimensions(double WidthKilometers, double HeightKilometers, int PixelWidth, int PixelHeight);
