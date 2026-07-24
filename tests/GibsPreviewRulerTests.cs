using StbImageSharp;
using ThermalWatch.Core;
using static ThermalWatch.Core.GibsPreviewRuler;

namespace ThermalWatch.Tests;

public sealed class GibsPreviewRulerTests
{
    [Fact]
    public void CreateLayoutMeetsPortraitAcceptanceGeometry()
    {
        GibsPreviewRulerLayout layout = CreateLayout(new(
            WidthKilometers: 48,
            HeightKilometers: 60,
            PixelWidth: 3072,
            PixelHeight: 3840));

        Assert.Equal(
            ["0", "10", "20", "30", "43.48"],
            AxisLabels(layout, GibsPreviewRulerAxis.Horizontal));
        Assert.Equal(
            ["10", "20", "30", "40", "50", "53.94"],
            AxisLabels(layout, GibsPreviewRulerAxis.Vertical));
        Assert.Equal(
            Enumerable.Range(start: 1, count: 43).Select(value => (double)value),
            layout.HorizontalTicks[..^1].Select(tick => tick.Kilometer));
        Assert.Equal(
            Enumerable.Range(start: 1, count: 53).Select(value => (double)value),
            layout.VerticalTicks[..^1].Select(tick => tick.Kilometer));
        Assert.All(
            layout.HorizontalTicks.Where(tick => tick.Kilometer % 10 == 0 || tick == layout.HorizontalTicks[^1]),
            tick => Assert.True(tick.IsMajor));
        Assert.All(
            layout.VerticalTicks.Where(tick => tick.Kilometer % 10 == 0 || tick == layout.VerticalTicks[^1]),
            tick => Assert.True(tick.IsMajor));
        GibsPreviewRulerLabel origin = Assert.Single(
            layout.Labels,
            label => label.Kind == GibsPreviewRulerLabelKind.Origin);
        GibsPreviewRulerLabel horizontalUnit = Assert.Single(
            layout.Labels,
            label => label.Kind == GibsPreviewRulerLabelKind.Unit
                && label.Axis == GibsPreviewRulerAxis.Horizontal);
        GibsPreviewRulerLabel verticalUnit = Assert.Single(
            layout.Labels,
            label => label.Kind == GibsPreviewRulerLabelKind.Unit
                && label.Axis == GibsPreviewRulerAxis.Vertical);
        Assert.True(origin.Bounds.X > layout.AxisX);
        Assert.True(origin.Bounds.Y > layout.AxisY);
        Assert.True(horizontalUnit.Bounds.Y > layout.AxisY);
        Assert.True(verticalUnit.Bounds.Right < layout.AxisX);
        AssertLabelBoundsDoNotCollide(layout);
    }

    [Theory]
    [InlineData(48, 60, 768, 960)]
    [InlineData(60, 40, 960, 540)]
    [InlineData(50, 50, 720, 720)]
    [InlineData(60, 40, 320, 240)]
    [InlineData(120, 80, 4096, 2160)]
    public void CreateLayoutSupportsRepresentativeShapesAndSizes(
        double widthKilometers,
        double heightKilometers,
        int pixelWidth,
        int pixelHeight)
    {
        GibsPreviewRulerLayout layout = CreateLayout(new(
            widthKilometers,
            heightKilometers,
            pixelWidth,
            pixelHeight));

        AssertMandatoryLabels(layout, widthKilometers, heightKilometers, pixelWidth, pixelHeight);
        Assert.All(layout.Labels, label => AssertBoundsInsideImage(label.Bounds, pixelWidth, pixelHeight));
        AssertLabelBoundsDoNotCollide(layout);
    }

