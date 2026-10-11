using ThermalWatch.Viewer;

namespace ThermalWatch.Tests.Viewer;

/// <summary>Verifies optional viewer credentials.</summary>
public sealed class ViewerOptionsTests
{
    /// <summary>Disables Google Maps when its key is absent or blank.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FromEnvironmentTreatsMissingOrBlankGoogleKeyAsUnavailable(string? value)
    {
        var options = ViewerOptions.FromEnvironment(name => "GOOGLE_MAPS_API_KEY".Equals(name, StringComparison.Ordinal) ? value : null);

        Assert.Null(options.GoogleMapsApiKey);
    }

    /// <summary>Trims surrounding whitespace from the Google Maps key.</summary>
    [Fact]
    public void FromEnvironmentTrimsGoogleKey()
    {
        var options = ViewerOptions.FromEnvironment(name => "GOOGLE_MAPS_API_KEY".Equals(name, StringComparison.Ordinal) ? "  browser-key  " : null);

        Assert.Equal(expected: "browser-key", options.GoogleMapsApiKey);
    }
}
