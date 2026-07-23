using ThermalWatch.Viewer;

namespace ThermalWatch.Tests;

public sealed class ViewerOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FromEnvironmentTreatsMissingOrBlankGoogleKeyAsUnavailable(string? value)
    {
        var options = ViewerOptions.FromEnvironment(name => "GOOGLE_MAPS_API_KEY".Equals(name, StringComparison.Ordinal) ? value : null);

        Assert.Null(options.GoogleMapsApiKey);
    }

    [Fact]
    public void FromEnvironmentTrimsGoogleKey()
    {
        var options = ViewerOptions.FromEnvironment(name => "GOOGLE_MAPS_API_KEY".Equals(name, StringComparison.Ordinal) ? "  browser-key  " : null);

        Assert.Equal("browser-key", options.GoogleMapsApiKey);
    }
}
