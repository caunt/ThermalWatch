using ThermalWatch.Api;
using ThermalWatch.Core;

namespace ThermalWatch.Tests.Core.Notifications;

/// <summary>Verifies provider-neutral notification settings and defaults.</summary>
public sealed class NotificationOptionsTests
{
    /// <summary>Provides zero and fractional FRP configuration values.</summary>
    public static TheoryData<string, double> MinimumFrpCases => new()
    {
        { "0", 0 },
        { "125.5", 125.5 }
    };

    /// <summary>Lists the supported notification environment variables.</summary>
    public static TheoryData<string> SupportedEnvironmentNames =>
    [
        "NOTIFICATION_SEND_EXISTING_ON_STARTUP",
        "NOTIFICATION_CLUSTER_RADIUS_KM",
        "NOTIFICATION_CLUSTER_TIME_WINDOW",
        "NOTIFICATION_EPISODE_RETENTION",
        "NOTIFICATION_HISTORICAL_FRP_FILTER_ENABLED",
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
        "NOTIFICATION_MIN_CLUSTER_TOTAL_FRP_MW",
        "NOTIFICATION_MIN_THERMAL_CONTRAST_K",
        "NOTIFICATION_MIN_CLUSTER_DETECTIONS",
        "NOTIFICATION_MIN_MODIS_CONFIDENCE_PERCENT",
        "NOTIFICATION_MIN_VIIRS_CONFIDENCE",
        "NOTIFICATION_REQUIRE_DAYTIME",
        "NOTIFICATION_REQUIRE_PREVIEW"
    ];

    /// <summary>Allows nighttime observations when the daytime requirement is disabled.</summary>
    [Fact]
    public void FromEnvironmentCanAllowNighttime()
    {
        NotificationOptions options = ApplicationConfiguration.ParseNotificationOptions(name => name.Equals(value: "NOTIFICATION_REQUIRE_DAYTIME", StringComparison.Ordinal) ? "false" : null);

        Assert.False(options.Visibility.RequireDaytime);
    }

    /// <summary>Allows explicit disabling of historical FRP filtering.</summary>
    [Fact]
    public void FromEnvironmentCanDisableHistoricalFrpFilter()
    {
        NotificationOptions options = ApplicationConfiguration.ParseNotificationOptions(name => name.Equals(value: "NOTIFICATION_HISTORICAL_FRP_FILTER_ENABLED", StringComparison.Ordinal) ? "false" : null);

        Assert.False(options.HistoricalFrpFilterEnabled);
    }

    /// <summary>Allows explicit disabling of land-cover filtering.</summary>
    [Fact]
    public void FromEnvironmentCanDisableLandCoverFilter()
    {
        NotificationOptions options = ApplicationConfiguration.ParseNotificationOptions(name => name.Equals(value: "NOTIFICATION_LAND_COVER_FILTER_ENABLED", StringComparison.Ordinal) ? "false" : null);

        Assert.False(options.LandCover.Enabled);
    }

    /// <summary>Defaults to cluster total FRP without a representative FRP minimum.</summary>
    [Fact]
    public void FromEnvironmentDisablesRepresentativeFrpAndUsesOneHundredMegawattClusterTotalDefault()
    {
        NotificationOptions options = ApplicationConfiguration.ParseNotificationOptions(environmentName => null);

        Assert.Equal(expected: 0, options.Visibility.MinimumFrpMegawatts);
        Assert.Equal(expected: 100, options.Visibility.MinimumClusterTotalFrpMegawatts);
    }

    /// <summary>Ignores the retired Telegram multi-satellite setting.</summary>
    [Fact]
    public void FromEnvironmentDoesNotUseReplacedMultiSatelliteName()
    {
        NotificationOptions options = ApplicationConfiguration.ParseNotificationOptions(name => name.Equals(value: "TELEGRAM_KEEP_MULTI_SATELLITE_CLUSTERS", StringComparison.Ordinal) ? "true" : null);

        Assert.False(options.LandCover.KeepMultiSatelliteVegetation);
    }

    /// <summary>Requires daytime observations by default.</summary>
    [Fact]
    public void FromEnvironmentEnablesDaytimeRequirementByDefault()
    {
        NotificationOptions options = ApplicationConfiguration.ParseNotificationOptions(environmentName => null);

        Assert.True(options.Visibility.RequireDaytime);
    }

    /// <summary>Enables historical FRP filtering by default.</summary>
    [Fact]
    public void FromEnvironmentEnablesHistoricalFrpFilterByDefault()
    {
        NotificationOptions options = ApplicationConfiguration.ParseNotificationOptions(environmentName => null);

        Assert.True(options.HistoricalFrpFilterEnabled);
    }

