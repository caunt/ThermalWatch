using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;

using ThermalWatch.Core;

namespace ThermalWatch.Telegram;

internal static class TelegramMessageFormatter
{
    private const int MaximumPhotoCaptionLength = 1024;

    internal static TelegramNotificationMessages FormatMessages(
        NotificationCluster cluster,
        GibsPreview preview,
        GibsPreviewDimensions previewDimensions,
        double? clusterDiameterKilometers,
        string? landCoverSummary,
        ImmutableArray<NearbyFeature> nearbyFeatures
    )
    {
        return FormatMessages(cluster, preview, previewDimensions, clusterDiameterKilometers, landCoverSummary, settlementName: null, nearbyFeatures);
    }

    internal static TelegramNotificationMessages FormatMessages(
        NotificationCluster cluster,
        GibsPreview preview,
        GibsPreviewDimensions previewDimensions,
        double? clusterDiameterKilometers,
        string? landCoverSummary,
        string? settlementName,
        ImmutableArray<NearbyFeature> nearbyFeatures
    )
    {
        TemplateData data = CreateTemplateData(cluster, preview, previewDimensions, clusterDiameterKilometers, landCoverSummary, settlementName, nearbyFeatures);

        string mainMessage = BuildMain(data, compactLevel: 0);

        if (mainMessage.Length > MaximumPhotoCaptionLength)
            mainMessage = BuildMain(data, compactLevel: 1);

        if (mainMessage.Length > MaximumPhotoCaptionLength)
            mainMessage = BuildMain(data, compactLevel: 2);

        if (mainMessage.Length > MaximumPhotoCaptionLength)
            mainMessage = BuildMain(data, compactLevel: 3);

        string commentMessage = data.Satellites.Length >= 2
            ? BuildMultiSatelliteComment(data)
            : BuildSingleSatelliteComment(data);

        return new(mainMessage, commentMessage);
    }

    private static string BuildMain(TemplateData data, int compactLevel)
    {
        var sections = new List<string>
        {
            "🔥 <b>New thermal anomaly</b>"
        };

        var observation = new List<string>
        {
            $"📍 <b>{Html(FormatLocation(data, compactLevel))}</b>",
            $"🕓 <b>Observed:</b> {Html(FormatDateTime(data.LatestAnomaly.AcquiredAtUtc))} UTC"
        };

        if (FormatPass(data.LatestAnomaly.DayNight) is { } pass)
            observation.Add($"{pass.Emoji} <b>Pass:</b> {pass.Name}");

        sections.Add(string.Join(separator: '\n', observation));

        if (FormatNearbyFeatures(data.NearbyFeatures, compactLevel) is { } nearbyFeatures)
            sections.Add(nearbyFeatures);

        var location = new List<string>();

        if (FormatNumber(data.ClusterDiameterKilometers) is { } diameter)
            location.Add($"♾️ <b>Diameter:</b> {Html(diameter)} km");

        location.Add(FormatCoordinates(data.Cluster.Representative));
        location.Add(FormatMapLinks(data.Cluster.Representative));
        sections.Add(string.Join(separator: '\n', location));

        return string.Join(separator: "\n\n", sections);
    }

    private static string BuildMultiSatelliteComment(TemplateData data)
    {
        Anomaly representative = data.Cluster.Representative;

        var sections = new List<string>
        {
            $"✅ <b>Confirmed by:</b> {Html(data.Satellites.Length.ToString(CultureInfo.InvariantCulture))} satellites",
            $"🛰 <b>Satellites:</b>\n{FormatSatelliteBullets(data.Satellites, compactLevel: 0)}",
            $"📡 <b>Feeds:</b> <code>{Html(FormatInlineList(data.Sources, compactLevel: 0))}</code>"
        };

        var metrics = new List<string>();

        if (FormatNumber(data.TotalFrpMegawatts) is { } totalFrp)
            metrics.Add($"⚡ <b>Total FRP:</b> {Html(totalFrp)} MW");

        if (FormatNumber(data.PeakFrpMegawatts) is { } frp)
            metrics.Add($"⚡ <b>Peak FRP:</b> {Html(frp)} MW");

        if (FormatSignedNumber(data.PeakThermalContrastKelvin) is { } contrast)
            metrics.Add($"🌡 <b>Peak contrast:</b> {Html(contrast)} K");

        metrics.Add($"🔎 <b>Detections:</b> {Html(data.Cluster.Members.Length.ToString(CultureInfo.InvariantCulture))}");

        if (data.LandCoverSummary is { Length: > 0 } landCover)
            metrics.Add($"🏙 <b>Land cover:</b> {Html(landCover)}");

        sections.Add(string.Join(separator: '\n', metrics));

        if (data.HasPreview)
        {
            string imagery = data.PreviewBaseSource is { } baseSource && !IsSensorMatchedPreview(representative, baseSource)
                ? FormatFallbackPreview(representative, baseSource)
                : $"{FormatSatellite(representative)} imagery";

            var preview = new List<string>
            {
                $"🖼 <b>Preview:</b> {Html(imagery)} · {Html(FormatDate(representative.AcquiredAtUtc))}"
            };

            if (FormatCoverage(data.PreviewDimensions) is { } coverage)
                preview.Add($"📏 <b>Coverage:</b> {Html(coverage)} km");

            sections.Add(string.Join(separator: '\n', preview));
        }

        return string.Join(separator: "\n\n", sections);
    }

