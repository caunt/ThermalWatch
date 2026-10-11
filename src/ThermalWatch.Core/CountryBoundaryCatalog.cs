using System.Collections.Frozen;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.IO;
using NetTopologySuite.Operation.Union;

namespace ThermalWatch.Core;

/// <summary>Loads and prepares configured country geometries from the embedded Ukraine-worldview boundary data.</summary>
public sealed class CountryBoundaryCatalog
{
    private const string ResourceName =
        "ThermalWatch.Core.Data.ne_10m_admin_0_countries_ukr.geojson.gz";
    private static readonly string[] CountryCodePropertyNames = ["ISO_A3", "ADM0_A3"];
    private readonly FrozenDictionary<string, CountryBoundary> _boundaries;

    /// <summary>Validates that every configured ingestion country has usable embedded geometry.</summary>
    /// <param name="options">The countries required for atomic area fallback.</param>
    public CountryBoundaryCatalog(FirmsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var requestedCountryCodes = new HashSet<string>([.. options.CountryCodes], StringComparer.Ordinal);
        Dictionary<string, List<Geometry>> geometries = LoadGeometries(requestedCountryCodes);
        var boundaries = new Dictionary<string, CountryBoundary>(StringComparer.Ordinal);

        foreach (string countryCode in options.CountryCodes)
        {
            if (!geometries.TryGetValue(countryCode, out List<Geometry>? parts) || parts.Count == 0)
                throw new CountryBoundaryException($"Embedded country boundaries contain no usable geometry for '{countryCode}'.");

            Geometry geometry = parts.Count == 1 ? parts[index: 0] : UnaryUnionOp.Union(parts);

            if (!geometry.IsValid)
                geometry = GeometryFixer.Fix(geometry);

            if (geometry is not (Polygon or MultiPolygon) || geometry.IsEmpty || !geometry.IsValid)
                throw new CountryBoundaryException($"Embedded country boundary for '{countryCode}' is invalid.");

            geometry.SRID = 4326;
            IPreparedGeometry prepared = PreparedGeometryFactory.Prepare(geometry);
            GeographicBounds areaBounds = ToBounds(geometry.EnvelopeInternal);

            bool validLongitudeBounds = areaBounds.West >= -180 && areaBounds.East <= 180 && areaBounds.West < areaBounds.East;
            bool validLatitudeBounds = areaBounds.South >= -90 && areaBounds.North <= 90 && areaBounds.South < areaBounds.North;

            if (!validLongitudeBounds || !validLatitudeBounds)
                throw new CountryBoundaryException($"Embedded country boundary for '{countryCode}' has invalid area bounds.");

            boundaries.Add(countryCode, new(geometry, prepared, areaBounds));
        }

        _boundaries = boundaries.ToFrozenDictionary(StringComparer.Ordinal);
    }

    internal CountryBoundary Get(string countryCode)
    {
        return _boundaries[countryCode];
    }

    private static Dictionary<string, List<Geometry>> LoadGeometries(HashSet<string> requestedCountryCodes)
    {
        try
        {
            Assembly assembly = typeof(CountryBoundaryCatalog).Assembly;

            using Stream resource = assembly.GetManifestResourceStream(ResourceName)
                ?? throw new CountryBoundaryException(safeMessage: "Embedded country boundary data is missing.");

            using var gzip = new GZipStream(resource, CompressionMode.Decompress);

            using var document = JsonDocument.Parse(gzip);

            var reader = new GeoJsonReader();
            var result = new Dictionary<string, List<Geometry>>(StringComparer.Ordinal);

            foreach (JsonElement feature in document.RootElement.GetProperty(propertyName: "features").EnumerateArray())
            {
                JsonElement properties = feature.GetProperty(propertyName: "properties");

                string[] countryCodes = [.. CountryCodePropertyNames
                    .Select(name => properties.TryGetProperty(name, out JsonElement property) ? property.GetString() : null)
                    .OfType<string>()
                    .Where(requestedCountryCodes.Contains)
                    .Distinct(StringComparer.Ordinal)];

                if (countryCodes.Length == 0)
                    continue;

                Geometry geometry = reader.Read<Geometry>(feature.GetProperty(propertyName: "geometry").GetRawText());

                if (geometry is not (Polygon or MultiPolygon) || geometry.IsEmpty)
                    continue;

                foreach (string countryCode in countryCodes)
                {
                    if (!result.TryGetValue(countryCode, out List<Geometry>? parts))
                        result.Add(countryCode, parts = []);

                    parts.Add(geometry);
                }
            }

            return result;
        }
        catch (CountryBoundaryException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or JsonException or ArgumentException or InvalidOperationException)
        {
            throw new CountryBoundaryException(safeMessage: "Embedded country boundary data is invalid.");
        }
    }

    private static GeographicBounds ToBounds(Envelope envelope)
    {
        return new(envelope.MinX, envelope.MinY, envelope.MaxX, envelope.MaxY);
    }
}
