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

public sealed class CountryBoundaryCatalog
{
    private const string ResourceName =
        "ThermalWatch.Core.Data.ne_50m_admin_0_countries.geojson.gz";
    private static readonly string[] s_countryCodePropertyNames = ["ISO_A3", "ADM0_A3"];
    private readonly FrozenDictionary<string, CountryBoundary> _boundaries;

    public CountryBoundaryCatalog(FirmsOptions options)
    {
        var requestedCountryCodes = options.CountryCodes.ToHashSet(StringComparer.Ordinal);
        Dictionary<string, List<Geometry>> geometries = LoadGeometries(requestedCountryCodes);
        var boundaries = new Dictionary<string, CountryBoundary>(StringComparer.Ordinal);

        foreach (string countryCode in options.CountryCodes)
        {
            if (!geometries.TryGetValue(countryCode, out List<Geometry>? parts) || parts.Count == 0)
            {
                throw new CountryBoundaryException(
                    safeMessage: $"Embedded country boundaries contain no usable geometry for '{countryCode}'.");
            }

            Geometry geometry = parts.Count == 1 ? parts[0] : UnaryUnionOp.Union(parts);
            if (!geometry.IsValid)
                geometry = GeometryFixer.Fix(geometry);

            if (geometry is not (Polygon or MultiPolygon) || geometry.IsEmpty || !geometry.IsValid)
            {
                throw new CountryBoundaryException(
                    safeMessage: $"Embedded country boundary for '{countryCode}' is invalid.");
            }

            geometry.SRID = 4326;
            IPreparedGeometry prepared = PreparedGeometryFactory.Prepare(geometry);
            GeographicBounds areaBounds = ToBounds(geometry.EnvelopeInternal);
            if (areaBounds.West < -180
                || areaBounds.South < -90
                || areaBounds.East > 180
                || areaBounds.North > 90
                || areaBounds.West >= areaBounds.East
                || areaBounds.South >= areaBounds.North)
            {
                throw new CountryBoundaryException(
                    safeMessage: $"Embedded country boundary for '{countryCode}' has invalid area bounds.");
            }

            boundaries.Add(countryCode, new(geometry, prepared, areaBounds));
        }

        _boundaries = boundaries.ToFrozenDictionary(StringComparer.Ordinal);
    }

    internal CountryBoundary Get(string countryCode) => _boundaries[countryCode];

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
                string?[] countryCodes = [.. s_countryCodePropertyNames
                    .Select(name => properties.TryGetProperty(name, out JsonElement property)
                        ? property.GetString()
                        : null)
                    .Where(code => code is not null && requestedCountryCodes.Contains(code))
                    .Distinct(StringComparer.Ordinal)];
                if (countryCodes.Length == 0)
                    continue;

                Geometry geometry = reader.Read<Geometry>(feature.GetProperty(propertyName: "geometry").GetRawText());
                if (geometry is not (Polygon or MultiPolygon) || geometry.IsEmpty)
                    continue;

                foreach (string? countryCode in countryCodes)
                {
                    if (!result.TryGetValue(countryCode!, out List<Geometry>? parts))
                        result.Add(countryCode!, parts = []);

                    parts.Add(geometry);
                }
            }

            return result;
        }
        catch (CountryBoundaryException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new CountryBoundaryException(safeMessage: "Embedded country boundary data is invalid.");
        }
    }

    private static GeographicBounds ToBounds(Envelope envelope) =>
        new(envelope.MinX, envelope.MinY, envelope.MaxX, envelope.MaxY);
}
