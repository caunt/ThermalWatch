using System.Collections.Immutable;

namespace ThermalWatch.Core;

/// <summary>A policy-approved cluster with the context needed by the delivery transport.</summary>
/// <param name="Cluster">The selected observation cluster.</param>
/// <param name="Preview">The preview payload or unavailable result.</param>
/// <param name="PreviewSelection">The selected preview size and coverage.</param>
/// <param name="LandCoverSummary">The optional land-cover explanation.</param>
/// <param name="NearbyFeatures">The mapped features available for presentation.</param>
public sealed record PreparedNotificationCandidate(
    NotificationCluster Cluster,
    GibsPreview Preview,
    NotificationPreviewSelection PreviewSelection,
    string? LandCoverSummary,
    ImmutableArray<NearbyFeature> NearbyFeatures
)
{
    /// <summary>The available containing settlement name.</summary>
    public string? SettlementName { get; init; }
}
