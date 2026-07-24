using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using StbImageSharp;
using StbImageWriteSharp;

namespace ThermalWatch.Core;

internal static class GibsPreviewRuler
{
    private const int GlyphColumns = 5;
    private const int GlyphRows = 7;
    private const int GlyphSpacingColumns = 1;
    private const int MaximumDirectTicks = 100_000;
    private const int MaximumIntervalLabels = 10_000;
    private static readonly PixelColor s_black = new(Red: 0, Green: 0, Blue: 0, Alpha: byte.MaxValue);
    private static readonly PixelColor s_white = new(Red: byte.MaxValue, Green: byte.MaxValue, Blue: byte.MaxValue, Alpha: byte.MaxValue);

    public static GibsPreviewRulerLayout CreateLayout(GibsPreviewDimensions dimensions)
    {
        ValidateDimensions(dimensions);
        RulerMetrics metrics = CreateMetrics(dimensions);
        ImmutableArray<GibsPreviewRulerTick> horizontalTicks = CreateTicks(
            GibsPreviewRulerAxis.Horizontal,
            dimensions.WidthKilometers,
            metrics.AxisX,
            metrics.AxisRight,
            metrics.MinorTickLength,
            metrics.MajorTickLength);
        ImmutableArray<GibsPreviewRulerTick> verticalTicks = CreateTicks(
            GibsPreviewRulerAxis.Vertical,
            dimensions.HeightKilometers,
            metrics.AxisY,
            metrics.AxisTop,
            metrics.MinorTickLength,
            metrics.MajorTickLength);
        ImmutableArray<GibsPreviewRulerLabel> labels = CreateLabels(dimensions, metrics);

        return new(
            dimensions.PixelWidth,
            dimensions.PixelHeight,
            metrics.AxisX,
            metrics.AxisRight,
            metrics.AxisY,
            metrics.AxisTop,
            metrics.LineWidth,
            metrics.OutlineWidth,
            metrics.MinorTickLength,
            metrics.MajorTickLength,
            metrics.GlyphScale,
            horizontalTicks,
            verticalTicks,
            labels);
    }

    private static RulerMetrics CreateMetrics(GibsPreviewDimensions dimensions)
    {
        int shorterSide = Math.Min(dimensions.PixelWidth, dimensions.PixelHeight);
        int glyphScale = Scale(shorterSide, divisor: 160, minimum: 1);
        int lineWidth = Scale(shorterSide, divisor: 1_000, minimum: 1);
        int outlineWidth = Scale(shorterSide, divisor: 900, minimum: 1);
        int margin = Scale(shorterSide, divisor: 80, minimum: 1);
        int labelGap = Scale(shorterSide, divisor: 170, minimum: 1);
        int minorTickLength = Scale(shorterSide, divisor: 120, minimum: 1);
        int majorTickLength = Scale(shorterSide, divisor: 65, minimum: 2);
        int fontHeight = GlyphRows * glyphScale;
        int axisX = Math.Min(
            dimensions.PixelWidth - 1,
            margin + fontHeight + labelGap + outlineWidth);
        int axisRight = Math.Max(axisX, dimensions.PixelWidth - 1 - margin);
        int axisY = Math.Max(
            val1: 0,
            dimensions.PixelHeight - 1 - margin - fontHeight - labelGap - outlineWidth);
        int axisTop = Math.Min(axisY, margin + fontHeight / 2 + outlineWidth);
        return new(
            glyphScale,
            lineWidth,
            outlineWidth,
            margin,
            labelGap,
            minorTickLength,
            majorTickLength,
            axisX,
            axisRight,
            axisY,
            axisTop);
    }

