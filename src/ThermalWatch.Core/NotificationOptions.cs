namespace ThermalWatch.Core;

public sealed record NotificationOptions(
    bool NotifyExistingOnStartup,
    double ClusterRadiusKilometers,
    TimeSpan ClusterTimeWindow,
    TimeSpan SeenRetention,
    TimeSpan PreviewRetryWindow,
    NotificationPreviewOptions Preview,
    NotificationLandCoverOptions LandCover,
    NotificationVisibilityOptions Visibility);
