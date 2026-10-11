using System.Text.Json;

using ThermalWatch.Core;
using ThermalWatch.Telegram;

namespace ThermalWatch.Tests;

/// <summary>Verifies stable JSON names for published API contracts.</summary>
public sealed class PublicContractNamingTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Preserves anomaly and refresh-segment property names.</summary>
    [Fact]
    public void AnomalySnapshotUsesExplicitAnomalyAndSegmentJsonNames()
    {
        var snapshot = new AnomalySnapshot(
            DateTimeOffset.UnixEpoch,
            ActiveWindowHours: 24,
            IsReady: true,
            IsPartiallyStale: true,
            ["UKR"],
            [
                new(
                    CountryCode: "UKR",
                    Source: "VIIRS_SNPP_NRT",
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch,
                    IsStale: true,
                    Error: null,
                    IngestionModes.Country
                )
            ],
            AnomalyCount: 0,
            []
        );

        using JsonDocument json = JsonSerializer.SerializeToDocument(snapshot, JsonOptions);

        var propertyNames = new List<string>();

        foreach (JsonProperty property in json.RootElement.EnumerateObject())
            propertyNames.Add(property.Name);

        Assert.Equal(
            [
                "activeWindowHours",
                "anomalies",
                "anomalyCount",
                "configuredCountryCodes",
                "generatedAtUtc",
                "isPartiallyStale",
                "isReady",
                "segments"
            ],
            propertyNames.Order(StringComparer.Ordinal),
            StringComparer.Ordinal
        );

        JsonElement[] segments = [.. json.RootElement.GetProperty(propertyName: "segments").EnumerateArray()];
        JsonElement segment = Assert.Single(segments);
        var segmentPropertyNames = new List<string>();

        foreach (JsonProperty property in segment.EnumerateObject())
            segmentPropertyNames.Add(property.Name);

        Assert.Equal(
            [
                "countryCode",
                "error",
                "ingestionMode",
                "isStale",
                "lastAttemptAtUtc",
                "lastSuccessAtUtc",
                "source"
            ],
            segmentPropertyNames.Order(StringComparer.Ordinal),
            StringComparer.Ordinal
        );
    }

    /// <summary>Preserves cluster-specific names in manual delivery results.</summary>
    [Fact]
    public void ManualTelegramSendResponseUsesClusterSpecificJsonNames()
    {
        var response = new ManualTelegramSendResponse(
            RequestedClusterCount: 5,
            EligibleClusterCount: 4,
            SelectedClusterCount: 3,
            SentClusterCount: 2,
            FailedClusterCount: 1,
            ["cluster"]
        );

        using JsonDocument json = JsonSerializer.SerializeToDocument(response, JsonOptions);

        var propertyNames = new List<string>();

        foreach (JsonProperty property in json.RootElement.EnumerateObject())
            propertyNames.Add(property.Name);

        Assert.Equal(
            [
                "eligibleClusterCount",
                "failedClusterCount",
                "failedClusterIds",
                "requestedClusterCount",
                "selectedClusterCount",
                "sentClusterCount"
            ],
            propertyNames.Order(StringComparer.Ordinal),
            StringComparer.Ordinal
        );
    }
}
