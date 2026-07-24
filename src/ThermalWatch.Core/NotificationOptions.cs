namespace ThermalWatch.Core;

public sealed record NotificationOptions(
    bool NotifyExistingOnStartup,
    double ClusterRadiusKilometers,
    TimeSpan ClusterTimeWindow,
    TimeSpan DeliveredRetention,
    NotificationPreviewOptions Preview,
    NotificationLandCoverOptions LandCover,
    NotificationVisibilityOptions Visibility);
