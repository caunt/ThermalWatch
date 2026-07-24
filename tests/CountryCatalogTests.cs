using ThermalWatch.Core;

namespace ThermalWatch.Tests;

public sealed class CountryCatalogTests
{
    [Theory]
    [InlineData("ATA", "Antarctica")]
    [InlineData("RUS", "Russia")]
    [InlineData("UKR", "Ukraine")]
    public void GetDisplayNameDecodesIsoAlpha3CountryCode(string countryCode, string expectedDisplayName)
    {
        Assert.True(CountryCatalog.IsValid(countryCode));
        Assert.Equal(expectedDisplayName, CountryCatalog.GetDisplayName(countryCode));
    }

    [Fact]
    public void GetDisplayNamePreservesUnknownCountryCode()
    {
        Assert.False(CountryCatalog.IsValid(countryCode: "XXX"));
        Assert.Equal(expected: "XXX", actual: CountryCatalog.GetDisplayName(countryCode: "XXX"));
    }
}
