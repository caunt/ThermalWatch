using System.Runtime.InteropServices;
using ThermalWatch.Core;

namespace ThermalWatch.Telegram;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct TelegramPreviewSelection(
    GibsPreviewDimensions Dimensions,
    double ClusterDiameterKilometers);
