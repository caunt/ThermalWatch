namespace ThermalWatch.Core;

/// <summary>An exact-date notification preview, or an unavailable result that can be retried later.</summary>
public sealed record GibsPreview
{
    /// <summary>Creates a preview from its image payload and contextual imagery source.</summary>
    public GibsPreview(byte[]? pngBytes, GibsPreviewSource? baseSource)
    {
        if (pngBytes is not null)
            PngBytes = new ReadOnlyMemory<byte>(pngBytes);

        BaseSource = baseSource;
    }

    /// <summary>Creates a preview without contextual base-source metadata.</summary>
    /// <param name="pngBytes">The image bytes, or null when unavailable.</param>
    public GibsPreview(byte[]? pngBytes)
        : this(pngBytes, baseSource: null)
    {
    }

    /// <summary>Gets the retryable unavailable preview result.</summary>
    public static GibsPreview Unavailable { get; } = new(pngBytes: null, baseSource: null);

    /// <summary>The contextual base-imagery source, when available.</summary>
    public GibsPreviewSource? BaseSource { get; }

    /// <summary>Gets whether a nonempty composed image is present.</summary>
    public bool IsAvailable => PngBytes is { Length: > 0 };

    /// <summary>The composed PNG image, including its scale ruler, when available.</summary>
    public ReadOnlyMemory<byte>? PngBytes { get; }
}