    private static string BuildSingleSatelliteComment(TemplateData data)
    {
        Anomaly representative = data.Cluster.Representative;

        var sensor = new List<string>
        {
            $"🛰 <b>Satellite:</b> {Html(FormatSatellite(representative))}",
            $"📡 <b>Source:</b> <code>{Html(representative.Source)}</code>"
        };

        if (FormatConfidence(representative) is { } confidence)
            sensor.Add($"🎯 <b>Confidence:</b> {Html(confidence)}");

        var metrics = new List<string>();

        if (FormatNumber(data.TotalFrpMegawatts) is { } totalFrp)
            metrics.Add($"⚡ <b>Total FRP:</b> {Html(totalFrp)} MW");

        if (FormatNumber(representative.FrpMegawatts) is { } frp)
            metrics.Add($"⚡ <b>FRP:</b> {Html(frp)} MW");

        if (FormatSignedNumber(representative.ThermalContrastKelvin) is { } contrast)
            metrics.Add($"🌡 <b>Thermal contrast:</b> {Html(contrast)} K");

        metrics.Add($"🔎 <b>Detections:</b> {Html(data.Cluster.Members.Length.ToString(CultureInfo.InvariantCulture))}");

        var sections = new List<string>
        {
            string.Join(separator: '\n', sensor),
            string.Join(separator: '\n', metrics)
        };

        if (data.HasPreview)
        {
            string imagery = data.PreviewBaseSource is { } baseSource && !IsSensorMatchedPreview(representative, baseSource)
                ? FormatFallbackPreview(representative, baseSource)
                : "Sensor-matched";

            var preview = new List<string>
            {
                $"🖼 <b>Imagery:</b> {Html(imagery)} · {Html(FormatDate(representative.AcquiredAtUtc))}"
            };

            if (FormatCoverage(data.PreviewDimensions) is { } coverage)
                preview.Add($"📏 <b>Coverage:</b> {Html(coverage)} km");

            sections.Add(string.Join(separator: '\n', preview));
        }

        return string.Join(separator: "\n\n", sections);
    }

    private static string CompactNearbyName(string name, int compactLevel)
    {
        int maximumTextElements = compactLevel switch
        {
            0 => 64,
            1 => 36,
            2 => 16,
            _ => 4
        };

        if (name.Length <= maximumTextElements)
            return name;

        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(name);
        var result = new StringBuilder();

        while (elements.MoveNext() && maximumTextElements-- > 1)
            result = result.Append(elements.GetTextElement());

        return result.Append(value: '…').ToString();
    }

    private static string CompactValue(string value, int compactLevel)
    {
        int maximumTextElements = compactLevel switch
        {
            0 => int.MaxValue,
            1 => 48,
            _ => 24
        };

        if (value.Length <= maximumTextElements)
            return value;

        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(value);
        var result = new StringBuilder();

        while (elements.MoveNext() && maximumTextElements-- > 1)
            result = result.Append(elements.GetTextElement());

        return result.Append(value: '…').ToString();
    }

