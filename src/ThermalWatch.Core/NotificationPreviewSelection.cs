using System.Runtime.InteropServices;

namespace ThermalWatch.Core;

[StructLayout(LayoutKind.Auto)]
public readonly record struct NotificationPreviewSelection(
    GibsPreviewDimensions Dimensions,
    double ClusterDiameterKilometers,
    bool IsLargePreview);
