using System.Runtime.InteropServices;

using static StbTrueTypeSharp.StbTrueType;

namespace ThermalWatch.Core;

internal sealed unsafe class TrueTypeFont : IDisposable
{
    private const string FontResourceName = "ThermalWatch.Core.Data.LiberationSans-Regular.ttf";
    private static readonly byte[] FontData = ReadEmbeddedFont();
    private readonly stbtt_fontinfo _fontMetadata = new();
    private bool _disposed;
    private GCHandle _fontDataHandle;

    public TrueTypeFont()
    {
        _fontDataHandle = GCHandle.Alloc(FontData, GCHandleType.Pinned);

        if (stbtt_InitFont(_fontMetadata, (byte*)_fontDataHandle.AddrOfPinnedObject(), offset: 0) == 0)
        {
            _fontDataHandle.Free();

            throw new InvalidDataException(message: "The embedded ruler font is invalid.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _fontMetadata.Dispose();
        _fontDataHandle.Free();
        _disposed = true;
    }

    public TrueTypeTextMask Rasterize(string text, int pixelHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelHeight);

        float scale = stbtt_ScaleForPixelHeight(_fontMetadata, pixelHeight);
        int[] glyphIndices = FindGlyphs(text);
        GlyphPlacement[] placements = CreatePlacements(glyphIndices, scale);
        int minimumX = placements.Min(static placement => placement.Left);
        int minimumY = placements.Min(static placement => placement.Top);
        int maximumX = placements.Max(static placement => placement.Right);
        int maximumY = placements.Max(static placement => placement.Bottom);
        int width = maximumX - minimumX;
        int height = maximumY - minimumY;

        if (width <= 0 || height <= 0)
            throw new InvalidDataException(message: "The ruler text has no visible glyphs.");

        byte[] pixels = new byte[checked(width * height)];

        for (int index = 0; index < placements.Length; index++)
        {
            GlyphPlacement placement = placements[index];
            RasterizeGlyph(glyphIndices[index], placement, scale, pixels, width, minimumX, minimumY);
        }

        return new(pixels, width, height);
    }

    private static byte[] ReadEmbeddedFont()
    {
        using Stream stream = typeof(TrueTypeFont).Assembly.GetManifestResourceStream(FontResourceName)
            ?? throw new InvalidOperationException(message: "The embedded ruler font is unavailable.");

        using var buffer = new MemoryStream();

        stream.CopyTo(buffer);

        return buffer.ToArray();
    }

    private static int RoundPixel(float value)
    {
        return (int)MathF.Round(value, MidpointRounding.AwayFromZero);
    }

    private GlyphPlacement[] CreatePlacements(int[] glyphIndices, float scale)
    {
        var placements = new GlyphPlacement[glyphIndices.Length];
        float penX = 0;

        for (int index = 0; index < glyphIndices.Length; index++)
        {
            int glyphIndex = glyphIndices[index];
            int glyphLeft;
            int glyphTop;
            int glyphRight;
            int glyphBottom;
            stbtt_GetGlyphBitmapBox(_fontMetadata, glyphIndex, scale, scale, &glyphLeft, &glyphTop, &glyphRight, &glyphBottom);
            int originX = RoundPixel(penX);
            placements[index] = new(originX + glyphLeft, glyphTop, glyphRight - glyphLeft, glyphBottom - glyphTop);

            int advanceWidth;
            stbtt_GetGlyphHMetrics(_fontMetadata, glyphIndex, &advanceWidth, leftSideBearing: null);

            int kerning = index + 1 < glyphIndices.Length
                ? stbtt_GetGlyphKernAdvance(_fontMetadata, glyphIndex, glyphIndices[index + 1])
                : 0;

            penX += (advanceWidth + kerning) * scale;
        }

        return placements;
    }

    private int[] FindGlyphs(string text)
    {
        int[] glyphIndices = new int[text.Length];

        for (int index = 0; index < text.Length; index++)
        {
            int glyphIndex = stbtt_FindGlyphIndex(_fontMetadata, text[index]);

            if (glyphIndex == 0)
                throw new InvalidDataException($"The ruler font does not contain '{text[index]}'.");

            glyphIndices[index] = glyphIndex;
        }

        return glyphIndices;
    }

    private void RasterizeGlyph(int glyphIndex, GlyphPlacement placement, float scale, byte[] destination, int destinationWidth, int minimumX, int minimumY)
    {
        if (placement.Width <= 0 || placement.Height <= 0)
            return;

        byte[] glyphPixels = new byte[checked(placement.Width * placement.Height)];
        fixed (byte* glyphPointer = glyphPixels)
            stbtt_MakeGlyphBitmap(_fontMetadata, glyphPointer, placement.Width, placement.Height, placement.Width, scale, scale, glyphIndex);

        int destinationX = placement.Left - minimumX;
        int destinationY = placement.Top - minimumY;

        for (int row = 0; row < placement.Height; row++)
        {
            int sourceOffset = row * placement.Width;
            int destinationOffset = ((destinationY + row) * destinationWidth) + destinationX;

            for (int column = 0; column < placement.Width; column++)
                destination[destinationOffset + column] = Math.Max(destination[destinationOffset + column], glyphPixels[sourceOffset + column]);
        }
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct GlyphPlacement(int Left, int Top, int Width, int Height)
    {
        public int Right => Left + Width;

        public int Bottom => Top + Height;
    }
}
