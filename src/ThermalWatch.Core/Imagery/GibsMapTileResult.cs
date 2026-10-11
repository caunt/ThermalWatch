namespace ThermalWatch.Core;

/// <summary>A composed Web Mercator PNG and the extent of its usable imagery.</summary>
/// <param name="PngBytes">The complete PNG payload, with missing pixels transparent.</param>
/// <param name="Coverage">The usable-pixel coverage classification.</param>
public sealed record GibsMapTileResult(ReadOnlyMemory<byte> PngBytes, GibsMapTileCoverage Coverage);
