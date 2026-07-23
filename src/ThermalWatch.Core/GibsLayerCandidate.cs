namespace ThermalWatch.Core;

internal readonly record struct GibsLayerCandidate(
    string BaseLayer,
    string BaseTileMatrixSet,
    string OverlayLayer,
    string OverlayTileMatrixSet,
    GibsPreviewSource BaseSource);
