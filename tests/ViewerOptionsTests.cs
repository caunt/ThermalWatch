using ThermalWatch.Viewer;

namespace ThermalWatch.Tests;

public sealed class ViewerOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FromEnvironment_TreatsMissingOrBlankGoogleKeyAsUnavailable(string? value)
    {
        var options = ViewerOptions.FromEnvironment(name =>
            name == "GOOGLE_MAPS_API_KEY" ? value : null);

        Assert.Null(options.GoogleMapsApiKey);
    }

    [Fact]
    public void FromEnvironment_TrimsGoogleKey()
    {
        var options = ViewerOptions.FromEnvironment(name =>
            name == "GOOGLE_MAPS_API_KEY" ? "  browser-key  " : null);

        Assert.Equal("browser-key", options.GoogleMapsApiKey);
    }
}
