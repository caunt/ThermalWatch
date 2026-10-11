using System.Runtime.InteropServices;

namespace ThermalWatch.Core;

/// <summary>A validated Web Mercator tile address within the supported GIBS zoom range.</summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct GibsMapTileCoordinates
{
    private GibsMapTileCoordinates(int zoom, int column, int row)
    {
        Zoom = zoom;
        Column = column;
        Row = row;
    }

    /// <summary>The tile pyramid level.</summary>
    public int Zoom { get; }

    /// <summary>The west-to-east tile index.</summary>
    public int Column { get; }

    /// <summary>The north-to-south tile index.</summary>
    public int Row { get; }

    /// <summary>Validates a tile address without wrapping out-of-range coordinates.</summary>
    public static bool TryCreate(int zoom, int column, int row, out GibsMapTileCoordinates coordinates)
    {
        int tileCount = zoom is >= 0 and <= GibsMapTileClient.MaximumZoom
            ? 1 << zoom
            : 0;

        if (column >= 0 && column < tileCount && row >= 0 && row < tileCount)
        {
            coordinates = new(zoom, column, row);

            return true;
        }

        coordinates = default;

        return false;
    }
}
