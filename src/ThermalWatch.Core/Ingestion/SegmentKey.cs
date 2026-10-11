namespace ThermalWatch.Core;

/// <summary>Identifies an independently refreshed country/source segment.</summary>
/// <param name="CountryCode">The ingestion country's three-letter code.</param>
/// <param name="Source">The FIRMS dataset identifier.</param>
public readonly record struct SegmentKey(string CountryCode, string Source);
