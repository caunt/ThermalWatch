using System.Globalization;
using System.Net;
using System.Text;
using ThermalWatch.Core;

namespace ThermalWatch.Telegram;

public static class TelegramMessageFormatter
{
    private const int MaximumPhotoCaptionLength = 1024;

    public static string Format(
        NotificationCluster cluster,
        bool hasPreview,
        GibsPreviewDimensions previewDimensions,
        double? clusterDiameterKilometers,
        string? landCoverSummary) =>
        Format(
            cluster,
            hasPreview ? new([1]) : GibsPreview.Unavailable,
            previewDimensions,
            clusterDiameterKilometers,
            landCoverSummary);

    public static string Format(
        NotificationCluster cluster,
        GibsPreview preview,
        GibsPreviewDimensions previewDimensions,
        double? clusterDiameterKilometers,
        string? landCoverSummary)
    {
        TemplateData data = CreateTemplateData(
            cluster,
            preview,
            previewDimensions,
            clusterDiameterKilometers,
            landCoverSummary);
        Func<TemplateData, int, string> build = data.Satellites.Length >= 2
            ? BuildMultiSatellite
            : BuildBalanced;

        string message = build(data, 0);
        if (message.Length <= MaximumPhotoCaptionLength)
            return message;

        message = build(data, 1);
        if (message.Length <= MaximumPhotoCaptionLength)
            return message;

        message = build(data, 2);
        return message.Length <= MaximumPhotoCaptionLength
            ? message
            : BuildMinimal(data);
    }

    private static string BuildBalanced(TemplateData data, int compactLevel)
    {
        Anomaly representative = data.Cluster.Representative;
        var sections = new List<string>
        {
            "🔥 <b>New thermal anomaly</b>"
        };
        var observation = new List<string>
        {
            $"📍 <b>{Html(FormatInlineList(data.Countries, compactLevel))}</b>",
            $"🕓 <b>Observed:</b> {Html(FormatDateTime(representative.AcquiredAtUtc))} UTC"
        };
        if (FormatPass(representative.DayNight) is { } pass)
            observation.Add($"{pass.Emoji} <b>Pass:</b> {pass.Name}");
        sections.Add(string.Join('\n', observation));

        var sensor = new List<string>
        {
            $"🛰 <b>Satellite:</b> {Html(FormatInlineList(data.Satellites, compactLevel))}",
            $"📡 <b>Sources:</b> <code>{Html(FormatInlineList(data.Sources, compactLevel))}</code>"
        };
        if (compactLevel < 2 && FormatConfidence(representative) is { } confidence)
            sensor.Add($"🎯 <b>Confidence:</b> {Html(confidence)}");
        sections.Add(string.Join('\n', sensor));

        var metrics = new List<string>();
        if (compactLevel < 2 && FormatNumber(representative.FrpMegawatts) is { } frp)
            metrics.Add($"⚡ <b>FRP:</b> {Html(frp)} MW");
        if (compactLevel < 2 && FormatSignedNumber(representative.ThermalContrastKelvin) is { } contrast)
            metrics.Add($"🌡 <b>Thermal contrast:</b> {Html(contrast)} K");
        metrics.Add($"🔎 <b>Detections:</b> {Html(data.Cluster.Members.Length.ToString(CultureInfo.InvariantCulture))}");
        if (FormatNumber(data.ClusterDiameterKilometers) is { } diameter)
            metrics.Add($"📐 <b>Cluster diameter:</b> {Html(diameter)} km");
        sections.Add(string.Join('\n', metrics));

        if (data.HasPreview && compactLevel < 2)
        {
            string imagery = IsSensorMatchedPreview(representative, data.PreviewBaseSource)
                ? "Sensor-matched"
                : FormatFallbackPreview(representative, data.PreviewBaseSource!.Value);
            var preview = new List<string>
            {
                $"🖼 <b>Imagery:</b> {Html(imagery)} · {Html(FormatDate(representative.AcquiredAtUtc))}"
            };
            if (FormatCoverage(data.PreviewDimensions) is { } coverage)
                preview.Add($"📏 <b>Coverage:</b> {Html(coverage)} km");
            sections.Add(string.Join('\n', preview));
        }

        sections.Add(FormatLocation(data));
        sections.Add("⚠️ <i>Satellite-detected thermal anomaly; not a confirmed fire.</i>");
        return string.Join(separator: "\n\n", sections);
    }

