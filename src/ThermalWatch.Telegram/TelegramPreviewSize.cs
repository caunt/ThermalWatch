using System.Runtime.InteropServices;

namespace ThermalWatch.Telegram;

[StructLayout(LayoutKind.Auto)]
public readonly record struct TelegramPreviewSize(
    double WidthKilometers,
    double HeightKilometers);
