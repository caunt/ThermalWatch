using System.Runtime.InteropServices;

namespace ThermalWatch.Core;

/// <summary>The crop selected for a cluster and the diameter used in that decision.</summary>
/// <param name="Dimensions">The selected physical and pixel dimensions.</param>
/// <param name="ClusterDiameterKilometers">The greatest pairwise member distance.</param>
/// <param name="IsLargePreview">Whether a configured large-crop threshold was met.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct NotificationPreviewSelection(GibsPreviewDimensions Dimensions, double ClusterDiameterKilometers, bool IsLargePreview);
