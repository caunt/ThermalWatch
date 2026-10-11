using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ThermalWatch.Core;

/// <summary>Creates deterministic observation and cluster identifiers from canonical inputs.</summary>
public static class AnomalyId
{
    /// <summary>Hashes the ingestion identity, acquisition time, and exact coordinates.</summary>
    /// <param name="countryCode">The ingestion country's code.</param>
    /// <param name="source">The FIRMS dataset identifier.</param>
    /// <param name="satellite">The observing satellite.</param>
    /// <param name="acquiredAtUtc">The UTC acquisition timestamp.</param>
    /// <param name="latitude">The latitude in degrees.</param>
    /// <param name="longitude">The longitude in degrees.</param>
    public static string Create(string countryCode, string source, string satellite, DateTimeOffset acquiredAtUtc, double latitude, double longitude)
    {
        string canonical = string.Join(
            separator: '|',
            countryCode,
            source,
            satellite,
            acquiredAtUtc.UtcDateTime.ToString(format: "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture),
            latitude.ToString(format: "R", CultureInfo.InvariantCulture),
            longitude.ToString(format: "R", CultureInfo.InvariantCulture)
        );

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        return Convert.ToHexStringLower(hash.AsSpan(start: 0, length: 16));
    }

    /// <summary>Hashes member identifiers in ordinal order, independent of their input order.</summary>
    /// <param name="anomalyIds">The observation identifiers in the cluster.</param>
    public static string CreateClusterId(IEnumerable<string> anomalyIds)
    {
        string canonical = string.Join(separator: '|', anomalyIds.Order(StringComparer.Ordinal));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        return Convert.ToHexStringLower(hash.AsSpan(start: 0, length: 16));
    }
}
