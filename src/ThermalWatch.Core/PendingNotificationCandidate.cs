namespace ThermalWatch.Core;

internal sealed record PendingNotificationCandidate(
    NotificationCluster Cluster,
    DateTimeOffset FirstSeenUtc,
    NotificationPreviewSelection PreviewSelection,
    string? LandCoverSummary);
