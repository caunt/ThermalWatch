namespace ThermalWatch.Core;

public sealed record NotificationOptions(
    bool SendExistingOnStartup,
    double ClusterRadiusKilometers,
    TimeSpan ClusterTimeWindow,
    TimeSpan EpisodeRetention,
    NotificationPreviewOptions Preview,
    NotificationLandCoverOptions LandCover,
    NotificationVisibilityOptions Visibility);
