using System.Buffers.Binary;
using System.IO.Compression;
using StbImageWriteSharp;

namespace ThermalWatch.Tests;

internal static class PngTestData
{
    public static byte[] CreateSolidRgb(
        int width,
        int height,
        byte red,
        byte green,
        byte blue)
    {
        byte[] pixels = new byte[width * height * 3];
        for (int offset = 0; offset < pixels.Length; offset += 3)
        {
            pixels[offset] = red;
            pixels[offset + 1] = green;
            pixels[offset + 2] = blue;
        }

        return WriteTrueColorPng(pixels, width, height, ColorComponents.RedGreenBlue);
    }

    public static byte[] CreateSolidRgba(
        int width,
        int height,
        byte red,
        byte green,
        byte blue,
        byte alpha) =>
        CreateRgba(width, height, _ => (red, green, blue, alpha));

    public static byte[] CreateRgba(
        int width,
        int height,
        Func<int, (byte Red, byte Green, byte Blue, byte Alpha)> pixelFactory)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int pixel = 0; pixel < width * height; pixel++)
        {
            (byte Red, byte Green, byte Blue, byte Alpha) color = pixelFactory(pixel);
            int offset = pixel * 4;
            pixels[offset] = color.Red;
            pixels[offset + 1] = color.Green;
            pixels[offset + 2] = color.Blue;
            pixels[offset + 3] = color.Alpha;
        }

        return WriteTrueColorPng(pixels, width, height, ColorComponents.RedGreenBlueAlpha);
    }

    public static byte[] CreateIndexedSolid(
        int width,
        int height,
        byte red,
        byte green,
        byte blue,
        byte alpha = byte.MaxValue)
    {
        byte[] raw = new byte[(width + 1) * height];
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(raw);

        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header[..4], (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.Slice(start: 4, length: 4), (uint)height);
        header[8] = 8;
        header[9] = 3;
        WriteChunk(png, "IHDR"u8, header);
        WriteChunk(png, "PLTE"u8, [red, green, blue]);
        if (alpha != byte.MaxValue)
            WriteChunk(png, "tRNS"u8, [alpha]);
        WriteChunk(png, "IDAT"u8, compressed.ToArray());
        WriteChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static byte[] WriteTrueColorPng(
        byte[] pixels,
        int width,
        int height,
        ColorComponents components)
    {
        using var stream = new MemoryStream();
        new ImageWriter().WritePng(pixels, width, height, components, stream);
        return stream.ToArray();
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> value = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(value, (uint)data.Length);
        stream.Write(value);
        stream.Write(type);
        stream.Write(data);

        uint crc = uint.MaxValue;
        UpdateCrc(ref crc, type);
        UpdateCrc(ref crc, data);
        BinaryPrimitives.WriteUInt32BigEndian(value, ~crc);
        stream.Write(value);
    }

    private static void UpdateCrc(ref uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte item in data)
        {
            crc ^= item;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
        }
    }
}
