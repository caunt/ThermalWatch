namespace ThermalWatch.Core;

public sealed record NotificationPreviewOptions(
    NotificationPreviewSize PreviewSize,
    NotificationPreviewSize LargePreviewSize,
    int PixelWidth,
    int PixelHeight,
    int LargeClusterMinimumDetections,
    double LargeClusterMinimumFrpMegawatts,
    double LargeClusterMinimumDiameterKilometers);
