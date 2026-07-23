namespace ThermalWatch.Core;

public readonly record struct GibsLayerPair(
    string BaseLayer,
    string BaseTileMatrixSet,
    string OverlayLayer,
    string OverlayTileMatrixSet);
