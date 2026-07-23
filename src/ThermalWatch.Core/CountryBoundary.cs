using System.Collections.Immutable;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;

namespace ThermalWatch.Core;

internal sealed record CountryBoundary(
    Geometry Geometry,
    IPreparedGeometry Prepared,
    ImmutableArray<GeographicBounds> Tiles);
