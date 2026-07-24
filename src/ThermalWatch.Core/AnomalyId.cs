using System.Security.Cryptography;
using System.Text;
using System.Globalization;

namespace ThermalWatch.Core;

public static class AnomalyId
{
    public static string Create(
        string countryCode,
        string source,
        string satellite,
        DateTimeOffset acquiredAtUtc,
        double latitude,
        double longitude)
    {
        string canonical = string.Join('|',
            countryCode,
            source,
            satellite,
            acquiredAtUtc.UtcDateTime.ToString(format: "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture),
            latitude.ToString(format: "R", CultureInfo.InvariantCulture),
            longitude.ToString(format: "R", CultureInfo.InvariantCulture));

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash.AsSpan(start: 0, length: 16));
    }

    public static string CreateClusterId(IEnumerable<string> anomalyIds)
    {
        string canonical = string.Join('|', anomalyIds.Order(StringComparer.Ordinal));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(hash.AsSpan(start: 0, length: 16));
    }
}
