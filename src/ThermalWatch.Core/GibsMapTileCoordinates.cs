using System.Runtime.InteropServices;

namespace ThermalWatch.Core;

[StructLayout(LayoutKind.Auto)]
public readonly record struct GibsMapTileCoordinates
{
    private GibsMapTileCoordinates(int zoom, int x, int y)
    {
        Zoom = zoom;
        X = x;
        Y = y;
    }

    public int Zoom { get; }

    public int X { get; }

    public int Y { get; }

    public static bool TryCreate(int zoom, int x, int y, out GibsMapTileCoordinates coordinates)
    {
        int tileCount = zoom is >= 0 and <= GibsMapTileClient.MaximumZoom
            ? 1 << zoom
            : 0;
        if (x >= 0 && x < tileCount && y >= 0 && y < tileCount)
        {
            coordinates = new(zoom, x, y);
            return true;
        }

        coordinates = default;
        return false;
    }
}
