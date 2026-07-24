using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using StbImageSharp;
using StbImageWriteSharp;

namespace ThermalWatch.Core;

internal static class GibsPreviewRuler
{
    private const int MaximumDirectTicks = 100_000;
    private const int MaximumIntervalLabels = 10_000;
    private static readonly PixelColor s_black = new(Red: 0, Green: 0, Blue: 0, Alpha: byte.MaxValue);
    private static readonly PixelColor s_textOutline = new(Red: 0, Green: 0, Blue: 0, Alpha: 220);
    private static readonly PixelColor s_white = new(Red: byte.MaxValue, Green: byte.MaxValue, Blue: byte.MaxValue, Alpha: byte.MaxValue);

    public static GibsPreviewRulerLayout CreateLayout(GibsPreviewDimensions dimensions)
    {
        ValidateDimensions(dimensions);
        int fontPixelHeight = CreateFontPixelHeight(dimensions);
        using var font = new TrueTypeFont();
        var text = new RulerText(font, fontPixelHeight);
        return CreateLayout(dimensions, fontPixelHeight, text);
    }

    private static GibsPreviewRulerLayout CreateLayout(
        GibsPreviewDimensions dimensions,
        int fontPixelHeight,
        RulerText text)
    {
        RulerMetrics metrics = CreateMetrics(dimensions, fontPixelHeight);
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
        ImmutableArray<GibsPreviewRulerLabel> labels = CreateLabels(dimensions, metrics, text);

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
            metrics.FontPixelHeight,
            horizontalTicks,
            verticalTicks,
            labels);
    }

    private static int CreateFontPixelHeight(GibsPreviewDimensions dimensions) =>
        Scale(Math.Min(dimensions.PixelWidth, dimensions.PixelHeight), divisor: 16, minimum: 16);

    private static RulerMetrics CreateMetrics(GibsPreviewDimensions dimensions, int fontPixelHeight)
    {
        int shorterSide = Math.Min(dimensions.PixelWidth, dimensions.PixelHeight);
        int lineWidth = Scale(shorterSide, divisor: 1_000, minimum: 1);
        int outlineWidth = Scale(shorterSide, divisor: 900, minimum: 1);
        int margin = Scale(shorterSide, divisor: 80, minimum: 1);
        int labelGap = Scale(shorterSide, divisor: 170, minimum: 1);
        int minorTickLength = Scale(shorterSide, divisor: 120, minimum: 1);
        int majorTickLength = Scale(shorterSide, divisor: 65, minimum: 2);
        int axisX = Math.Min(
            dimensions.PixelWidth - 1,
            margin + fontPixelHeight + labelGap + outlineWidth);
        int axisRight = Math.Max(axisX, dimensions.PixelWidth - 1 - margin);
        int axisY = Math.Max(
            val1: 0,
            dimensions.PixelHeight - 1 - margin - fontPixelHeight - labelGap - outlineWidth);
        int axisTop = Math.Min(axisY, margin + fontPixelHeight / 2 + outlineWidth);
        return new(
            fontPixelHeight,
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
        RulerMetrics metrics,
        RulerText text)
    {
        ImmutableArray<GibsPreviewRulerLabel>.Builder labels = ImmutableArray.CreateBuilder<GibsPreviewRulerLabel>();
        AddRequiredLabels(
            labels,
            dimensions,
            metrics,
            text);
        AddIntervalLabels(
            labels,
            GibsPreviewRulerAxis.Horizontal,
            dimensions.WidthKilometers,
            text,
            metrics.FontPixelHeight,
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
            text,
            metrics.FontPixelHeight,
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

            int fontPixelHeight = CreateFontPixelHeight(dimensions);
            using var font = new TrueTypeFont();
            var text = new RulerText(font, fontPixelHeight);
            GibsPreviewRulerLayout layout = CreateLayout(dimensions, fontPixelHeight, text);
            DrawLines(image.Data, layout, s_black, layout.OutlineWidth);
            DrawLines(image.Data, layout, s_white, padding: 0);
            foreach (GibsPreviewRulerLabel label in layout.Labels)
                DrawLabel(image.Data, layout, label, text);

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
        RulerMetrics metrics,
        RulerText text)
    {
        AddEndpointLabels(labels, dimensions, metrics, text);
        AddOriginAndUnitLabels(labels, dimensions, metrics, text);
    }

    private static void AddEndpointLabels(
        ImmutableArray<GibsPreviewRulerLabel>.Builder labels,
        GibsPreviewDimensions dimensions,
        RulerMetrics metrics,
        RulerText text)
    {
        string widthText = FormatCoverage(dimensions.WidthKilometers);
        string heightText = FormatCoverage(dimensions.HeightKilometers);
        TrueTypeTextMask widthMask = text.GetMask(widthText);
        TrueTypeTextMask heightMask = text.GetMask(heightText);
        int widthEndpointX = ClampLabelStart(
            metrics.AxisRight - widthMask.Width / 2,
            widthMask.Width,
            metrics.Margin,
            dimensions.PixelWidth);
        int bottomLabelY = Math.Max(
            metrics.Margin,
            metrics.AxisY - metrics.MajorTickLength - metrics.LabelGap - widthMask.Height);
        labels.Add(CreateLabel(
            widthText,
            GibsPreviewRulerAxis.Horizontal,
            GibsPreviewRulerLabelKind.Endpoint,
            dimensions.WidthKilometers,
            widthEndpointX,
            bottomLabelY,
            text,
            isVertical: false));

        int heightEndpointY = ClampLabelStart(
            metrics.AxisTop - heightMask.Height / 2,
            heightMask.Height,
            metrics.Margin,
            dimensions.PixelHeight);
        labels.Add(CreateLabel(
            heightText,
            GibsPreviewRulerAxis.Vertical,
            GibsPreviewRulerLabelKind.Endpoint,
            dimensions.HeightKilometers,
            Math.Min(
                dimensions.PixelWidth - heightMask.Width,
                metrics.AxisX + metrics.MajorTickLength + metrics.LabelGap),
            heightEndpointY,
            text,
            isVertical: false));
    }

    private static void AddOriginAndUnitLabels(
        ImmutableArray<GibsPreviewRulerLabel>.Builder labels,
        GibsPreviewDimensions dimensions,
        RulerMetrics metrics,
        RulerText text)
    {
        TrueTypeTextMask originMask = text.GetMask(text: "0");
        labels.Add(CreateLabel(
            text: "0",
            GibsPreviewRulerAxis.Horizontal,
            GibsPreviewRulerLabelKind.Origin,
            kilometer: 0,
            Math.Min(
                dimensions.PixelWidth - originMask.Width,
                metrics.AxisX + metrics.LineWidth + metrics.LabelGap + metrics.OutlineWidth),
            Math.Min(
                dimensions.PixelHeight - originMask.Height,
                metrics.AxisY + metrics.LineWidth + metrics.LabelGap + metrics.OutlineWidth),
            text,
            isVertical: false));

        TrueTypeTextMask unitMask = text.GetMask(text: "km");
        labels.Add(CreateLabel(
            text: "km",
            GibsPreviewRulerAxis.Horizontal,
            GibsPreviewRulerLabelKind.Unit,
            kilometer: null,
            Math.Max(metrics.Margin, metrics.AxisRight - unitMask.Width),
            Math.Min(
                dimensions.PixelHeight - unitMask.Height,
                metrics.AxisY + metrics.LineWidth + metrics.LabelGap + metrics.OutlineWidth),
            text,
            isVertical: false));
        labels.Add(CreateLabel(
            text: "km",
            GibsPreviewRulerAxis.Vertical,
            GibsPreviewRulerLabelKind.Unit,
            kilometer: null,
            metrics.Margin,
            Math.Min(
                dimensions.PixelHeight - unitMask.Width,
                metrics.AxisTop + metrics.MajorTickLength + metrics.LabelGap),
            text,
            isVertical: true));
    }

    private static void AddIntervalLabels(
        ImmutableArray<GibsPreviewRulerLabel>.Builder labels,
        GibsPreviewRulerAxis axis,
        double coverageKilometers,
        RulerText textRenderer,
        int fontPixelHeight,
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

        int clearance = Math.Max(
            outlineWidth * 2,
            Scale(fontPixelHeight, divisor: 16, minimum: 1));
        for (int interval = 1; interval <= intervalCount; interval++)
        {
            double kilometer = interval * 10;
            if (kilometer >= coverageKilometers)
                break;

            string labelText = interval.ToString(CultureInfo.InvariantCulture) + "0";
            TrueTypeTextMask mask = textRenderer.GetMask(labelText);
            int coordinate = MapCoordinate(kilometer, coverageKilometers, start, end);
            GibsPreviewRulerLabel candidate = axis switch
            {
                GibsPreviewRulerAxis.Horizontal => CreateLabel(
                    labelText,
                    axis,
                    GibsPreviewRulerLabelKind.Interval,
                    kilometer,
                    ClampLabelStart(coordinate - mask.Width / 2, mask.Width, margin, imageWidth),
                    Math.Max(margin, perpendicularAxis - majorTickLength - labelGap - mask.Height),
                    textRenderer,
                    isVertical: false),
                GibsPreviewRulerAxis.Vertical => CreateLabel(
                    labelText,
                    axis,
                    GibsPreviewRulerLabelKind.Interval,
                    kilometer,
                    Math.Min(imageWidth - mask.Width, perpendicularAxis + majorTickLength + labelGap),
                    ClampLabelStart(coordinate - mask.Height / 2, mask.Height, margin, imageHeight),
                    textRenderer,
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
        RulerText textRenderer,
        bool isVertical)
    {
        TrueTypeTextMask mask = textRenderer.GetMask(text);
        PixelRectangle bounds = isVertical
            ? new PixelRectangle(x, y, mask.Height, mask.Width)
            : new PixelRectangle(x, y, mask.Width, mask.Height);
        return new(text, axis, kind, kilometer, bounds, isVertical);
    }

    private static string FormatCoverage(double value) =>
        value.ToString(format: "0.##", CultureInfo.InvariantCulture);

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
        GibsPreviewRulerLabel label,
        RulerText text)
    {
        TrueTypeTextMask mask = text.GetMask(label.Text);
        int outlineRadius = layout.OutlineWidth;
        int outlineLimit = outlineRadius * outlineRadius + outlineRadius;
        for (int offsetY = -outlineRadius; offsetY <= outlineRadius; offsetY++)
        {
            for (int offsetX = -outlineRadius; offsetX <= outlineRadius; offsetX++)
            {
                if (offsetX * offsetX + offsetY * offsetY <= outlineLimit)
                    DrawTextMask(pixels, layout, label, mask, offsetX, offsetY, s_textOutline);
            }
        }

        DrawTextMask(pixels, layout, label, mask, offsetX: 0, offsetY: 0, s_white);
    }

    private static void DrawTextMask(
        byte[] pixels,
        GibsPreviewRulerLayout layout,
        GibsPreviewRulerLabel label,
        TrueTypeTextMask mask,
        int offsetX,
        int offsetY,
        PixelColor color)
    {
        for (int sourceY = 0; sourceY < mask.Height; sourceY++)
        {
            int sourceOffset = sourceY * mask.Width;
            for (int sourceX = 0; sourceX < mask.Width; sourceX++)
            {
                byte coverage = mask.Pixels[sourceOffset + sourceX];
                if (coverage == 0)
                    continue;

                int x = label.IsVertical
                    ? label.Bounds.X + mask.Height - 1 - sourceY
                    : label.Bounds.X + sourceX;
                int y = label.IsVertical
                    ? label.Bounds.Y + sourceX
                    : label.Bounds.Y + sourceY;
                CompositePixel(
                    pixels,
                    layout.PixelWidth,
                    layout.PixelHeight,
                    x + offsetX,
                    y + offsetY,
                    color,
                    coverage);
            }
        }
    }

    private static void CompositePixel(
        byte[] pixels,
        int pixelWidth,
        int pixelHeight,
        int x,
        int y,
        PixelColor color,
        byte coverage)
    {
        if (x < 0 || x >= pixelWidth || y < 0 || y >= pixelHeight)
            return;

        int offset = (y * pixelWidth + x) * 4;
        int sourceAlpha = (color.Alpha * coverage + 127) / byte.MaxValue;
        int destinationAlpha = pixels[offset + 3];
        int inverseSourceAlpha = byte.MaxValue - sourceAlpha;
        int destinationContribution = (destinationAlpha * inverseSourceAlpha + 127) / byte.MaxValue;
        int outputAlpha = sourceAlpha + destinationContribution;
        for (int channel = 0; channel < 3; channel++)
        {
            int sourceValue = channel switch
            {
                0 => color.Red,
                1 => color.Green,
                _ => color.Blue
            };
            int premultipliedValue = sourceValue * sourceAlpha
                + (pixels[offset + channel] * destinationAlpha * inverseSourceAlpha + 127) / byte.MaxValue;
            pixels[offset + channel] = (byte)((premultipliedValue + outputAlpha / 2) / outputAlpha);
        }

        pixels[offset + 3] = (byte)outputAlpha;
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

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct PixelColor(byte Red, byte Green, byte Blue, byte Alpha);

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct RulerMetrics(
        int FontPixelHeight,
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

    private sealed class RulerText
    {
        private readonly TrueTypeFont _font;
        private readonly int _pixelHeight;
        private readonly Dictionary<string, TrueTypeTextMask> _masks = new(StringComparer.Ordinal);

        public RulerText(TrueTypeFont font, int pixelHeight)
        {
            _font = font;
            _pixelHeight = pixelHeight;
        }

        public TrueTypeTextMask GetMask(string text)
        {
            if (!_masks.TryGetValue(text, out TrueTypeTextMask mask))
            {
                mask = _font.Rasterize(text, _pixelHeight);
                _masks.Add(text, mask);
            }

            return mask;
        }
    }

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
        int FontPixelHeight,
        ImmutableArray<GibsPreviewRulerTick> HorizontalTicks,
        ImmutableArray<GibsPreviewRulerTick> VerticalTicks,
        ImmutableArray<GibsPreviewRulerLabel> Labels);
}
