using System.Collections.Frozen;
using System.Collections.Immutable;
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
    private const double MaximumTileSpanDegrees = 10;
    private static readonly string[] s_countryCodePropertyNames = ["ISO_A3", "ADM0_A3"];
    private readonly FrozenDictionary<string, CountryBoundary> _boundaries;

    public CountryBoundaryCatalog(FirmsOptions options)
    {
        var requested = options.Countries.ToHashSet(StringComparer.Ordinal);
        Dictionary<string, List<Geometry>> geometries = LoadGeometries(requested);
        var boundaries = new Dictionary<string, CountryBoundary>(StringComparer.Ordinal);

        foreach (string countryCode in options.Countries)
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
            ImmutableArray<GeographicBounds> tiles = CreateTiles(geometry, prepared);
            if (tiles.Length == 0)
            {
                throw new CountryBoundaryException(
                    safeMessage: $"Embedded country boundary for '{countryCode}' cannot be tiled.");
            }

            boundaries.Add(countryCode, new(geometry, prepared, tiles));
        }

        _boundaries = boundaries.ToFrozenDictionary(StringComparer.Ordinal);
    }

    internal CountryBoundary Get(string countryCode) => _boundaries[countryCode];

    private static Dictionary<string, List<Geometry>> LoadGeometries(HashSet<string> requested)
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
                    .Where(code => code is not null && requested.Contains(code))
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

    private static ImmutableArray<GeographicBounds> CreateTiles(
        Geometry geometry,
        IPreparedGeometry prepared)
    {
        Envelope envelope = geometry.EnvelopeInternal;
        if (envelope.Width <= MaximumTileSpanDegrees
            && envelope.Height <= MaximumTileSpanDegrees)
        {
            return [ToBounds(envelope)];
        }

        GeometryFactory factory = geometry.Factory;
        var tiles = new HashSet<GeographicBounds>();

        foreach (Polygon polygon in GetPolygons(geometry))
        {
            Envelope componentEnvelope = polygon.EnvelopeInternal;
            int firstColumn = GridIndex(componentEnvelope.MinX, -180, count: 36);
            int lastColumn = GridIndex(Math.BitDecrement(componentEnvelope.MaxX), -180, count: 36);
            int firstRow = GridIndex(componentEnvelope.MinY, -90, count: 18);
            int lastRow = GridIndex(Math.BitDecrement(componentEnvelope.MaxY), -90, count: 18);

            for (int column = firstColumn; column <= lastColumn; column++)
            {
                for (int row = firstRow; row <= lastRow; row++)
                {
                    var tileEnvelope = new Envelope(
                        -180 + column * MaximumTileSpanDegrees,
                        -180 + (column + 1) * MaximumTileSpanDegrees,
                        -90 + row * MaximumTileSpanDegrees,
                        -90 + (row + 1) * MaximumTileSpanDegrees);
                    if (prepared.Intersects(factory.ToGeometry(tileEnvelope)))
                        tiles.Add(ToBounds(tileEnvelope));
                }
            }
        }

        return
        [
            .. tiles
                .OrderBy(tile => tile.West)
                .ThenBy(tile => tile.South)
        ];
    }

    private static IEnumerable<Polygon> GetPolygons(Geometry geometry) => geometry switch
    {
        Polygon polygon => [polygon],
        MultiPolygon multiPolygon => Enumerable.Range(start: 0, multiPolygon.NumGeometries)
            .Select(index => (Polygon)multiPolygon.GetGeometryN(index)),
        _ => []
    };

    private static int GridIndex(double coordinate, double minimum, int count) =>
        Math.Clamp((int)Math.Floor((coordinate - minimum) / MaximumTileSpanDegrees), min: 0, count - 1);

    private static GeographicBounds ToBounds(Envelope envelope) =>
        new(envelope.MinX, envelope.MinY, envelope.MaxX, envelope.MaxY);
}
