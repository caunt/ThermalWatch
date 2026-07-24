using ThermalWatch.Api;
using ThermalWatch.Core;

namespace ThermalWatch.Tests;

public sealed class NotificationOptionsTests
{
    public static TheoryData<string> SupportedEnvironmentNames =>
    [
        "NOTIFICATION_SEND_EXISTING_ON_STARTUP",
        "NOTIFICATION_CLUSTER_RADIUS_KM",
        "NOTIFICATION_CLUSTER_TIME_WINDOW",
        "NOTIFICATION_EPISODE_RETENTION",
        "NOTIFICATION_PREVIEW_WIDTH_KM",
        "NOTIFICATION_PREVIEW_HEIGHT_KM",
        "NOTIFICATION_LARGE_PREVIEW_WIDTH_KM",
        "NOTIFICATION_LARGE_PREVIEW_HEIGHT_KM",
        "NOTIFICATION_PREVIEW_PIXEL_WIDTH",
        "NOTIFICATION_PREVIEW_PIXEL_HEIGHT",
        "NOTIFICATION_LARGE_CLUSTER_MIN_DETECTIONS",
        "NOTIFICATION_LARGE_CLUSTER_MIN_FRP_MW",
        "NOTIFICATION_LARGE_CLUSTER_MIN_DIAMETER_KM",
        "NOTIFICATION_LAND_COVER_FILTER_ENABLED",
        "NOTIFICATION_VEGETATION_PERCENT_THRESHOLD",
        "NOTIFICATION_BUILT_UP_PROXIMITY_KM",
        "NOTIFICATION_VEGETATION_MAX_FRP_MW",
        "NOTIFICATION_KEEP_HIGH_FRP_VEGETATION",
        "NOTIFICATION_KEEP_MULTI_SATELLITE_VEGETATION",
        "NOTIFICATION_VISIBILITY_FILTER_ENABLED",
        "NOTIFICATION_MIN_FRP_MW",
        "NOTIFICATION_MIN_THERMAL_CONTRAST_K",
        "NOTIFICATION_MIN_CLUSTER_DETECTIONS",
        "NOTIFICATION_MIN_MODIS_CONFIDENCE_PERCENT",
        "NOTIFICATION_MIN_VIIRS_CONFIDENCE",
        "NOTIFICATION_REQUIRE_DAYTIME",
        "NOTIFICATION_REQUIRE_PREVIEW"
    ];

    [Fact]
    public void FromEnvironmentUsesExpandedPreviewCoverageDefaults()
    {
        NotificationOptions options = ApplicationConfiguration.ParseNotificationOptions(_ => null);

        Assert.Equal(
            new NotificationPreviewSize(WidthKilometers: 60, HeightKilometers: 40),
            options.Preview.PreviewSize);
        Assert.Equal(
            new NotificationPreviewSize(WidthKilometers: 90, HeightKilometers: 60),
            options.Preview.LargePreviewSize);
    }

    [Fact]
    public void FromEnvironmentUsesFourKPreviewPixelDefaults()
    {
        NotificationOptions options = ApplicationConfiguration.ParseNotificationOptions(_ => null);

        Assert.Equal(3840, options.Preview.PixelWidth);
        Assert.Equal(2560, options.Preview.PixelHeight);
    }

    [Fact]
    public void FromEnvironmentUsesStrictVegetationDefaults()
    {
        NotificationOptions options = ApplicationConfiguration.ParseNotificationOptions(_ => null);

        Assert.True(options.LandCover.Enabled);
        Assert.Equal(50, options.LandCover.VegetationPercentThreshold);
        Assert.Equal(2, options.LandCover.BuiltUpProximityKilometers);
        Assert.Equal(300, options.LandCover.VegetationMaximumFrpMegawatts);
        Assert.False(options.LandCover.KeepHighFrpVegetation);
        Assert.False(options.LandCover.KeepMultiSatelliteVegetation);
    }

    [Fact]
    public void FromEnvironmentReadsVegetationSpecificExceptionNames()
    {
        NotificationOptions options = ApplicationConfiguration.ParseNotificationOptions(name => name switch
        {
            "NOTIFICATION_KEEP_HIGH_FRP_VEGETATION" => "true",
            "NOTIFICATION_KEEP_MULTI_SATELLITE_VEGETATION" => "true",
            _ => null
        });

        Assert.True(options.LandCover.KeepHighFrpVegetation);
        Assert.True(options.LandCover.KeepMultiSatelliteVegetation);
    }

    [Fact]
    public void FromEnvironmentDoesNotUseReplacedMultiSatelliteName()
    {
        NotificationOptions options = ApplicationConfiguration.ParseNotificationOptions(name => name.Equals(value: "TELEGRAM_KEEP_MULTI_SATELLITE_CLUSTERS", StringComparison.Ordinal) ? "true" : null);

        Assert.False(options.LandCover.KeepMultiSatelliteVegetation);
    }

    [Theory]
    [MemberData(nameof(SupportedEnvironmentNames))]
    public void ParseNotificationOptionsRecognizesEveryProviderNeutralEnvironmentName(string name)
    {
        Assert.Throws<ApplicationConfigurationException>(() =>
            ApplicationConfiguration.ParseNotificationOptions(candidate =>
                candidate.Equals(name, StringComparison.Ordinal) ? "invalid" : null));
    }

    [Theory]
    [MemberData(nameof(SupportedEnvironmentNames))]
    public void ParseNotificationOptionsIgnoresEveryFormerTelegramPolicyName(string name)
    {
        string formerName = name switch
        {
            "NOTIFICATION_SEND_EXISTING_ON_STARTUP" => "TELEGRAM_NOTIFY_EXISTING_ON_STARTUP",
            "NOTIFICATION_EPISODE_RETENTION" => "TELEGRAM_SEEN_RETENTION",
            _ => name.Replace(
                oldValue: "NOTIFICATION_",
                newValue: "TELEGRAM_",
                StringComparison.Ordinal)
        };
        NotificationOptions expected = ApplicationConfiguration.ParseNotificationOptions(_ => null);

        NotificationOptions actual = ApplicationConfiguration.ParseNotificationOptions(candidate =>
            candidate.Equals(formerName, StringComparison.Ordinal) ? "invalid" : null);

        Assert.Equal(expected, actual);
    }
}
