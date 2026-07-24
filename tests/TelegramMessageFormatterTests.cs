using ThermalWatch.Core;
using ThermalWatch.Telegram;

namespace ThermalWatch.Tests;

public sealed class TelegramMessageFormatterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FormatMessagesUsesSharedMainTemplateWithLatestObservation(bool multiSatellite)
    {
        Anomaly representative = CreateAnomaly(
            id: "representative",
            satellite: "Suomi-NPP",
            acquiredAtUtc: Utc(hour: 12),
            dayNight: "D",
            frpMegawatts: 100,
            thermalContrastKelvin: 30);
        Anomaly latest = CreateAnomaly(
            id: "latest",
            satellite: multiSatellite ? "NOAA-20" : "Suomi-NPP",
            acquiredAtUtc: Utc(hour: 14, minute: 30),
            dayNight: "N",
            frpMegawatts: 80,
            thermalContrastKelvin: 25);
        var cluster = new NotificationCluster(Id: "cluster", representative, [representative, latest]);
        NearbyFeature[] nearbyFeatures =
        [
            Feature(id: 1, name: "Factory & Sons", distanceKilometers: 0.12),
            Feature(id: 2, name: "Fuel depot", distanceKilometers: 0.4)
        ];

        TelegramNotificationMessages messages = TelegramMessageFormatter.FormatMessages(
            cluster,
            new GibsPreview([1], new(FirmsSource: "VIIRS_SNPP_NRT", Satellite: "Suomi-NPP", "VIIRS")),
            new(WidthKilometers: 30, HeightKilometers: 20, PixelWidth: 900, PixelHeight: 600),
            clusterDiameterKilometers: 1.25,
            landCoverSummary: "Built-up · 80%",
            nearbyFeatures);

        const string expected = """
            🔥 <b>New thermal anomaly</b>

            📍 <b>Ukraine</b>
            🕓 <b>Observed:</b> 2026-07-19 14:30 UTC
            🌙 <b>Pass:</b> Nighttime

            🏷 <b>Possible nearby sources:</b>
            <code>0.12 km • Factory &amp; Sons</code>
            <code>0.40 km • Fuel depot</code>

            ♾️ <b>Diameter:</b> 1.25 km
            📌 <code>50.123456, 30.654321</code>
            🗺 <a href="https://www.google.com/maps?q=50.123456,30.654321&amp;id=representative">Google Maps</a> · <a href="https://yandex.com/maps/?ll=30.654321%2C50.123456&amp;pt=30.654321%2C50.123456&amp;z=12&amp;l=sat">Yandex Maps</a>
            """;
        Assert.Equal(expected, messages.MainMessage);
        Assert.Equal(
            expected,
            TelegramMessageFormatter.Format(
                cluster,
                hasPreview: true,
                new(WidthKilometers: 30, HeightKilometers: 20, PixelWidth: 900, PixelHeight: 600),
                clusterDiameterKilometers: 1.25,
                landCoverSummary: "Built-up · 80%",
                nearbyFeatures));
        Assert.DoesNotContain("Satellite:", messages.MainMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Detections:", messages.MainMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatMessagesBuildsSingleSatelliteComment()
    {
        Anomaly representative = CreateAnomaly(
            id: "representative",
            satellite: "Suomi-NPP",
            acquiredAtUtc: Utc(hour: 12),
            dayNight: "D",
            frpMegawatts: 100,
            thermalContrastKelvin: 30);
        Anomaly latest = CreateAnomaly(
            id: "latest",
            satellite: "Suomi-NPP",
            acquiredAtUtc: Utc(hour: 13),
            dayNight: "N",
            frpMegawatts: 80,
            thermalContrastKelvin: 25);
        var cluster = new NotificationCluster(Id: "cluster", representative, [representative, latest]);

        TelegramNotificationMessages messages = TelegramMessageFormatter.FormatMessages(
            cluster,
            new GibsPreview([1], new(FirmsSource: "VIIRS_SNPP_NRT", Satellite: "Suomi-NPP", "VIIRS")),
            new(WidthKilometers: 30, HeightKilometers: 20, PixelWidth: 900, PixelHeight: 600),
            clusterDiameterKilometers: 1,
            landCoverSummary: "Built-up · 80%",
            nearbyFeatures: []);

        const string expected = """
            🛰 <b>Satellite:</b> Suomi-NPP &#183; VIIRS
            📡 <b>Source:</b> <code>VIIRS_SNPP_NRT</code>
            🎯 <b>Confidence:</b> Nominal

            ⚡ <b>FRP:</b> 100 MW
            🌡 <b>Thermal contrast:</b> +30 K
            🔎 <b>Detections:</b> 2

            🖼 <b>Imagery:</b> Sensor-matched · 2026-07-19
            📏 <b>Coverage:</b> 30 &#215; 20 km
            """;
        Assert.Equal(expected, messages.CommentMessage);
        Assert.DoesNotContain("Land cover:", messages.CommentMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatMessagesDecodesAndSortsClusterCountryCodes()
    {
        Anomaly representative = CreateAnomaly(
            id: "representative",
            satellite: "Suomi-NPP",
            acquiredAtUtc: Utc(hour: 12),
            dayNight: "D",
            frpMegawatts: 100,
            thermalContrastKelvin: 30,
            countryCode: "UKR");
        Anomaly russianAnomaly = CreateAnomaly(
            id: "russian",
            satellite: "Suomi-NPP",
            acquiredAtUtc: Utc(hour: 13),
            dayNight: "D",
            frpMegawatts: 90,
            thermalContrastKelvin: 25,
            countryCode: "RUS");
        var cluster = new NotificationCluster(Id: "cluster", representative, [representative, russianAnomaly]);

        TelegramNotificationMessages messages = TelegramMessageFormatter.FormatMessages(
            cluster,
            GibsPreview.Unavailable,
            new(WidthKilometers: 30, HeightKilometers: 20, PixelWidth: 900, PixelHeight: 600),
            clusterDiameterKilometers: 1,
            landCoverSummary: null,
            nearbyFeatures: []);

        Assert.Contains("📍 <b>Russia; Ukraine</b>", messages.MainMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatMessagesBuildsMultiSatelliteComment()
    {
        Anomaly representative = CreateAnomaly(
            id: "representative",
            satellite: "Suomi-NPP",
            acquiredAtUtc: Utc(hour: 12),
            dayNight: "D",
            frpMegawatts: 100,
            thermalContrastKelvin: 30);
        Anomaly latest = CreateAnomaly(
            id: "latest",
            satellite: "NOAA-20",
            acquiredAtUtc: Utc(hour: 13),
            dayNight: "N",
            frpMegawatts: 120,
            thermalContrastKelvin: 35);
        var cluster = new NotificationCluster(Id: "cluster", representative, [representative, latest]);

        TelegramNotificationMessages messages = TelegramMessageFormatter.FormatMessages(
            cluster,
            new GibsPreview([1], new(FirmsSource: "VIIRS_SNPP_NRT", Satellite: "Suomi-NPP", "VIIRS")),
            new(WidthKilometers: 30, HeightKilometers: 20, PixelWidth: 900, PixelHeight: 600),
            clusterDiameterKilometers: 1,
            landCoverSummary: "Built-up · 80%",
            nearbyFeatures: []);

        const string expected = """
            ✅ <b>Confirmed by:</b> 2 satellites

            🛰 <b>Satellites:</b>
            • NOAA-20 &#183; VIIRS
            • Suomi-NPP &#183; VIIRS

            📡 <b>Feeds:</b> <code>VIIRS_NOAA20_NRT; VIIRS_SNPP_NRT</code>

            ⚡ <b>Peak FRP:</b> 120 MW
            🌡 <b>Peak contrast:</b> +35 K
            🔎 <b>Detections:</b> 2
            🏙 <b>Land cover:</b> Built-up &#183; 80%

            🖼 <b>Preview:</b> Suomi-NPP &#183; VIIRS imagery · 2026-07-19
            📏 <b>Coverage:</b> 30 &#215; 20 km
            """;
        Assert.Equal(expected, messages.CommentMessage);
    }

    [Theory]
    [InlineData(false, "🖼 <b>Imagery:</b> Aqua &#183; MODIS base &#183; Suomi-NPP &#183; VIIRS thermal overlay · 2026-07-19")]
    [InlineData(true, "🖼 <b>Preview:</b> Aqua &#183; MODIS base &#183; Suomi-NPP &#183; VIIRS thermal overlay · 2026-07-19")]
    public void FormatMessagesNamesFallbackBaseAndRepresentativeOverlay(
        bool multiSatellite,
        string expectedPreviewLine)
    {
        Anomaly representative = CreateAnomaly(
            id: "representative",
            satellite: "Suomi-NPP",
            acquiredAtUtc: Utc(hour: 12),
            dayNight: "D",
            frpMegawatts: 100,
            thermalContrastKelvin: 30);
        Anomaly[] members = multiSatellite
            ?
            [
                representative,
                CreateAnomaly(
                    id: "second",
                    satellite: "NOAA-20",
                    acquiredAtUtc: representative.AcquiredAtUtc,
                    dayNight: "D",
                    frpMegawatts: 90,
                    thermalContrastKelvin: 25)
            ]
            : [representative];
        var cluster = new NotificationCluster(Id: "cluster", representative, [.. members]);

        TelegramNotificationMessages messages = TelegramMessageFormatter.FormatMessages(
            cluster,
            new GibsPreview([1], new(FirmsSource: "MODIS_NRT", Satellite: "Aqua", "MODIS")),
            new(WidthKilometers: 30, HeightKilometers: 20, PixelWidth: 900, PixelHeight: 600),
            clusterDiameterKilometers: 1,
            landCoverSummary: null,
            nearbyFeatures: []);

        Assert.Contains(expectedPreviewLine, messages.CommentMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatMessagesOmitsUnavailableOptionalCommentFields()
    {
        Anomaly representative = CreateAnomaly(
            id: "representative",
            satellite: "Suomi-NPP",
            acquiredAtUtc: Utc(hour: 12),
            dayNight: "?",
            frpMegawatts: null,
            thermalContrastKelvin: null,
            confidenceCategory: null);
        var cluster = new NotificationCluster(Id: "cluster", representative, [representative]);

        TelegramNotificationMessages messages = TelegramMessageFormatter.FormatMessages(
            cluster,
            GibsPreview.Unavailable,
            new(WidthKilometers: 30, HeightKilometers: 20, PixelWidth: 900, PixelHeight: 600),
            clusterDiameterKilometers: 0,
            landCoverSummary: null,
            nearbyFeatures: []);

        Assert.DoesNotContain("Pass:", messages.MainMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Confidence:", messages.CommentMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("FRP:", messages.CommentMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Thermal contrast:", messages.CommentMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Imagery:", messages.CommentMessage, StringComparison.Ordinal);
        Assert.Contains("🔎 <b>Detections:</b> 1", messages.CommentMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatKeepsFiveNearbyFeaturesWithinPhotoCaptionLimitWhenNamesAreLong()
    {
        Anomaly representative = CreateAnomaly(
            id: "representative",
            satellite: "Suomi-NPP",
            acquiredAtUtc: Utc(hour: 12),
            dayNight: "D",
            frpMegawatts: 100,
            thermalContrastKelvin: 30);
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
            landCoverSummary: "Built-up · 80%",
            nearbyFeatures);

        Assert.InRange(caption.Length, low: 1, high: 1024);
        Assert.Equal(5, caption.Split('\n').Count(line => line.StartsWith(value: "<code>", StringComparison.Ordinal)));
    }

    private static NearbyFeature Feature(long id, string name, double distanceKilometers) =>
        new(
            OsmType: "node",
            OsmId: id,
            Name: name,
            Latitude: 50.123,
            Longitude: 30.654,
            DistanceKilometers: distanceKilometers,
            OpenStreetMapUrl: $"https://www.openstreetmap.org/node/{id}");

    private static DateTimeOffset Utc(int hour, int minute = 0) =>
        new(
            year: 2026,
            month: 7,
            day: 19,
            hour,
            minute,
            second: 0,
            offset: TimeSpan.Zero);

    private static Anomaly CreateAnomaly(
        string id,
        string satellite,
        DateTimeOffset acquiredAtUtc,
        string dayNight,
        double? frpMegawatts,
        double? thermalContrastKelvin,
        string? confidenceCategory = "nominal",
        string countryCode = "UKR") =>
        new(
            Id: id,
            CountryCode: countryCode,
            Source: "NOAA-20".Equals(satellite, StringComparison.Ordinal)
                ? "VIIRS_NOAA20_NRT"
                : "VIIRS_SNPP_NRT",
            Satellite: satellite,
            Instrument: "VIIRS",
            Latitude: 50.123456,
            Longitude: 30.654321,
            AcquiredAtUtc: acquiredAtUtc,
            DayNight: dayNight,
            BrightnessKelvin: thermalContrastKelvin is null ? null : 330,
            SecondaryBrightnessKelvin: thermalContrastKelvin is { } contrast ? 330 - contrast : null,
            FrpMegawatts: frpMegawatts,
            ScanKilometers: 0.4,
            TrackKilometers: 0.4,
            ConfidenceRaw: confidenceCategory is null ? null : "n",
            ConfidencePercent: null,
            ConfidenceCategory: confidenceCategory,
            Version: "2.0NRT",
            GoogleMapsUrl: $"https://www.google.com/maps?q=50.123456,30.654321&id={id}");
}
