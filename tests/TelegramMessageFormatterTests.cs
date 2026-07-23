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
            landCoverSummary: "Vegetation · 20%",
            nearbyFeatures: []);

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
                    "https://yandex.com/maps/?ll=30.654321%2C50.123456&pt=30.654321%2C50.123456&z=12&l=sat",
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
            landCoverSummary: "Vegetation · 20%",
            nearbyFeatures: []);

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
            landCoverSummary: "Vegetation · 20%",
            nearbyFeatures: []);

        Assert.Contains(expectedPreviewLine, caption, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "🛰 <b>Satellite:</b>")]
    [InlineData(true, "🛰 <b>Satellites:</b>")]
    public void FormatShowsEveryNearbyFeatureAsMonospaceBeforeSatellites(
        bool multiSatellite,
        string satelliteHeading)
    {
        Anomaly representative = Detection(id: "first", satellite: "Suomi-NPP");
        Anomaly[] members = multiSatellite
            ? [representative, Detection(id: "second", satellite: "NOAA-20")]
            : [representative];
        var cluster = new NotificationCluster(Id: "cluster", representative, [.. members]);
        NearbyFeature[] nearbyFeatures =
        [
            Feature(id: 1, name: "Factory & Sons", distanceKilometers: 0.12),
            Feature(id: 2, name: "Fuel depot", distanceKilometers: 0.4),
            Feature(id: 3, name: "Workshop", distanceKilometers: 0.75),
            Feature(id: 4, name: "Rail terminal", distanceKilometers: 1.25),
            Feature(id: 5, name: "Power station", distanceKilometers: 1.9)
        ];

        string caption = TelegramMessageFormatter.Format(
            cluster,
            GibsPreview.Unavailable,
            new(WidthKilometers: 30, HeightKilometers: 20, PixelWidth: 900, PixelHeight: 600),
            clusterDiameterKilometers: 0,
            landCoverSummary: null,
            nearbyFeatures);

        Assert.Contains("<b>Possible nearby sources:</b>", caption, StringComparison.Ordinal);
        Assert.Contains("<code>0.12 km • Factory &amp; Sons</code>", caption, StringComparison.Ordinal);
        Assert.Contains("<code>1.9 km • Power station</code>", caption, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenStreetMap contributors", caption, StringComparison.Ordinal);
        Assert.DoesNotContain("Mapped proximity does not establish cause.", caption, StringComparison.Ordinal);
        Assert.True(
            caption.IndexOf(value: "<b>Possible nearby sources:</b>", StringComparison.Ordinal)
            < caption.IndexOf(value: satelliteHeading, StringComparison.Ordinal));
        Assert.Equal(5, caption.Split('\n').Count(line => line.StartsWith(value: "<code>", StringComparison.Ordinal)));
        Assert.InRange(caption.Length, low: 1, high: 1024);
    }

    [Fact]
    public void FormatKeepsFiveNearbyFeaturesWithinPhotoCaptionLimitWhenNamesAreLong()
    {
        Anomaly representative = Detection(id: "first", satellite: "Suomi-NPP");
        var cluster = new NotificationCluster(Id: "cluster", representative, [representative]);
        string longName = string.Concat(Enumerable.Repeat(element: "&", count: 300));
        NearbyFeature[] nearbyFeatures =
        [
            Feature(id: 1, name: longName, distanceKilometers: 0.1),
            Feature(id: 2, name: longName, distanceKilometers: 0.2),
            Feature(id: 3, name: longName, distanceKilometers: 0.3),
            Feature(id: 4, name: longName, distanceKilometers: 0.4),
            Feature(id: 5, name: longName, distanceKilometers: 0.5)
        ];

        string caption = TelegramMessageFormatter.Format(
            cluster,
            new GibsPreview([1], new(FirmsSource: "VIIRS_SNPP_NRT", Satellite: "Suomi-NPP", "VIIRS")),
            new(WidthKilometers: 30, HeightKilometers: 20, PixelWidth: 900, PixelHeight: 600),
            clusterDiameterKilometers: 1,
            landCoverSummary: "Vegetation · 20%",
            nearbyFeatures);

        Assert.InRange(caption.Length, low: 1, high: 1024);
        Assert.Equal(5, caption.Split('\n').Count(line => line.StartsWith(value: "<code>", StringComparison.Ordinal)));
        Assert.DoesNotContain("Mapped proximity does not establish cause.", caption, StringComparison.Ordinal);
    }

    private static NearbyFeature Feature(long id, string name, double distanceKilometers) =>
        new(
            OsmType: "node",
            id,
            name,
            Latitude: 50.123,
            Longitude: 30.654,
            distanceKilometers,
            OpenStreetMapUrl: $"https://www.openstreetmap.org/node/{id}");

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