    private static TemplateData CreateTemplateData(
        NotificationCluster cluster,
        GibsPreview preview,
        GibsPreviewDimensions previewDimensions,
        double? clusterDiameterKilometers,
        string? landCoverSummary,
        string? settlementName,
        ImmutableArray<NearbyFeature> nearbyFeatures
    )
    {
        Anomaly latestAnomaly = cluster.Members[index: 0];

        foreach (Anomaly member in cluster.Members)
        {
            if (member.AcquiredAtUtc > latestAnomaly.AcquiredAtUtc)
            {
                latestAnomaly = member;
            }
            else if (member.AcquiredAtUtc == latestAnomaly.AcquiredAtUtc)
            {
                if (StringComparer.Ordinal.Compare(member.Id, latestAnomaly.Id) < 0)
                    latestAnomaly = member;
            }
        }

        return new(
            cluster,
            SortedDistinct(cluster.Members.Select(static member => CountryCatalog.GetDisplayName(member.CountryCode))),
            SortedDistinct(cluster.Members.Select(static member => FormatSatellite(member))),
            SortedDistinct(cluster.Members.Select(static member => member.Source)),
            latestAnomaly,
            cluster.TotalFrpMegawatts,
            MaximumAvailable(cluster.Members.Select(static member => member.FrpMegawatts)),
            MaximumAvailable(cluster.Members.Select(static member => member.ThermalContrastKelvin)),
            preview.IsAvailable,
            preview.BaseSource,
            previewDimensions,
            clusterDiameterKilometers,
            landCoverSummary,
            string.IsNullOrWhiteSpace(settlementName) ? null : settlementName.Trim(),
            nearbyFeatures
        );
    }

    private static string? FormatConfidence(Anomaly anomaly)
    {
        return anomaly.ConfidenceCategory is { } category
            ? category.ToUpperInvariant() switch
            {
                "L" or "LOW" => "Low",
                "N" or "NOMINAL" => "Nominal",
                "H" or "HIGH" => "High",
                _ => null
            }
            : FormatNumber(anomaly.ConfidencePercent) is { } percentage
            ? $"{percentage}%"
            : null;
    }

    private static string FormatCoordinates(Anomaly representative)
    {
        string latitude = representative.Latitude.ToString(format: "0.000000", CultureInfo.InvariantCulture);
        string longitude = representative.Longitude.ToString(format: "0.000000", CultureInfo.InvariantCulture);

        return $"📌 <code>{Html(latitude)}, {Html(longitude)}</code>";
    }

    private static string? FormatCoverage(GibsPreviewDimensions dimensions)
    {
        string? width = FormatNumber(dimensions.WidthKilometers);
        string? height = FormatNumber(dimensions.HeightKilometers);

        return width is not null && height is not null
            ? $"{width} × {height}"
            : null;
    }

