using System.Runtime.InteropServices;

namespace ThermalWatch.Core;

/// <summary>The physical coverage of a centered notification imagery crop.</summary>
/// <param name="WidthKilometers">The full horizontal coverage.</param>
/// <param name="HeightKilometers">The full vertical coverage.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct NotificationPreviewSize(double WidthKilometers, double HeightKilometers);
