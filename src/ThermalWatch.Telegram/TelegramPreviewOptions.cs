namespace ThermalWatch.Telegram;

public sealed record TelegramPreviewOptions(
    TelegramPreviewSize PreviewSize,
    TelegramPreviewSize LargePreviewSize,
    int PixelWidth,
    int PixelHeight,
    int LargeClusterMinimumDetections,
    double LargeClusterMinimumFrpMegawatts,
    double LargeClusterMinimumDiameterKilometers);