    private static ImmutableArray<GibsPreviewRulerLabel> CreateLabels(
        GibsPreviewDimensions dimensions,
        RulerMetrics metrics)
    {
        ImmutableArray<GibsPreviewRulerLabel>.Builder labels = ImmutableArray.CreateBuilder<GibsPreviewRulerLabel>();
        AddRequiredLabels(
            labels,
            dimensions,
            metrics);
        AddIntervalLabels(
            labels,
            GibsPreviewRulerAxis.Horizontal,
            dimensions.WidthKilometers,
            metrics.GlyphScale,
            metrics.OutlineWidth,
            metrics.Margin,
            metrics.LabelGap,
            metrics.MajorTickLength,
            metrics.AxisX,
            metrics.AxisRight,
            metrics.AxisY,
            dimensions.PixelWidth,
            dimensions.PixelHeight);
        AddIntervalLabels(
            labels,
            GibsPreviewRulerAxis.Vertical,
            dimensions.HeightKilometers,
            metrics.GlyphScale,
            metrics.OutlineWidth,
            metrics.Margin,
            metrics.LabelGap,
            metrics.MajorTickLength,
            metrics.AxisY,
            metrics.AxisTop,
            metrics.AxisX,
            dimensions.PixelWidth,
            dimensions.PixelHeight);
        return labels.ToImmutable();
    }

