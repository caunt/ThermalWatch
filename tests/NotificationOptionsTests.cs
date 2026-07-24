using ThermalWatch.Api;
using ThermalWatch.Core;

namespace ThermalWatch.Tests;

public sealed class NotificationOptionsTests
{
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
            "TELEGRAM_KEEP_HIGH_FRP_VEGETATION" => "true",
            "TELEGRAM_KEEP_MULTI_SATELLITE_VEGETATION" => "true",
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
}