    [Theory]
    [InlineData(48, 60, 768, 960)]
    [InlineData(60, 40, 960, 540)]
    [InlineData(50, 50, 720, 720)]
    [InlineData(48.5, 60.25, 1200, 1500)]
    public void CreateLayoutMapsImageScaleAcrossInsetAxes(
        double widthKilometers,
        double heightKilometers,
        int pixelWidth,
        int pixelHeight)
    {
        GibsPreviewRulerLayout layout = CreateLayout(new(
            widthKilometers,
            heightKilometers,
            pixelWidth,
            pixelHeight));

        Assert.True(layout.AxisX > 0);
        Assert.True(layout.AxisRight < pixelWidth - 1);
        Assert.True(layout.AxisTop > 0);
        Assert.True(layout.AxisY < pixelHeight - 1);
        double horizontalCoverageKilometers = ExpectedAxisCoverage(
            widthKilometers,
            pixelWidth,
            layout.AxisX,
            layout.AxisRight);
        double verticalCoverageKilometers = ExpectedAxisCoverage(
            heightKilometers,
            pixelHeight,
            layout.AxisY,
            layout.AxisTop);
        Assert.True(horizontalCoverageKilometers < widthKilometers);
        Assert.True(verticalCoverageKilometers < heightKilometers);
        Assert.Equal(
            layout.AxisX,
            ExpectedCoordinate(kilometer: 0, horizontalCoverageKilometers, layout.AxisX, layout.AxisRight));
        Assert.Equal(
            layout.AxisY,
            ExpectedCoordinate(kilometer: 0, verticalCoverageKilometers, layout.AxisY, layout.AxisTop));
        AssertAxisCoordinates(
            layout.HorizontalTicks,
            widthKilometers,
            pixelWidth,
            layout.AxisX,
            direction: 1);
        AssertAxisCoordinates(
            layout.VerticalTicks,
            heightKilometers,
            pixelHeight,
            layout.AxisY,
            direction: -1);
        AssertApproximatelyEqual(horizontalCoverageKilometers, layout.HorizontalTicks[^1].Kilometer);
        AssertApproximatelyEqual(verticalCoverageKilometers, layout.VerticalTicks[^1].Kilometer);
        Assert.Equal(
            layout.AxisRight,
            layout.HorizontalTicks[^1].Coordinate);
        Assert.Equal(
            layout.AxisTop,
            layout.VerticalTicks[^1].Coordinate);
    }

    [Fact]
    public void CreateLayoutLabelsExactInsetEndpointsWithoutDuplicatingIntervals()
    {
        const double widthKilometers = 48.5;
        const double heightKilometers = 60;
        const int pixelWidth = 1200;
        const int pixelHeight = 1500;
        GibsPreviewRulerLayout layout = CreateLayout(new(
            widthKilometers,
            heightKilometers,
            pixelWidth,
            pixelHeight));
        double horizontalCoverageKilometers = ExpectedAxisCoverage(
            widthKilometers,
            pixelWidth,
            layout.AxisX,
            layout.AxisRight);
        double verticalCoverageKilometers = ExpectedAxisCoverage(
            heightKilometers,
            pixelHeight,
            layout.AxisY,
            layout.AxisTop);

        Assert.Single(
            layout.Labels,
            label => label.Kind == GibsPreviewRulerLabelKind.Endpoint
                && label.Axis == GibsPreviewRulerAxis.Horizontal
                && label.Text.Equals(FormatCoverage(horizontalCoverageKilometers), StringComparison.Ordinal));
        Assert.Single(
            layout.Labels,
            label => label.Kind == GibsPreviewRulerLabelKind.Endpoint
                && label.Axis == GibsPreviewRulerAxis.Vertical
                && label.Text.Equals(FormatCoverage(verticalCoverageKilometers), StringComparison.Ordinal));
        Assert.Equal(1, layout.Labels.Count(label => label.Text.Equals(value: "0", StringComparison.Ordinal)));
        Assert.Equal(2, layout.Labels.Count(label => label.Text.Equals(value: "km", StringComparison.Ordinal)));
        AssertLabelBoundsDoNotCollide(layout);
    }

    [Fact]
    public void CreateLayoutKeepsRequiredLabelsWhenIntervalsAreTooDense()
    {
        GibsPreviewRulerLayout layout = CreateLayout(new(
            WidthKilometers: 1_000,
            HeightKilometers: 800,
            PixelWidth: 320,
            PixelHeight: 240));

        AssertMandatoryLabels(
            layout,
            widthKilometers: 1_000,
            heightKilometers: 800,
            pixelWidth: 320,
            pixelHeight: 240);
        Assert.True(layout.Labels.Count(label => label.Kind == GibsPreviewRulerLabelKind.Interval) < 178);
        AssertLabelBoundsDoNotCollide(layout);
    }

    [Fact]
    public void CreateLayoutScalesStyleFromShorterSide()
    {
        GibsPreviewRulerLayout small = CreateLayout(new(
            WidthKilometers: 48,
            HeightKilometers: 60,
            PixelWidth: 320,
            PixelHeight: 480));
        GibsPreviewRulerLayout large = CreateLayout(new(
            WidthKilometers: 48,
            HeightKilometers: 60,
            PixelWidth: 3200,
            PixelHeight: 4800));

        Assert.Equal(15, small.FontPixelHeight);
        Assert.Equal(150, large.FontPixelHeight);
        Assert.True(large.FontPixelHeight > small.FontPixelHeight);
        Assert.True(large.LineWidth > small.LineWidth);
        Assert.True(large.OutlineWidth > small.OutlineWidth);
        Assert.True(large.MinorTickLength > small.MinorTickLength);
        Assert.True(large.MajorTickLength > small.MajorTickLength);
    }

