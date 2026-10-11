using ThermalWatch.Core;

namespace ThermalWatch.Tests;

/// <summary>Verifies country display names and unknown-code handling.</summary>
public sealed class CountryCatalogTests
{
    /// <summary>Resolves supported country codes to display names.</summary>
    [Theory]
    [InlineData("ATA", "Antarctica")]
    [InlineData("RUS", "Russia")]
    [InlineData("UKR", "Ukraine")]
    public void GetDisplayNameDecodesIsoAlpha3CountryCode(string countryCode, string expectedDisplayName)
    {
        Assert.True(CountryCatalog.IsValid(countryCode));
        Assert.Equal(expectedDisplayName, CountryCatalog.GetDisplayName(countryCode));
    }

    /// <summary>Preserves an unknown code instead of inventing a display name.</summary>
    [Fact]
    public void GetDisplayNamePreservesUnknownCountryCode()
    {
        Assert.False(CountryCatalog.IsValid(countryCode: "XXX"));
        Assert.Equal(expected: "XXX", CountryCatalog.GetDisplayName(countryCode: "XXX"));
    }
}
