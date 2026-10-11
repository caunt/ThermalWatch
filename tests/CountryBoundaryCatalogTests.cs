using NetTopologySuite.Geometries;

using ThermalWatch.Core;

namespace ThermalWatch.Tests;

/// <summary>Verifies the embedded country-boundary worldview.</summary>
public sealed class CountryBoundaryCatalogTests
{
    /// <summary>Assigns disputed locations consistently with the Ukraine worldview.</summary>
    [Fact]
    public void EmbeddedBoundariesUseNaturalEarthUkraineWorldview()
    {
        FirmsOptions options = new(
            new string(c: 'A', count: 32),
            ["UKR", "RUS", "CYP", "GEO", "SRB"],
            TimeSpan.FromMinutes(minutes: 5),
            TimeSpan.FromHours(hours: 24),
            TimeSpan.FromSeconds(seconds: 45),
            MaximumConcurrency: 4
        );

        var catalog = new CountryBoundaryCatalog(options);

        Assert.True(Covers(catalog, countryCode: "UKR", latitude: 44.952, longitude: 34.1));
        Assert.False(Covers(catalog, countryCode: "RUS", latitude: 44.952, longitude: 34.1));
        Assert.True(Covers(catalog, countryCode: "CYP", latitude: 35.2, longitude: 33.36));
        Assert.True(Covers(catalog, countryCode: "GEO", latitude: 43, longitude: 41.01));
        Assert.True(Covers(catalog, countryCode: "SRB", latitude: 42.67, longitude: 21.17));
    }

    private static bool Covers(CountryBoundaryCatalog catalog, string countryCode, double latitude, double longitude)
    {
        CountryBoundary boundary = catalog.Get(countryCode);
        Point point = boundary.Geometry.Factory.CreatePoint(new Coordinate(longitude, latitude));

        return boundary.Prepared.Covers(point);
    }
}
