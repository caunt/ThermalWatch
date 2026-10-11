namespace ThermalWatch.Core;

/// <summary>The clustering, content-selection, and delivery-lifecycle policy.</summary>
/// <param name="SendExistingOnStartup">Whether first-ready eligible incidents may be delivered.</param>
/// <param name="ClusterRadiusKilometers">The maximum distance for connected cluster members.</param>
/// <param name="ClusterTimeWindow">The maximum acquisition-time separation for connected members.</param>
/// <param name="EpisodeRetention">The retention interval for startup and delivered-episode suppression.</param>
/// <param name="HistoricalFrpFilterEnabled">Whether eligibility requires the historical location baseline.</param>
/// <param name="Preview">The notification imagery crop policy.</param>
/// <param name="LandCover">The vegetation suppression policy.</param>
/// <param name="Visibility">The observation-metadata selection policy.</param>
public sealed record NotificationOptions(
    bool SendExistingOnStartup,
    double ClusterRadiusKilometers,
    TimeSpan ClusterTimeWindow,
    TimeSpan EpisodeRetention,
    bool HistoricalFrpFilterEnabled,
    NotificationPreviewOptions Preview,
    NotificationLandCoverOptions LandCover,
    NotificationVisibilityOptions Visibility
);