    private static string BuildMultiSatellite(TemplateData data, int compactLevel)
    {
        Anomaly representative = data.Cluster.Representative;
        var sections = new List<string>
        {
            "🔥 <b>Multi-satellite thermal anomaly</b>",
            string.Join('\n',
                $"📍 <b>{Html(FormatInlineList(data.Countries, compactLevel))}</b>",
                $"🕓 <b>Latest observation:</b> {Html(FormatDateTime(data.LatestObservation))} UTC",
                $"✅ <b>Confirmed by:</b> {Html(data.Satellites.Length.ToString(CultureInfo.InvariantCulture))} satellites"),
            $"🛰 <b>Satellites:</b>\n{FormatSatelliteBullets(data.Satellites, compactLevel)}",
            $"📡 <b>Feeds:</b> <code>{Html(FormatInlineList(data.Sources, compactLevel))}</code>"
        };

        var metrics = new List<string>();
        if (compactLevel < 2 && FormatNumber(data.PeakFrpMegawatts) is { } frp)
            metrics.Add($"⚡ <b>Peak FRP:</b> {Html(frp)} MW");
        if (compactLevel < 2 && FormatSignedNumber(data.PeakThermalContrastKelvin) is { } contrast)
            metrics.Add($"🌡 <b>Peak contrast:</b> {Html(contrast)} K");
        metrics.Add($"🔎 <b>Detections:</b> {Html(data.Cluster.Members.Length.ToString(CultureInfo.InvariantCulture))}");
        if (FormatNumber(data.ClusterDiameterKilometers) is { } diameter)
            metrics.Add($"📐 <b>Spread:</b> {Html(diameter)} km");
        if (compactLevel < 2 && data.LandCoverSummary is { Length: > 0 } landCover)
            metrics.Add($"🏙 <b>Land cover:</b> {Html(landCover)}");
        sections.Add(string.Join('\n', metrics));

        if (data.HasPreview && compactLevel < 2)
        {
            string imagery = IsSensorMatchedPreview(representative, data.PreviewBaseSource)
                ? $"{FormatSatellite(representative)} imagery"
                : FormatFallbackPreview(representative, data.PreviewBaseSource!.Value);
            var preview = new List<string>
            {
                $"🖼 <b>Preview:</b> {Html(imagery)} for {Html(FormatDate(representative.AcquiredAtUtc))}"
            };
            if (FormatCoverage(data.PreviewDimensions) is { } coverage)
                preview.Add($"📏 <b>Coverage:</b> {Html(coverage)} km");
            sections.Add(string.Join('\n', preview));
        }

        sections.Add(FormatLocation(data));
        return string.Join(separator: "\n\n", sections);
    }

    private static string BuildMinimal(TemplateData data)
    {
        bool multiSatellite = data.Satellites.Length >= 2;
        string title = multiSatellite
            ? "🔥 <b>Multi-satellite thermal anomaly</b>"
            : "🔥 <b>New thermal anomaly</b>";
        string timeLabel = multiSatellite ? "Latest observation" : "Observed";
        DateTimeOffset observedAt = multiSatellite
            ? data.LatestObservation
            : data.Cluster.Representative.AcquiredAtUtc;
        var sections = new List<string>
        {
            title,
            string.Join('\n',
                $"📍 <b>{Html(FormatInlineList(data.Countries, compactLevel: 2))}</b>",
                $"🕓 <b>{timeLabel}:</b> {Html(FormatDateTime(observedAt))} UTC",
                $"🔎 <b>Detections:</b> {Html(data.Cluster.Members.Length.ToString(CultureInfo.InvariantCulture))}"),
            FormatLocation(data)
        };
        if (!multiSatellite)
            sections.Add("⚠️ <i>Satellite-detected thermal anomaly; not a confirmed fire.</i>");
        return string.Join(separator: "\n\n", sections);
    }

    private static TemplateData CreateTemplateData(
        NotificationCluster cluster,
        GibsPreview preview,
        GibsPreviewDimensions previewDimensions,
        double? clusterDiameterKilometers,
        string? landCoverSummary) =>
        new(
            cluster,
            SortedDistinct(cluster.Members.Select(member =>
                CountryCatalog.GetDisplayName(member.CountryCode))),
            SortedDistinct(cluster.Members.Select(FormatSatellite)),
            SortedDistinct(cluster.Members.Select(member => member.Source)),
            cluster.Members.Max(member => member.AcquiredAtUtc),
            MaximumAvailable(cluster.Members.Select(member => member.FrpMegawatts)),
            MaximumAvailable(cluster.Members.Select(member => member.ThermalContrastKelvin)),
            preview.IsAvailable,
            preview.BaseSource,
            previewDimensions,
            clusterDiameterKilometers,
            landCoverSummary);

