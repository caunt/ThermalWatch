using System.Runtime.InteropServices;

namespace ThermalWatch.Core;

[StructLayout(LayoutKind.Auto)]
public readonly record struct NotificationPreviewSize(
    double WidthKilometers,
    double HeightKilometers);
