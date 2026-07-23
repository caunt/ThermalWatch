using ThermalWatch.Core;

namespace ThermalWatch.Telegram;

internal readonly record struct TelegramCandidatePreparation(
    NotificationCluster Cluster,
    DateTimeOffset FirstSeenUtc,
    bool ContinuesDeliveredEpisode);