    /// <summary>Uses strict vegetation filtering defaults without optional exceptions.</summary>
    [Fact]
    public void FromEnvironmentEnablesLandCoverFilterAndUsesStrictVegetationDefaults()
    {
        NotificationOptions options = ApplicationConfiguration.ParseNotificationOptions(environmentName => null);

        Assert.True(options.LandCover.Enabled);
        Assert.Equal(expected: 50, options.LandCover.VegetationPercentThreshold);
        Assert.Equal(expected: 15, options.LandCover.BuiltUpProximityKilometers);
        Assert.Equal(expected: 300, options.LandCover.VegetationMaximumFrpMegawatts);
        Assert.False(options.LandCover.KeepHighFrpVegetation);
        Assert.False(options.LandCover.KeepMultiSatelliteVegetation);
    }

    /// <summary>Reads the configured minimum cluster FRP total.</summary>
    [Theory]
    [MemberData(nameof(MinimumFrpCases))]
    public void FromEnvironmentReadsMinimumClusterTotalFrp(string configured, double expected)
    {
        NotificationOptions options = ApplicationConfiguration.ParseNotificationOptions(name => name.Equals(value: "NOTIFICATION_MIN_CLUSTER_TOTAL_FRP_MW", StringComparison.Ordinal) ? configured : null);

        Assert.Equal(expected, options.Visibility.MinimumClusterTotalFrpMegawatts);
    }

    /// <summary>Reads the configured minimum representative FRP.</summary>
    [Theory]
    [MemberData(nameof(MinimumFrpCases))]
    public void FromEnvironmentReadsMinimumRepresentativeFrp(string configured, double expected)
    {
        NotificationOptions options = ApplicationConfiguration.ParseNotificationOptions(name => name.Equals(value: "NOTIFICATION_MIN_FRP_MW", StringComparison.Ordinal) ? configured : null);

        Assert.Equal(expected, options.Visibility.MinimumFrpMegawatts);
    }

    /// <summary>Reads the vegetation-specific exception settings.</summary>
    [Fact]
    public void FromEnvironmentReadsVegetationSpecificExceptionNames()
    {
        NotificationOptions options = ApplicationConfiguration.ParseNotificationOptions(
            name => name switch
        {
            "NOTIFICATION_KEEP_HIGH_FRP_VEGETATION" => "true",
            "NOTIFICATION_KEEP_MULTI_SATELLITE_VEGETATION" => "true",
            _ => null
        }
        );

        Assert.True(options.LandCover.KeepHighFrpVegetation);
        Assert.True(options.LandCover.KeepMultiSatelliteVegetation);
    }

    /// <summary>Uses portrait four-kilopixel imagery dimensions by default.</summary>
    [Fact]
    public void FromEnvironmentUsesPortraitFourKPreviewPixelDefaults()
    {
        NotificationOptions options = ApplicationConfiguration.ParseNotificationOptions(environmentName => null);

        Assert.Equal(expected: 3072, options.Preview.PixelWidth);
        Assert.Equal(expected: 3840, options.Preview.PixelHeight);
    }

    /// <summary>Uses portrait ground coverage for normal and large previews.</summary>
    [Fact]
    public void FromEnvironmentUsesPortraitPreviewCoverageDefaults()
    {
        NotificationOptions options = ApplicationConfiguration.ParseNotificationOptions(environmentName => null);

        Assert.Equal(new NotificationPreviewSize(WidthKilometers: 48, HeightKilometers: 60), options.Preview.PreviewSize);
        Assert.Equal(new NotificationPreviewSize(WidthKilometers: 72, HeightKilometers: 90), options.Preview.LargePreviewSize);
    }

    /// <summary>Ignores each retired Telegram policy environment variable.</summary>
    [Theory]
    [MemberData(nameof(SupportedEnvironmentNames))]
    public void ParseNotificationOptionsIgnoresEveryFormerTelegramPolicyName(string name)
    {
        string formerName = name switch
        {
            "NOTIFICATION_SEND_EXISTING_ON_STARTUP" => "TELEGRAM_NOTIFY_EXISTING_ON_STARTUP",
            "NOTIFICATION_EPISODE_RETENTION" => "TELEGRAM_SEEN_RETENTION",
            _ => name.Replace(oldValue: "NOTIFICATION_", newValue: "TELEGRAM_", StringComparison.Ordinal)
        };

        NotificationOptions expected = ApplicationConfiguration.ParseNotificationOptions(environmentName => null);

        NotificationOptions actual = ApplicationConfiguration.ParseNotificationOptions(candidate => candidate.Equals(formerName, StringComparison.Ordinal) ? "invalid" : null);

        Assert.Equal(expected, actual);
    }

    /// <summary>Validates each recognized provider-neutral notification variable.</summary>
    [Theory]
    [MemberData(nameof(SupportedEnvironmentNames))]
    public void ParseNotificationOptionsRecognizesEveryProviderNeutralEnvironmentName(string name)
    {
        ApplicationConfigurationException exception = Assert.Throws<ApplicationConfigurationException>(
            () =>
            ApplicationConfiguration.ParseNotificationOptions(candidate => candidate.Equals(name, StringComparison.Ordinal) ? "invalid" : null)
        );

        Assert.Contains(name, exception.Message, StringComparison.Ordinal);
    }
}
