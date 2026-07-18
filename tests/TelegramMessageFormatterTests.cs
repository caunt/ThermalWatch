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
            true,
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

    private static Anomaly Detection(string id, string satellite) =>
        new(
            id,
            "UKR",
            "VIIRS_SNPP_NRT",
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
