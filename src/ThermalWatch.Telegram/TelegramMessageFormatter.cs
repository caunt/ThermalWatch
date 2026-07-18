using System.Globalization;
using System.Net;
using System.Text;
using ThermalWatch.Core;

namespace ThermalWatch.Telegram;

public static class TelegramMessageFormatter
{
    public static string Format(NotificationCluster cluster, bool hasPreview)
    {
        var representative = cluster.Representative;
        var countries = JoinUnique(cluster.Members.Select(member =>
            CountryCatalog.GetDisplayName(member.CountryCode)));
        var satellites = JoinUnique(cluster.Members.Select(FormatSatellite));
        var sources = JoinUnique(cluster.Members.Select(member => member.Source));
        var latitude = representative.Latitude.ToString("0.######", CultureInfo.InvariantCulture);
        var longitude = representative.Longitude.ToString("0.######", CultureInfo.InvariantCulture);
        var mapUrl = WebUtility.HtmlEncode(representative.GoogleMapsUrl);
        var message = new StringBuilder()
            .AppendLine("🔥 <b>New thermal anomaly</b>")
            .AppendLine()
            .Append("📍 <b>").Append(WebUtility.HtmlEncode(countries)).AppendLine("</b>")
            .Append("🛰 <b>Satellite:</b> ").AppendLine(WebUtility.HtmlEncode(satellites))
            .Append("🕓 <b>Observed:</b> ")
            .Append(representative.AcquiredAtUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture))
            .AppendLine();

        if (FormatConfidence(representative) is { } confidence)
            message.Append("🌡 <b>Confidence:</b> ").AppendLine(WebUtility.HtmlEncode(confidence));

        message.Append("👁 <b>Signal:</b> ")
            .AppendLine(FormatSignal(cluster))
            .Append("📡 <b>Sources:</b> ").AppendLine(WebUtility.HtmlEncode(sources));

        if (hasPreview)
        {
            message.Append("🖼 <b>Preview:</b> Sensor-matched · ")
                .AppendLine(representative.AcquiredAtUtc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        message.AppendLine()
            .Append("<code>").Append(latitude).Append(", ").Append(longitude).AppendLine("</code>")
            .Append("🗺 <a href=\"").Append(mapUrl).AppendLine("\">Open in Google Maps</a>")
            .AppendLine()
            .Append("⚠️ Satellite-detected thermal anomaly; not a confirmed fire.");

        return message.ToString();
    }

    private static string FormatSatellite(Anomaly anomaly) => anomaly.Source switch
    {
        "VIIRS_SNPP_NRT" => "Suomi-NPP · VIIRS",
        "VIIRS_NOAA20_NRT" => "NOAA-20 · VIIRS",
        "VIIRS_NOAA21_NRT" => "NOAA-21 · VIIRS",
        _ => $"{anomaly.Satellite} · {anomaly.Instrument}"
    };

    private static string? FormatConfidence(Anomaly anomaly)
    {
        if (anomaly.ConfidenceCategory is { } category)
            return category;

        return anomaly.ConfidencePercent is { } percent
            ? $"{percent.ToString("0.##", CultureInfo.InvariantCulture)}%"
            : null;
    }

    private static string FormatSignal(NotificationCluster cluster)
    {
        var representative = cluster.Representative;
        var components = new List<string>(3);

        if (representative.FrpMegawatts is { } frp)
            components.Add($"{frp.ToString("0.00", CultureInfo.InvariantCulture)} MW");

        if (representative.ThermalContrastKelvin is { } thermalContrast)
        {
            components.Add(
                $"{thermalContrast.ToString("+0.00;-0.00;+0.00", CultureInfo.InvariantCulture)} K");
        }

        components.Add(cluster.Members.Length == 1
            ? "1 detection"
            : $"{cluster.Members.Length.ToString(CultureInfo.InvariantCulture)} detections");
        return string.Join(" · ", components);
    }

    private static string JoinUnique(IEnumerable<string> values) => string.Join(
        "; ",
        values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
}
