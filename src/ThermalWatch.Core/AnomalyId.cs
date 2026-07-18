using System.Security.Cryptography;
using System.Text;
using System.Globalization;

namespace ThermalWatch.Core;

public static class AnomalyId
{
    public static string Create(
        string country,
        string source,
        string satellite,
        DateTimeOffset acquiredAtUtc,
        double latitude,
        double longitude)
    {
        var canonical = string.Join('|',
            country,
            source,
            satellite,
            acquiredAtUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture),
            latitude.ToString("R", CultureInfo.InvariantCulture),
            longitude.ToString("R", CultureInfo.InvariantCulture));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    public static string CreateClusterId(IEnumerable<string> anomalyIds)
    {
        var canonical = string.Join('|', anomalyIds.Order(StringComparer.Ordinal));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }
}