    [Fact]
    public void CreateLayoutPreservesReferenceScaleAxisPlacementWithSmallerText()
    {
        GibsPreviewRulerLayout layout = CreateLayout(new(
            WidthKilometers: 48,
            HeightKilometers: 60,
            PixelWidth: 768,
            PixelHeight: 960));

        Assert.Equal(64, layout.AxisX);
        Assert.Equal(757, layout.AxisRight);
        Assert.Equal(895, layout.AxisY);
        Assert.Equal(35, layout.AxisTop);
        Assert.Equal(36, layout.FontPixelHeight);
    }

    [Theory]
    [InlineData("portrait", 48, 60, 768, 960)]
    [InlineData("landscape", 60, 40, 960, 540)]
    [InlineData("square", 50, 50, 720, 720)]
    [InlineData("small", 60, 40, 320, 240)]
    [InlineData("large", 120, 80, 1600, 900)]
    [InlineData("acceptance-48x60", 48, 60, 3072, 3840)]
    public void RenderProducesValidLosslessPngForRepresentativeShapes(
        string artifactName,
        double widthKilometers,
        double heightKilometers,
        int pixelWidth,
        int pixelHeight)
    {
        const byte sourceRed = 24;
        const byte sourceGreen = 72;
        const byte sourceBlue = 48;
        byte[] source = PngTestData.CreateSolidRgba(
            pixelWidth,
            pixelHeight,
            sourceRed,
            sourceGreen,
            sourceBlue,
            alpha: byte.MaxValue);
        var dimensions = new GibsPreviewDimensions(
            widthKilometers,
            heightKilometers,
            pixelWidth,
            pixelHeight);

        byte[] rendered = Assert.IsType<byte[]>(Render(source, dimensions));
        var image = ImageResult.FromMemory(rendered, ColorComponents.RedGreenBlueAlpha);

        Assert.Equal(pixelWidth, image.Width);
        Assert.Equal(pixelHeight, image.Height);
        AssertPixel(image, pixelWidth / 2, pixelHeight / 2, sourceRed, sourceGreen, sourceBlue, byte.MaxValue);
        Assert.Contains(Pixels(image), pixel => pixel == (byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue));
        Assert.Contains(Pixels(image), pixel => pixel == (byte.MinValue, byte.MinValue, byte.MinValue, byte.MaxValue));
        Assert.Contains(
            Pixels(image),
            pixel => pixel.Alpha == byte.MaxValue
                && pixel != (sourceRed, sourceGreen, sourceBlue, byte.MaxValue)
                && pixel != (byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue)
                && pixel != (byte.MinValue, byte.MinValue, byte.MinValue, byte.MaxValue));
        WriteArtifactIfRequested(artifactName, rendered);
    }

    [Fact]
    public void RenderAcceptsRgbInputAndPreservesUntouchedPixels()
    {
        const int pixelWidth = 640;
        const int pixelHeight = 480;
        byte[] source = PngTestData.CreateSolidRgb(
            pixelWidth,
            pixelHeight,
            red: 41,
            green: 83,
            blue: 127);

        byte[] rendered = Assert.IsType<byte[]>(Render(
            source,
            new(
                WidthKilometers: 48,
                HeightKilometers: 60,
                pixelWidth,
                pixelHeight)));
        var image = ImageResult.FromMemory(rendered, ColorComponents.RedGreenBlueAlpha);

        AssertPixel(image, x: 400, y: 200, red: 41, green: 83, blue: 127, alpha: byte.MaxValue);
    }

    [Fact]
    public void RenderRejectsMismatchedPixelDimensions()
    {
        byte[] source = PngTestData.CreateSolidRgba(
            width: 100,
            height: 100,
            red: 0,
            green: 0,
            blue: 0,
            alpha: byte.MaxValue);

        Assert.Null(Render(
            source,
            new(
                WidthKilometers: 48,
                HeightKilometers: 60,
                PixelWidth: 101,
                PixelHeight: 100)));
    }

    private static string[] AxisLabels(
        GibsPreviewRulerLayout layout,
        GibsPreviewRulerAxis axis) =>
        [.. layout.Labels
            .Where(label => label.Axis == axis && label.Kind != GibsPreviewRulerLabelKind.Unit)
            .OrderBy(label => label.Kilometer)
            .Select(label => label.Text)];

