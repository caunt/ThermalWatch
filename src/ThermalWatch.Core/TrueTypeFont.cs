using System.Runtime.InteropServices;
using static StbTrueTypeSharp.StbTrueType;

namespace ThermalWatch.Core;

internal sealed unsafe class TrueTypeFont : IDisposable
{
    private const string FontResourceName = "ThermalWatch.Core.Data.LiberationSans-Regular.ttf";
    private static readonly byte[] s_fontData = ReadEmbeddedFont();
    private readonly stbtt_fontinfo _fontInfo = new();
    private GCHandle _fontDataHandle;
    private bool _disposed;

    public TrueTypeFont()
    {
        _fontDataHandle = GCHandle.Alloc(s_fontData, GCHandleType.Pinned);
        if (stbtt_InitFont(
            _fontInfo,
            (byte*)_fontDataHandle.AddrOfPinnedObject(),
            offset: 0) == 0)
        {
            _fontDataHandle.Free();
            throw new InvalidDataException(message: "The embedded ruler font is invalid.");
        }
    }

    public TrueTypeTextMask Rasterize(string text, int pixelHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelHeight);

        float scale = stbtt_ScaleForPixelHeight(_fontInfo, pixelHeight);
        int[] glyphIndices = FindGlyphs(text);
        GlyphPlacement[] placements = CreatePlacements(glyphIndices, scale);
        int minimumX = placements.Min(placement => placement.X);
        int minimumY = placements.Min(placement => placement.Y);
        int maximumX = placements.Max(placement => placement.Right);
        int maximumY = placements.Max(placement => placement.Bottom);
        int width = maximumX - minimumX;
        int height = maximumY - minimumY;
        if (width <= 0 || height <= 0)
            throw new InvalidDataException(message: "The ruler text has no visible glyphs.");

        byte[] pixels = new byte[checked(width * height)];
        for (int index = 0; index < placements.Length; index++)
        {
            GlyphPlacement placement = placements[index];
            RasterizeGlyph(
                glyphIndices[index],
                placement,
                scale,
                pixels,
                width,
                minimumX,
                minimumY);
        }

        return new(pixels, width, height);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _fontInfo.Dispose();
        _fontDataHandle.Free();
        _disposed = true;
    }

    private int[] FindGlyphs(string text)
    {
        int[] glyphIndices = new int[text.Length];
        for (int index = 0; index < text.Length; index++)
        {
            int glyphIndex = stbtt_FindGlyphIndex(_fontInfo, text[index]);
            if (glyphIndex == 0)
                throw new InvalidDataException(message: $"The ruler font does not contain '{text[index]}'.");

            glyphIndices[index] = glyphIndex;
        }

        return glyphIndices;
    }

    private GlyphPlacement[] CreatePlacements(int[] glyphIndices, float scale)
    {
        var placements = new GlyphPlacement[glyphIndices.Length];
        float penX = 0;
        for (int index = 0; index < glyphIndices.Length; index++)
        {
            int glyphIndex = glyphIndices[index];
            int x0;
            int y0;
            int x1;
            int y1;
            stbtt_GetGlyphBitmapBox(_fontInfo, glyphIndex, scale, scale, &x0, &y0, &x1, &y1);
            int originX = RoundPixel(penX);
            placements[index] = new(originX + x0, y0, x1 - x0, y1 - y0);

            int advanceWidth;
            stbtt_GetGlyphHMetrics(_fontInfo, glyphIndex, &advanceWidth, leftSideBearing: null);
            int kerning = index + 1 < glyphIndices.Length
                ? stbtt_GetGlyphKernAdvance(_fontInfo, glyphIndex, glyphIndices[index + 1])
                : 0;
            penX += (advanceWidth + kerning) * scale;
        }

        return placements;
    }

    private void RasterizeGlyph(
        int glyphIndex,
        GlyphPlacement placement,
        float scale,
        byte[] destination,
        int destinationWidth,
        int minimumX,
        int minimumY)
    {
        if (placement.Width <= 0 || placement.Height <= 0)
            return;

        byte[] glyphPixels = new byte[checked(placement.Width * placement.Height)];
        fixed (byte* glyphPointer = glyphPixels)
        {
            stbtt_MakeGlyphBitmap(
                _fontInfo,
                glyphPointer,
                placement.Width,
                placement.Height,
                placement.Width,
                scale,
                scale,
                glyphIndex);
        }

        int destinationX = placement.X - minimumX;
        int destinationY = placement.Y - minimumY;
        for (int y = 0; y < placement.Height; y++)
        {
            int sourceOffset = y * placement.Width;
            int destinationOffset = (destinationY + y) * destinationWidth + destinationX;
            for (int x = 0; x < placement.Width; x++)
            {
                destination[destinationOffset + x] = Math.Max(
                    destination[destinationOffset + x],
                    glyphPixels[sourceOffset + x]);
            }
        }
    }

    private static int RoundPixel(float value) =>
        (int)MathF.Round(value, MidpointRounding.AwayFromZero);

    private static byte[] ReadEmbeddedFont()
    {
        using Stream stream = typeof(TrueTypeFont).Assembly.GetManifestResourceStream(FontResourceName)
            ?? throw new InvalidOperationException(message: "The embedded ruler font is unavailable.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct GlyphPlacement(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;

        public int Bottom => Y + Height;
    }
}