    private static string FormatDate(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString(format: "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string FormatDateTime(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString(format: "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static string FormatFallbackPreview(Anomaly representative, GibsPreviewSource baseSource)
    {
        return $"{FormatSatellite(baseSource)} base · {FormatSatellite(representative)} thermal overlay";
    }

    private static string FormatInlineList(string[] values, int compactLevel)
    {
        int maximumItems = compactLevel switch
        {
            0 => int.MaxValue,
            1 => 2,
            _ => 1
        };

        var included = values
            .Take(maximumItems)
            .Select(value => CompactValue(value, compactLevel))
            .ToList();

        if (values.Length > included.Count)
            included.Add($"+{values.Length - included.Count} more");

        return string.Join(separator: "; ", included);
    }

    private static string FormatLocation(TemplateData data, int compactLevel)
    {
        string countries = FormatInlineList(data.Countries, compactLevel);

        return data.SettlementName is { } settlementName
            ? $"{countries}, {CompactValue(settlementName, compactLevel)}"
            : countries;
    }

    private static string FormatMapLinks(Anomaly representative)
    {
        string yandexMapsUrl = string.Create(
            CultureInfo.InvariantCulture,
            $"https://yandex.com/maps/?ll={representative.Longitude:0.######}%2C{representative.Latitude:0.######}&pt={representative.Longitude:0.######}%2C{representative.Latitude:0.######}&z=12&l=sat"
        );

        return $"🗺 <a href=\"{Html(representative.GoogleMapsUrl.OriginalString)}\">Google Maps</a> · <a href=\"{Html(yandexMapsUrl)}\">Yandex Maps</a>";
    }

    private static string FormatNearbyDistance(double value)
    {
        return value.ToString(format: "0.00", CultureInfo.InvariantCulture);
    }

    private static string? FormatNearbyFeatures(ImmutableArray<NearbyFeature> nearbyFeatures, int compactLevel)
    {
        if (nearbyFeatures.IsEmpty)
            return null;

        var lines = new List<string>
        {
            "🏷 <b>Possible nearby sources:</b>"
        };

        foreach (NearbyFeature feature in nearbyFeatures)
        {
            lines.Add(
                $"<code>{Html(FormatNearbyDistance(feature.DistanceKilometers))} km • {Html(CompactNearbyName(feature.Name, compactLevel))}</code>"
            );
        }

        return string.Join(separator: '\n', lines);
    }

    private static string? FormatNumber(double? value)
    {
        return value is { } number && double.IsFinite(number)
            ? number.ToString(format: "0.##", CultureInfo.InvariantCulture)
            : null;
    }

    private static PassLabel? FormatPass(string dayNight)
    {
        return dayNight switch
        {
            "D" => new(Emoji: "☀️", Name: "Daytime"),
            "N" => new(Emoji: "🌙", Name: "Nighttime"),
            _ => null
        };
    }

    private static string FormatSatellite(Anomaly anomaly)
    {
        if ("VIIRS_SNPP_NRT".Equals(anomaly.Source, StringComparison.Ordinal))
            return "Suomi-NPP · VIIRS";

        if ("VIIRS_NOAA20_NRT".Equals(anomaly.Source, StringComparison.Ordinal))
            return "NOAA-20 · VIIRS";

        if ("VIIRS_NOAA21_NRT".Equals(anomaly.Source, StringComparison.Ordinal))
            return "NOAA-21 · VIIRS";

        if ("MODIS_NRT".Equals(anomaly.Source, StringComparison.Ordinal))
        {
            bool isTerra = anomaly.Satellite.Equals(value: "Terra", StringComparison.OrdinalIgnoreCase)
                || anomaly.Satellite.Equals(value: "T", StringComparison.OrdinalIgnoreCase);

            if (isTerra)
                return "Terra · MODIS";

            bool isAqua = anomaly.Satellite.Equals(value: "Aqua", StringComparison.OrdinalIgnoreCase)
                || anomaly.Satellite.Equals(value: "A", StringComparison.OrdinalIgnoreCase);

            if (isAqua)
                return "Aqua · MODIS";
        }

        return $"{anomaly.Satellite} · {anomaly.Instrument}";
    }

    private static string FormatSatellite(GibsPreviewSource source)
    {
        return $"{source.Satellite} · {source.Instrument}";
    }

    private static string FormatSatelliteBullets(string[] satellites, int compactLevel)
    {
        int maximumItems = compactLevel switch
        {
            0 => int.MaxValue,
            1 => 2,
            _ => 1
        };

        var included = satellites.Take(maximumItems).Select(satellite => $"• {Html(CompactValue(satellite, compactLevel))}").ToList();

        if (satellites.Length > included.Count)
            included.Add($"• +{Html((satellites.Length - included.Count).ToString(CultureInfo.InvariantCulture))} more");

        return string.Join(separator: '\n', included);
    }

    private static string? FormatSignedNumber(double? value)
    {
        return value is { } number && double.IsFinite(number)
            ? number.ToString(format: "+0.##;-0.##;0", CultureInfo.InvariantCulture)
            : null;
    }

    private static string Html(string value)
    {
        return WebUtility.HtmlEncode(value);
    }

    private static bool IsSensorMatchedPreview(Anomaly representative, GibsPreviewSource? baseSource)
    {
        return baseSource is null
        || FormatSatellite(representative).Equals(FormatSatellite(baseSource.Value), StringComparison.Ordinal);
    }

    private static double? MaximumAvailable(IEnumerable<double?> values)
    {
        double[] available = [.. values
            .Where(static value => value is { } number && double.IsFinite(number))
            .Select(static value => value.GetValueOrDefault())];

        return available.Length == 0 ? null : available.Max();
    }

    private static string[] SortedDistinct(IEnumerable<string> values)
    {
        return [
        .. values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
    ];
    }

    private sealed record PassLabel(string Emoji, string Name);

    private sealed record TemplateData(
        NotificationCluster Cluster,
        string[] Countries,
        string[] Satellites,
        string[] Sources,
        Anomaly LatestAnomaly,
        double? TotalFrpMegawatts,
        double? PeakFrpMegawatts,
        double? PeakThermalContrastKelvin,
        bool HasPreview,
        GibsPreviewSource? PreviewBaseSource,
        GibsPreviewDimensions PreviewDimensions,
        double? ClusterDiameterKilometers,
        string? LandCoverSummary,
        string? SettlementName,
        ImmutableArray<NearbyFeature> NearbyFeatures
    );
}
