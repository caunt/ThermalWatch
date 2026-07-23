namespace ThermalWatch.Core;

internal readonly record struct NotificationCandidatePreparation(
    NotificationCluster Cluster,
    DateTimeOffset FirstSeenUtc,
    bool ContinuesDeliveredEpisode);
