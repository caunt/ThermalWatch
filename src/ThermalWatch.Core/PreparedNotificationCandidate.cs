using System.Collections.Immutable;

namespace ThermalWatch.Core;

public sealed record PreparedNotificationCandidate(
    NotificationCluster Cluster,
    GibsPreview Preview,
    NotificationPreviewSelection PreviewSelection,
    string? LandCoverSummary,
    ImmutableArray<NearbyFeature> NearbyFeatures);
