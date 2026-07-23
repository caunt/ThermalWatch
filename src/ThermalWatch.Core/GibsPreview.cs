namespace ThermalWatch.Core;

public sealed record GibsPreview(
    byte[]? PngBytes,
    GibsPreviewSource? BaseSource)
{
    public GibsPreview(byte[]? pngBytes)
        : this(pngBytes, BaseSource: null)
    {
    }

    public bool IsAvailable => PngBytes is { Length: > 0 };

    public static GibsPreview Unavailable { get; } = new(PngBytes: null, BaseSource: null);
}
