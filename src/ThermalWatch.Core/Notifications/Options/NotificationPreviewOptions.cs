namespace ThermalWatch.Core;

/// <summary>The normal and large notification imagery crops and their selection thresholds.</summary>
/// <param name="PreviewSize">The normal crop's physical coverage.</param>
/// <param name="LargePreviewSize">The large crop's physical coverage.</param>
/// <param name="PixelWidth">The output image width in pixels.</param>
/// <param name="PixelHeight">The output image height in pixels.</param>
/// <param name="LargeClusterMinimumDetections">The member count that selects a large crop.</param>
/// <param name="LargeClusterMinimumFrpMegawatts">The representative FRP that selects a large crop.</param>
/// <param name="LargeClusterMinimumDiameterKilometers">The cluster diameter that selects a large crop.</param>
public sealed record NotificationPreviewOptions(
    NotificationPreviewSize PreviewSize,
    NotificationPreviewSize LargePreviewSize,
    int PixelWidth,
    int PixelHeight,
    int LargeClusterMinimumDetections,
    double LargeClusterMinimumFrpMegawatts,
    double LargeClusterMinimumDiameterKilometers
);
