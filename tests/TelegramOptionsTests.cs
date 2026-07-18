using ThermalWatch.Telegram;

namespace ThermalWatch.Tests;

public sealed class TelegramOptionsTests
{
    [Fact]
    public void FromEnvironment_UsesStrictVegetationDefaults()
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
    public void FromEnvironment_ReadsVegetationSpecificExceptionNames()
    {
        var environment = new Dictionary<string, string>
        {
            ["TELEGRAM_KEEP_HIGH_FRP_VEGETATION"] = "true",
            ["TELEGRAM_KEEP_MULTI_SATELLITE_VEGETATION"] = "true"
        };

        var options = TelegramOptions.FromEnvironment(name => environment.GetValueOrDefault(name));

        Assert.True(options.LandCover.KeepHighFrpVegetation);
        Assert.True(options.LandCover.KeepMultiSatelliteVegetation);
    }

    [Fact]
    public void FromEnvironment_DoesNotUseReplacedMultiSatelliteName()
    {
        var options = TelegramOptions.FromEnvironment(name =>
            name == "TELEGRAM_KEEP_MULTI_SATELLITE_CLUSTERS" ? "true" : null);

        Assert.False(options.LandCover.KeepMultiSatelliteVegetation);
    }
}
