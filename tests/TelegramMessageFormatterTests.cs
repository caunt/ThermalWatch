using Telegram.Bot.Types.ReplyMarkups;
using ThermalWatch.Core;
using ThermalWatch.Telegram;

namespace ThermalWatch.Tests;

public sealed class TelegramMessageFormatterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FormatShowsCoordinatesWithoutVisibleGoogleMapsLink(bool multiSatellite)
    {
        Anomaly first = Detection(id: "first", satellite: "Suomi-NPP");
        Anomaly[] members = multiSatellite
            ? [first, Detection(id: "second", satellite: "NOAA-20")]
            : [first];
        var cluster = new NotificationCluster(Id: "cluster", first, [.. members]);

        string caption = TelegramMessageFormatter.Format(
            cluster,
            new GibsPreview([1], new(FirmsSource: "VIIRS_SNPP_NRT", Satellite: "Suomi-NPP", "VIIRS")),
            new(WidthKilometers: 30, HeightKilometers: 20, PixelWidth: 900, PixelHeight: 600),
            clusterDiameterKilometers: 1,
            landCoverSummary: "Vegetation · 20%");

        Assert.Contains("50.123456, 30.654321", caption, StringComparison.Ordinal);
        Assert.DoesNotContain("Open in Google Maps", caption, StringComparison.Ordinal);
        Assert.DoesNotContain("View location", caption, StringComparison.Ordinal);
        Assert.DoesNotContain("<a href=", caption, StringComparison.Ordinal);
        Assert.DoesNotContain(first.GoogleMapsUrl, caption, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Automated satellite detection; event type is not confirmed.",
            caption,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Satellite-detected thermal anomaly; not a confirmed fire.",
            caption,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CreateLocationKeyboardAddsGoogleAndYandexMapsButtons()
    {
        Anomaly detection = Detection(id: "keyboard", satellite: "Suomi-NPP");
        var cluster = new NotificationCluster(Id: "cluster", detection, [detection]);

        InlineKeyboardMarkup keyboard = TelegramNotificationService.CreateLocationKeyboard(cluster);
        IEnumerable<InlineKeyboardButton> row = Assert.Single(keyboard.InlineKeyboard);

        Assert.Collection(
            row,
            googleButton =>
            {
                Assert.Equal("🗺 Google Maps", googleButton.Text);
                Assert.Equal(detection.GoogleMapsUrl, googleButton.Url);
            },
            yandexButton =>
            {
                Assert.Equal("🗺 Yandex Maps", yandexButton.Text);
                Assert.Equal(
                    "https://yandex.com/maps/?ll=30.654321%2C50.123456&pt=30.654321%2C50.123456&z=12&l=map",
                    yandexButton.Url);
            });
    }

    [Theory]
    [InlineData(false, "🖼 <b>Imagery:</b> Aqua &#183; MODIS base &#183; Suomi-NPP &#183; VIIRS thermal overlay · 2026-07-19")]
    [InlineData(true, "🖼 <b>Preview:</b> Aqua &#183; MODIS base &#183; Suomi-NPP &#183; VIIRS thermal overlay for 2026-07-19")]
    public void FormatNamesFallbackBaseAndRepresentativeThermalOverlay(
        bool multiSatellite,
        string expectedPreviewLine)
    {
        Anomaly representative = Detection(id: "first", satellite: "Suomi-NPP");
        Anomaly[] members = multiSatellite
            ? [representative, Detection(id: "second", satellite: "NOAA-20")]
            : [representative];
        var cluster = new NotificationCluster(Id: "cluster", representative, [.. members]);

        string caption = TelegramMessageFormatter.Format(
            cluster,
            new GibsPreview([1], new(FirmsSource: "MODIS_NRT", Satellite: "Aqua", "MODIS")),
            new(WidthKilometers: 30, HeightKilometers: 20, PixelWidth: 900, PixelHeight: 600),
            clusterDiameterKilometers: 1,
            landCoverSummary: "Vegetation · 20%");

        Assert.Contains(expectedPreviewLine, caption, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "🖼 <b>Imagery:</b> Sensor-matched · 2026-07-19")]
    [InlineData(true, "🖼 <b>Preview:</b> Suomi-NPP &#183; VIIRS imagery for 2026-07-19")]
    public void FormatPreservesMatchedPreviewWording(bool multiSatellite, string expectedPreviewLine)
    {
        Anomaly representative = Detection(id: "first", satellite: "Suomi-NPP");
        Anomaly[] members = multiSatellite
            ? [representative, Detection(id: "second", satellite: "NOAA-20")]
            : [representative];
        var cluster = new NotificationCluster(Id: "cluster", representative, [.. members]);

        string caption = TelegramMessageFormatter.Format(
            cluster,
            new GibsPreview([1], new(FirmsSource: "VIIRS_SNPP_NRT", Satellite: "Suomi-NPP", "VIIRS")),
            new(WidthKilometers: 30, HeightKilometers: 20, PixelWidth: 900, PixelHeight: 600),
            clusterDiameterKilometers: 1,
            landCoverSummary: "Vegetation · 20%");

        Assert.Contains(expectedPreviewLine, caption, StringComparison.Ordinal);
    }

    private static Anomaly Detection(string id, string satellite) =>
        new(
            id,
            CountryCode: "UKR",
            "NOAA-20".Equals(satellite, StringComparison.Ordinal) ? "VIIRS_NOAA20_NRT" : "VIIRS_SNPP_NRT",
            satellite,
            Instrument: "VIIRS",
            Latitude: 50.123456,
            Longitude: 30.654321,
            new(year: 2026, month: 7, day: 19, hour: 12, minute: 0, second: 0, TimeSpan.Zero),
            DayNight: "D",
            BrightnessKelvin: 330,
            SecondaryBrightnessKelvin: 300,
            FrpMegawatts: 100,
            ScanKilometers: 0.4,
            TrackKilometers: 0.4,
            ConfidenceRaw: "n",
            ConfidencePercent: null,
            ConfidenceCategory: "nominal",
            Version: "2.0NRT",
            GoogleMapsUrl: $"https://www.google.com/maps?q=50.123456,30.654321&id={id}");
}