    public static byte[]? Render(byte[] pngBytes, GibsPreviewDimensions dimensions)
    {
        try
        {
            var image = ImageResult.FromMemory(
                pngBytes,
                StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            if (image.Width != dimensions.PixelWidth
                || image.Height != dimensions.PixelHeight
                || image.Data.Length != checked(dimensions.PixelWidth * dimensions.PixelHeight * 4))
            {
                return null;
            }

            GibsPreviewRulerLayout layout = CreateLayout(dimensions);
            DrawLines(image.Data, layout, s_black, layout.OutlineWidth);
            DrawLines(image.Data, layout, s_white, padding: 0);
            foreach (GibsPreviewRulerLabel label in layout.Labels)
                DrawLabel(image.Data, layout, label);

            using var stream = new MemoryStream();
            new ImageWriter().WritePng(
                image.Data,
                dimensions.PixelWidth,
                dimensions.PixelHeight,
                StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha,
                stream);
            return stream.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void ValidateDimensions(GibsPreviewDimensions dimensions)
    {
        if (dimensions.PixelWidth <= 0
            || dimensions.PixelHeight <= 0
            || !double.IsFinite(dimensions.WidthKilometers)
            || dimensions.WidthKilometers <= 0
            || !double.IsFinite(dimensions.HeightKilometers)
            || dimensions.HeightKilometers <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions));
        }
    }

    private static int Scale(int shorterSide, int divisor, int minimum) =>
        Math.Max(
            minimum,
            (int)Math.Round(shorterSide / (double)divisor, MidpointRounding.AwayFromZero));

    private static ImmutableArray<GibsPreviewRulerTick> CreateTicks(
        GibsPreviewRulerAxis axis,
        double coverageKilometers,
        int start,
        int end,
        int minorTickLength,
        int majorTickLength)
    {
        var ticksByCoordinate = new Dictionary<int, GibsPreviewRulerTick>();
        double wholeKilometers = Math.Floor(coverageKilometers);
        if (wholeKilometers <= MaximumDirectTicks)
        {
            for (int kilometer = 1; kilometer <= (int)wholeKilometers; kilometer++)
            {
                if (kilometer >= coverageKilometers)
                    break;

                AddTick(
                    ticksByCoordinate,
                    axis,
                    kilometer,
                    coverageKilometers,
                    start,
                    end,
                    minorTickLength,
                    majorTickLength);
            }
        }
        else
        {
            int pixelLength = Math.Abs(end - start);
            for (int offset = 1; offset < pixelLength; offset++)
            {
                double kilometer = Math.Round(
                    offset * coverageKilometers / pixelLength,
                    MidpointRounding.AwayFromZero);
                AddTick(
                    ticksByCoordinate,
                    axis,
                    kilometer,
                    coverageKilometers,
                    start,
                    end,
                    minorTickLength,
                    majorTickLength);
            }
        }

        AddTick(
            ticksByCoordinate,
            axis,
            coverageKilometers,
            coverageKilometers,
            start,
            end,
            minorTickLength,
            majorTickLength,
            forceMajor: true);
        return [.. ticksByCoordinate.Values.OrderBy(tick => tick.Kilometer)];
    }

    private static void AddTick(
        Dictionary<int, GibsPreviewRulerTick> ticksByCoordinate,
        GibsPreviewRulerAxis axis,
        double kilometer,
        double coverageKilometers,
        int start,
        int end,
        int minorTickLength,
        int majorTickLength,
        bool forceMajor = false)
    {
        int coordinate = MapCoordinate(kilometer, coverageKilometers, start, end);
        bool isMajor = forceMajor || kilometer % 10 == 0;
        var tick = new GibsPreviewRulerTick(
            axis,
            kilometer,
            coordinate,
            isMajor ? majorTickLength : minorTickLength,
            isMajor);
        if (!ticksByCoordinate.TryGetValue(coordinate, out GibsPreviewRulerTick existing)
            || tick.IsMajor && !existing.IsMajor)
        {
            ticksByCoordinate[coordinate] = tick;
        }
    }

    private static int MapCoordinate(
        double kilometer,
        double coverageKilometers,
        int start,
        int end) =>
        start + (int)Math.Round(
            (end - start) * kilometer / coverageKilometers,
            MidpointRounding.AwayFromZero);

    private static void AddRequiredLabels(
        ImmutableArray<GibsPreviewRulerLabel>.Builder labels,
        GibsPreviewDimensions dimensions,
        RulerMetrics metrics)
    {
        AddEndpointLabels(labels, dimensions, metrics);
        AddOriginAndUnitLabels(labels, dimensions, metrics);
    }

    private static void AddEndpointLabels(
        ImmutableArray<GibsPreviewRulerLabel>.Builder labels,
        GibsPreviewDimensions dimensions,
        RulerMetrics metrics)
    {
        int fontHeight = GlyphRows * metrics.GlyphScale;
        string widthText = FormatCoverage(dimensions.WidthKilometers);
        string heightText = FormatCoverage(dimensions.HeightKilometers);
        int widthEndpointX = ClampLabelStart(
            metrics.AxisRight - MeasureText(widthText, metrics.GlyphScale) / 2,
            MeasureText(widthText, metrics.GlyphScale),
            metrics.Margin,
            dimensions.PixelWidth);
        int bottomLabelY = Math.Max(
            metrics.Margin,
            metrics.AxisY - metrics.MajorTickLength - metrics.LabelGap - fontHeight);
        labels.Add(CreateLabel(
            widthText,
            GibsPreviewRulerAxis.Horizontal,
            GibsPreviewRulerLabelKind.Endpoint,
            dimensions.WidthKilometers,
            widthEndpointX,
            bottomLabelY,
            metrics.GlyphScale,
            isVertical: false));

        int heightEndpointY = ClampLabelStart(
            metrics.AxisTop - fontHeight / 2,
            fontHeight,
            metrics.Margin,
            dimensions.PixelHeight);
        labels.Add(CreateLabel(
            heightText,
            GibsPreviewRulerAxis.Vertical,
            GibsPreviewRulerLabelKind.Endpoint,
            dimensions.HeightKilometers,
            Math.Min(
                dimensions.PixelWidth - MeasureText(heightText, metrics.GlyphScale),
                metrics.AxisX + metrics.MajorTickLength + metrics.LabelGap),
            heightEndpointY,
            metrics.GlyphScale,
            isVertical: false));
    }

    private static void AddOriginAndUnitLabels(
        ImmutableArray<GibsPreviewRulerLabel>.Builder labels,
        GibsPreviewDimensions dimensions,
        RulerMetrics metrics)
    {
        int fontHeight = GlyphRows * metrics.GlyphScale;
        labels.Add(CreateLabel(
            text: "0",
            GibsPreviewRulerAxis.Horizontal,
            GibsPreviewRulerLabelKind.Origin,
            kilometer: 0,
            Math.Min(
                dimensions.PixelWidth - MeasureText(text: "0", metrics.GlyphScale),
                metrics.AxisX + metrics.LabelGap + metrics.OutlineWidth),
            Math.Min(
                dimensions.PixelHeight - fontHeight,
                metrics.AxisY + metrics.LabelGap + metrics.OutlineWidth),
            metrics.GlyphScale,
            isVertical: false));

        int unitWidth = MeasureText(text: "km", metrics.GlyphScale);
        labels.Add(CreateLabel(
            text: "km",
            GibsPreviewRulerAxis.Horizontal,
            GibsPreviewRulerLabelKind.Unit,
            kilometer: null,
            Math.Max(metrics.Margin, metrics.AxisRight - unitWidth),
            Math.Min(
                dimensions.PixelHeight - fontHeight,
                metrics.AxisY + metrics.LabelGap + metrics.OutlineWidth),
            metrics.GlyphScale,
            isVertical: false));
        labels.Add(CreateLabel(
            text: "km",
            GibsPreviewRulerAxis.Vertical,
            GibsPreviewRulerLabelKind.Unit,
            kilometer: null,
            metrics.Margin,
            Math.Min(
                dimensions.PixelHeight - unitWidth,
                metrics.AxisTop + metrics.MajorTickLength + metrics.LabelGap),
            metrics.GlyphScale,
            isVertical: true));
    }

    private static void AddIntervalLabels(
        ImmutableArray<GibsPreviewRulerLabel>.Builder labels,
        GibsPreviewRulerAxis axis,
        double coverageKilometers,
        int glyphScale,
        int outlineWidth,
        int margin,
        int labelGap,
        int majorTickLength,
        int start,
        int end,
        int perpendicularAxis,
        int imageWidth,
        int imageHeight)
    {
        int intervalCount = coverageKilometers / 10 >= MaximumIntervalLabels
            ? MaximumIntervalLabels
            : (int)Math.Floor(coverageKilometers / 10);
        if (intervalCount == MaximumIntervalLabels)
            return;

        int fontHeight = GlyphRows * glyphScale;
        int clearance = Math.Max(outlineWidth * 2, glyphScale / 2);
        for (int interval = 1; interval <= intervalCount; interval++)
        {
            double kilometer = interval * 10;
            if (kilometer >= coverageKilometers)
                break;

            string text = interval.ToString(CultureInfo.InvariantCulture) + "0";
            int textWidth = MeasureText(text, glyphScale);
            int coordinate = MapCoordinate(kilometer, coverageKilometers, start, end);
            GibsPreviewRulerLabel candidate = axis switch
            {
                GibsPreviewRulerAxis.Horizontal => CreateLabel(
                    text,
                    axis,
                    GibsPreviewRulerLabelKind.Interval,
                    kilometer,
                    ClampLabelStart(coordinate - textWidth / 2, textWidth, margin, imageWidth),
                    Math.Max(margin, perpendicularAxis - majorTickLength - labelGap - fontHeight),
                    glyphScale,
                    isVertical: false),
                GibsPreviewRulerAxis.Vertical => CreateLabel(
                    text,
                    axis,
                    GibsPreviewRulerLabelKind.Interval,
                    kilometer,
                    Math.Min(imageWidth - textWidth, perpendicularAxis + majorTickLength + labelGap),
                    ClampLabelStart(coordinate - fontHeight / 2, fontHeight, margin, imageHeight),
                    glyphScale,
                    isVertical: false),
                _ => throw new ArgumentOutOfRangeException(nameof(axis))
            };
            if (labels.All(label => !label.Bounds.Inflate(clearance).Intersects(candidate.Bounds.Inflate(clearance))))
                labels.Add(candidate);
        }
    }

    private static int ClampLabelStart(int start, int length, int margin, int imageLength) =>
        Math.Clamp(start, Math.Min(margin, imageLength - length), Math.Max(margin, imageLength - margin - length));

    private static GibsPreviewRulerLabel CreateLabel(
        string text,
        GibsPreviewRulerAxis axis,
        GibsPreviewRulerLabelKind kind,
        double? kilometer,
        int x,
        int y,
        int glyphScale,
        bool isVertical)
    {
        int textWidth = MeasureText(text, glyphScale);
        int textHeight = GlyphRows * glyphScale;
        PixelRectangle bounds = isVertical
            ? new PixelRectangle(x, y, textHeight, textWidth)
            : new PixelRectangle(x, y, textWidth, textHeight);
        return new(text, axis, kind, kilometer, bounds, isVertical);
    }

    private static string FormatCoverage(double value) =>
        value.ToString(format: "0.##", CultureInfo.InvariantCulture);

    private static int MeasureText(string text, int glyphScale) =>
        (text.Length * GlyphColumns + Math.Max(val1: 0, text.Length - 1) * GlyphSpacingColumns) * glyphScale;

    private static void DrawLines(
        byte[] pixels,
        GibsPreviewRulerLayout layout,
        PixelColor color,
        int padding)
    {
        DrawSegment(
            pixels,
            layout.PixelWidth,
            layout.PixelHeight,
            layout.AxisX,
            layout.AxisY,
            layout.AxisRight,
            layout.AxisY,
            layout.LineWidth,
            padding,
            color);
        DrawSegment(
            pixels,
            layout.PixelWidth,
            layout.PixelHeight,
            layout.AxisX,
            layout.AxisY,
            layout.AxisX,
            layout.AxisTop,
            layout.LineWidth,
            padding,
            color);
        foreach (GibsPreviewRulerTick tick in layout.HorizontalTicks)
        {
            DrawSegment(
                pixels,
                layout.PixelWidth,
                layout.PixelHeight,
                tick.Coordinate,
                layout.AxisY,
                tick.Coordinate,
                layout.AxisY - tick.Length,
                layout.LineWidth,
                padding,
                color);
        }

        foreach (GibsPreviewRulerTick tick in layout.VerticalTicks)
        {
            DrawSegment(
                pixels,
                layout.PixelWidth,
                layout.PixelHeight,
                layout.AxisX,
                tick.Coordinate,
                layout.AxisX + tick.Length,
                tick.Coordinate,
                layout.LineWidth,
                padding,
                color);
        }
    }

    private static void DrawSegment(
        byte[] pixels,
        int pixelWidth,
        int pixelHeight,
        int startX,
        int startY,
        int endX,
        int endY,
        int lineWidth,
        int padding,
        PixelColor color)
    {
        int halfWidth = lineWidth / 2 + padding;
        int x = Math.Min(startX, endX) - (startX == endX ? halfWidth : padding);
        int y = Math.Min(startY, endY) - (startY == endY ? halfWidth : padding);
        int width = Math.Abs(endX - startX) + 1 + (startX == endX ? halfWidth * 2 : padding * 2);
        int height = Math.Abs(endY - startY) + 1 + (startY == endY ? halfWidth * 2 : padding * 2);
        DrawRectangle(pixels, pixelWidth, pixelHeight, x, y, width, height, color);
    }

    private static void DrawLabel(
        byte[] pixels,
        GibsPreviewRulerLayout layout,
        GibsPreviewRulerLabel label)
    {
        DrawTextPass(pixels, layout, label, s_black, layout.OutlineWidth);
        DrawTextPass(pixels, layout, label, s_white, padding: 0);
    }

    private static void DrawTextPass(
        byte[] pixels,
        GibsPreviewRulerLayout layout,
        GibsPreviewRulerLabel label,
        PixelColor color,
        int padding)
    {
        int characterStride = GlyphColumns + GlyphSpacingColumns;
        for (int characterIndex = 0; characterIndex < label.Text.Length; characterIndex++)
        {
            ReadOnlySpan<byte> glyph = GetGlyph(label.Text[characterIndex]);
            for (int row = 0; row < GlyphRows; row++)
            {
                for (int column = 0; column < GlyphColumns; column++)
                {
                    if ((glyph[row] & 1 << (GlyphColumns - 1 - column)) == 0)
                        continue;

                    int textColumn = characterIndex * characterStride + column;
                    int x = label.IsVertical
                        ? label.Bounds.X + (GlyphRows - 1 - row) * layout.GlyphScale
                        : label.Bounds.X + textColumn * layout.GlyphScale;
                    int y = label.IsVertical
                        ? label.Bounds.Y + textColumn * layout.GlyphScale
                        : label.Bounds.Y + row * layout.GlyphScale;
                    DrawRectangle(
                        pixels,
                        layout.PixelWidth,
                        layout.PixelHeight,
                        x - padding,
                        y - padding,
                        layout.GlyphScale + padding * 2,
                        layout.GlyphScale + padding * 2,
                        color);
                }
            }
        }
    }

    private static void DrawRectangle(
        byte[] pixels,
        int pixelWidth,
        int pixelHeight,
        int x,
        int y,
        int width,
        int height,
        PixelColor color)
    {
        int firstX = Math.Clamp(x, min: 0, max: pixelWidth);
        int firstY = Math.Clamp(y, min: 0, max: pixelHeight);
        int lastX = Math.Clamp(x + width, min: 0, max: pixelWidth);
        int lastY = Math.Clamp(y + height, min: 0, max: pixelHeight);
        for (int row = firstY; row < lastY; row++)
        {
            int offset = (row * pixelWidth + firstX) * 4;
            for (int column = firstX; column < lastX; column++)
            {
                pixels[offset] = color.Red;
                pixels[offset + 1] = color.Green;
                pixels[offset + 2] = color.Blue;
                pixels[offset + 3] = color.Alpha;
                offset += 4;
            }
        }
    }

    private static ReadOnlySpan<byte> GetGlyph(char value) => value switch
    {
        '0' => [0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110],
        '1' => [0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110],
        '2' => [0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b01000, 0b11111],
        '3' => [0b11110, 0b00001, 0b00001, 0b01110, 0b00001, 0b00001, 0b11110],
        '4' => [0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010],
        '5' => [0b11111, 0b10000, 0b10000, 0b11110, 0b00001, 0b00001, 0b11110],
        '6' => [0b01110, 0b10000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110],
        '7' => [0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000],
        '8' => [0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110],
        '9' => [0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00001, 0b01110],
        '.' => [0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b00110, 0b00110],
        'k' => [0b10001, 0b10010, 0b10100, 0b11000, 0b10100, 0b10010, 0b10001],
        'm' => [0b00000, 0b11011, 0b10101, 0b10101, 0b10101, 0b10101, 0b10101],
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct PixelColor(byte Red, byte Green, byte Blue, byte Alpha);

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct RulerMetrics(
        int GlyphScale,
        int LineWidth,
        int OutlineWidth,
        int Margin,
        int LabelGap,
        int MinorTickLength,
        int MajorTickLength,
        int AxisX,
        int AxisRight,
        int AxisY,
        int AxisTop);

    internal enum GibsPreviewRulerAxis
    {
        Horizontal,
        Vertical
    }

    internal enum GibsPreviewRulerLabelKind
    {
        Origin,
        Interval,
        Endpoint,
        Unit
    }

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct PixelRectangle(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;

        public int Bottom => Y + Height;

        public PixelRectangle Inflate(int amount) =>
            new(X - amount, Y - amount, Width + amount * 2, Height + amount * 2);

        public bool Intersects(PixelRectangle other) =>
            X < other.Right && Right > other.X && Y < other.Bottom && Bottom > other.Y;
    }

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct GibsPreviewRulerTick(
        GibsPreviewRulerAxis Axis,
        double Kilometer,
        int Coordinate,
        int Length,
        bool IsMajor);

    [StructLayout(LayoutKind.Auto)]
    internal readonly record struct GibsPreviewRulerLabel(
        string Text,
        GibsPreviewRulerAxis Axis,
        GibsPreviewRulerLabelKind Kind,
        double? Kilometer,
        PixelRectangle Bounds,
        bool IsVertical);

    internal sealed record GibsPreviewRulerLayout(
        int PixelWidth,
        int PixelHeight,
        int AxisX,
        int AxisRight,
        int AxisY,
        int AxisTop,
        int LineWidth,
        int OutlineWidth,
        int MinorTickLength,
        int MajorTickLength,
        int GlyphScale,
        ImmutableArray<GibsPreviewRulerTick> HorizontalTicks,
        ImmutableArray<GibsPreviewRulerTick> VerticalTicks,
        ImmutableArray<GibsPreviewRulerLabel> Labels);
}
