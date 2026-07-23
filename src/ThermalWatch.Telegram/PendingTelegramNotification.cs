using ThermalWatch.Core;

namespace ThermalWatch.Telegram;

internal sealed record PendingTelegramNotification(
    NotificationCluster Cluster,
    DateTimeOffset FirstSeenUtc,
    TelegramPreviewSelection PreviewSelection,
    string? LandCoverSummary);