    private static string FormatLocation(TemplateData data)
    {
        Anomaly representative = data.Cluster.Representative;
        string latitude = representative.Latitude.ToString(format: "0.000000", CultureInfo.InvariantCulture);
        string longitude = representative.Longitude.ToString(format: "0.000000", CultureInfo.InvariantCulture);
        return $"📌 <code>{Html(latitude)}, {Html(longitude)}</code>";
    }

    private static string FormatSatelliteBullets(string[] satellites, int compactLevel)
    {
        int maximumItems = compactLevel switch
        {
            0 => int.MaxValue,
            1 => 2,
            _ => 1
        };
        var included = satellites.Take(maximumItems).Select(satellite =>
            $"• {Html(CompactValue(satellite, compactLevel))}").ToList();
        if (satellites.Length > included.Count)
            included.Add($"• +{Html((satellites.Length - included.Count).ToString(CultureInfo.InvariantCulture))} more");
        return string.Join('\n', included);
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
            result.Append(elements.GetTextElement());
        return result.Append('…').ToString();
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
            if (anomaly.Satellite.Equals(value: "Terra", StringComparison.OrdinalIgnoreCase)
                || anomaly.Satellite.Equals(value: "T", StringComparison.OrdinalIgnoreCase))
            {
                return "Terra · MODIS";
            }
            if (anomaly.Satellite.Equals(value: "Aqua", StringComparison.OrdinalIgnoreCase)
                || anomaly.Satellite.Equals(value: "A", StringComparison.OrdinalIgnoreCase))
            {
                return "Aqua · MODIS";
            }
        }

        return $"{anomaly.Satellite} · {anomaly.Instrument}";
    }

    private static string FormatSatellite(GibsPreviewSource source) =>
        $"{source.Satellite} · {source.Instrument}";

    private static bool IsSensorMatchedPreview(
        Anomaly representative,
        GibsPreviewSource? baseSource) =>
        baseSource is null
        || FormatSatellite(representative).Equals(
            FormatSatellite(baseSource.Value),
            StringComparison.Ordinal);

    private static string FormatFallbackPreview(
        Anomaly representative,
        GibsPreviewSource baseSource) =>
        $"{FormatSatellite(baseSource)} base · {FormatSatellite(representative)} thermal overlay";

    private static string? FormatConfidence(Anomaly anomaly)
    {
        if (anomaly.ConfidenceCategory is { } category)
        {
            return category.ToLowerInvariant() switch
            {
                "l" or "low" => "Low",
                "n" or "nominal" => "Nominal",
                "h" or "high" => "High",
                _ => null
            };
        }

        return FormatNumber(anomaly.ConfidencePercent) is { } percentage
            ? $"{percentage}%"
            : null;
    }

    private static (string Emoji, string Name)? FormatPass(string dayNight) => dayNight switch
    {
        "D" => ("☀️", "Daytime"),
        "N" => ("🌙", "Nighttime"),
        _ => null
    };

    private static string? FormatCoverage(GibsPreviewDimensions dimensions)
    {
        string? width = FormatNumber(dimensions.WidthKilometers);
        string? height = FormatNumber(dimensions.HeightKilometers);
        return width is not null && height is not null
            ? $"{width} × {height}"
            : null;
    }

    private static string? FormatNumber(double? value) =>
        value is { } number && double.IsFinite(number)
            ? number.ToString(format: "0.##", CultureInfo.InvariantCulture)
            : null;

    private static string? FormatSignedNumber(double? value) =>
        value is { } number && double.IsFinite(number)
            ? number.ToString(format: "+0.##;-0.##;0", CultureInfo.InvariantCulture)
            : null;

    private static double? MaximumAvailable(IEnumerable<double?> values)
    {
        double[] available = [.. values
            .Where(value => value is { } number && double.IsFinite(number))
            .Select(value => value!.Value)];
        return available.Length == 0 ? null : available.Max();
    }

    private static string FormatDateTime(DateTimeOffset value) =>
        value.UtcDateTime.ToString(format: "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string FormatDate(DateTimeOffset value) =>
        value.UtcDateTime.ToString(format: "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string[] SortedDistinct(IEnumerable<string> values) =>
    [
        .. values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
    ];

    private static string Html(string value) => WebUtility.HtmlEncode(value);

    private sealed record TemplateData(
        NotificationCluster Cluster,
        string[] Countries,
        string[] Satellites,
        string[] Sources,
        DateTimeOffset LatestObservation,
        double? PeakFrpMegawatts,
        double? PeakThermalContrastKelvin,
        bool HasPreview,
        GibsPreviewSource? PreviewBaseSource,
        GibsPreviewDimensions PreviewDimensions,
        double? ClusterDiameterKilometers,
        string? LandCoverSummary);
}