    private static void AssertMandatoryLabels(
        GibsPreviewRulerLayout layout,
        double widthKilometers,
        double heightKilometers,
        int pixelWidth,
        int pixelHeight)
    {
        double horizontalCoverageKilometers = ExpectedAxisCoverage(
            widthKilometers,
            pixelWidth,
            layout.AxisX,
            layout.AxisRight);
        double verticalCoverageKilometers = ExpectedAxisCoverage(
            heightKilometers,
            pixelHeight,
            layout.AxisY,
            layout.AxisTop);
        Assert.Single(layout.Labels, label => label.Kind == GibsPreviewRulerLabelKind.Origin);
        Assert.Equal(2, layout.Labels.Count(label => label.Kind == GibsPreviewRulerLabelKind.Unit));
        Assert.Single(
            layout.Labels,
            label => label.Kind == GibsPreviewRulerLabelKind.Endpoint
                && label.Axis == GibsPreviewRulerAxis.Horizontal
                && label.Kilometer == horizontalCoverageKilometers);
        Assert.Single(
            layout.Labels,
            label => label.Kind == GibsPreviewRulerLabelKind.Endpoint
                && label.Axis == GibsPreviewRulerAxis.Vertical
                && label.Kilometer == verticalCoverageKilometers);
    }

    private static void AssertAxisCoordinates(
        IEnumerable<GibsPreviewRulerTick> ticks,
        double imageCoverageKilometers,
        int imagePixelLength,
        int start,
        int direction)
    {
        Assert.All(
            ticks,
            tick => Assert.Equal(
                start + direction * (int)Math.Round(
                    (imagePixelLength - 1) * tick.Kilometer / imageCoverageKilometers,
                    MidpointRounding.AwayFromZero),
                tick.Coordinate));
    }

    private static double ExpectedAxisCoverage(
        double imageCoverageKilometers,
        int imagePixelLength,
        int start,
        int end) =>
        imageCoverageKilometers * Math.Abs(end - start) / (imagePixelLength - 1);

    private static int ExpectedCoordinate(
        double kilometer,
        double coverageKilometers,
        int start,
        int end) =>
        start + (int)Math.Round(
            (end - start) * kilometer / coverageKilometers,
            MidpointRounding.AwayFromZero);

    private static string FormatCoverage(double value) =>
        value.ToString(format: "0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static void AssertApproximatelyEqual(double expected, double actual) =>
        Assert.InRange(Math.Abs(expected - actual), low: 0, high: 0.000000000001);

    private static void AssertLabelBoundsDoNotCollide(GibsPreviewRulerLayout layout)
    {
        for (int first = 0; first < layout.Labels.Length; first++)
        {
            for (int second = first + 1; second < layout.Labels.Length; second++)
            {
                PixelRectangle firstBounds = layout.Labels[first].Bounds.Inflate(layout.OutlineWidth);
                PixelRectangle secondBounds = layout.Labels[second].Bounds.Inflate(layout.OutlineWidth);
                Assert.False(
                    firstBounds.Intersects(secondBounds),
                    $"Labels '{layout.Labels[first].Text}' and '{layout.Labels[second].Text}' overlap.");
            }
        }
    }

    private static void AssertBoundsInsideImage(PixelRectangle bounds, int pixelWidth, int pixelHeight)
    {
        Assert.InRange(bounds.X, low: 0, pixelWidth - 1);
        Assert.InRange(bounds.Y, low: 0, pixelHeight - 1);
        Assert.InRange(bounds.Right, low: 1, pixelWidth);
        Assert.InRange(bounds.Bottom, low: 1, pixelHeight);
    }

    private static IEnumerable<(byte Red, byte Green, byte Blue, byte Alpha)> Pixels(ImageResult image)
    {
        for (int offset = 0; offset < image.Data.Length; offset += 4)
        {
            yield return (
                image.Data[offset],
                image.Data[offset + 1],
                image.Data[offset + 2],
                image.Data[offset + 3]);
        }
    }

    private static void AssertPixel(
        ImageResult image,
        int x,
        int y,
        byte red,
        byte green,
        byte blue,
        byte alpha)
    {
        int offset = (y * image.Width + x) * 4;
        Assert.Equal(red, image.Data[offset]);
        Assert.Equal(green, image.Data[offset + 1]);
        Assert.Equal(blue, image.Data[offset + 2]);
        Assert.Equal(alpha, image.Data[offset + 3]);
    }

    private static void WriteArtifactIfRequested(string artifactName, byte[] pngBytes)
    {
        string? directory = Environment.GetEnvironmentVariable(variable: "THERMALWATCH_RULER_ARTIFACT_DIR");
        if (string.IsNullOrWhiteSpace(directory))
            return;

        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(path1: directory, path2: $"{artifactName}.png"), pngBytes);
    }
}
