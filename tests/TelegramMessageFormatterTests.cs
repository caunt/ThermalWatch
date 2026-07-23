using ThermalWatch.Core;
using ThermalWatch.Telegram;

namespace ThermalWatch.Tests;

public sealed class TelegramMessageFormatterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Format_ShowsCoordinatesWithoutVisibleGoogleMapsLink(bool multiSatellite)
    {
        var first = Detection("first", "Suomi-NPP");
        var members = multiSatellite
            ? new[] { first, Detection("second", "NOAA-20") }
            : new[] { first };
        var cluster = new NotificationCluster("cluster", first, [.. members]);

        var caption = TelegramMessageFormatter.Format(
            cluster,
            new GibsPreview([1], new("VIIRS_SNPP_NRT", "Suomi-NPP", "VIIRS")),
            new(30, 20, 900, 600),
            1,
            "Vegetation · 20%");

        Assert.Contains("50.123456, 30.654321", caption);
        Assert.DoesNotContain("Open in Google Maps", caption);
        Assert.DoesNotContain("View location", caption);
        Assert.DoesNotContain("<a href=", caption);
        Assert.DoesNotContain(first.GoogleMapsUrl, caption);
    }

    [Fact]
    public void CreateLocationKeyboard_KeepsGoogleMapsButton()
    {
        var detection = Detection("keyboard", "Suomi-NPP");
        var cluster = new NotificationCluster("cluster", detection, [detection]);

        var keyboard = TelegramNotificationService.CreateLocationKeyboard(cluster);
        var button = Assert.Single(Assert.Single(keyboard.InlineKeyboard));

        Assert.Equal("🗺 Open in Google Maps", button.Text);
        Assert.Equal(detection.GoogleMapsUrl, button.Url);
    }

    [Theory]
    [InlineData(false, "🖼 <b>Imagery:</b> Aqua &#183; MODIS base &#183; Suomi-NPP &#183; VIIRS thermal overlay · 2026-07-19")]
    [InlineData(true, "🖼 <b>Preview:</b> Aqua &#183; MODIS base &#183; Suomi-NPP &#183; VIIRS thermal overlay for 2026-07-19")]
    public void Format_NamesFallbackBaseAndRepresentativeThermalOverlay(
        bool multiSatellite,
        string expectedPreviewLine)
    {
        var representative = Detection("first", "Suomi-NPP");
        var members = multiSatellite
            ? new[] { representative, Detection("second", "NOAA-20") }
            : new[] { representative };
        var cluster = new NotificationCluster("cluster", representative, [.. members]);

        var caption = TelegramMessageFormatter.Format(
            cluster,
            new GibsPreview([1], new("MODIS_NRT", "Aqua", "MODIS")),
            new(30, 20, 900, 600),
            1,
            "Vegetation · 20%");

        Assert.Contains(expectedPreviewLine, caption);
    }

    [Theory]
    [InlineData(false, "🖼 <b>Imagery:</b> Sensor-matched · 2026-07-19")]
    [InlineData(true, "🖼 <b>Preview:</b> Suomi-NPP &#183; VIIRS imagery for 2026-07-19")]
    public void Format_PreservesMatchedPreviewWording(bool multiSatellite, string expectedPreviewLine)
    {
        var representative = Detection("first", "Suomi-NPP");
        var members = multiSatellite
            ? new[] { representative, Detection("second", "NOAA-20") }
            : new[] { representative };
        var cluster = new NotificationCluster("cluster", representative, [.. members]);

        var caption = TelegramMessageFormatter.Format(
            cluster,
            new GibsPreview([1], new("VIIRS_SNPP_NRT", "Suomi-NPP", "VIIRS")),
            new(30, 20, 900, 600),
            1,
            "Vegetation · 20%");

        Assert.Contains(expectedPreviewLine, caption);
    }

    private static Anomaly Detection(string id, string satellite) =>
        new(
            id,
            "UKR",
            satellite == "NOAA-20" ? "VIIRS_NOAA20_NRT" : "VIIRS_SNPP_NRT",
            satellite,
            "VIIRS",
            50.123456,
            30.654321,
            new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero),
            "D",
            330,
            300,
            100,
            0.4,
            0.4,
            "n",
            null,
            "nominal",
            "2.0NRT",
            $"https://www.google.com/maps?q=50.123456,30.654321&id={id}");
}
