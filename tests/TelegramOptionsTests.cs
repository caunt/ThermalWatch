using ThermalWatch.Telegram;

namespace ThermalWatch.Tests;

public sealed class TelegramOptionsTests
{
    [Fact]
    public void FromEnvironmentUsesStrictVegetationDefaults()
    {
        var options = TelegramOptions.FromEnvironment(_ => null);

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
        var options = TelegramOptions.FromEnvironment(name => name switch
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
        var options = TelegramOptions.FromEnvironment(name => name.Equals(value: "TELEGRAM_KEEP_MULTI_SATELLITE_CLUSTERS", StringComparison.Ordinal) ? "true" : null);

        Assert.False(options.LandCover.KeepMultiSatelliteVegetation);
    }
}
