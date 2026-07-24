using System.Text.Json;
using ThermalWatch.Core;
using ThermalWatch.Telegram;

namespace ThermalWatch.Tests;

public sealed class PublicContractNamingTests
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void AnomalySnapshotUsesExplicitAnomalyAndSegmentJsonNames()
    {
        var snapshot = new AnomalySnapshot(
            GeneratedAtUtc: DateTimeOffset.UnixEpoch,
            ActiveWindowHours: 24,
            IsReady: true,
            IsPartiallyStale: true,
            ConfiguredCountryCodes: ["UKR"],
            Segments:
            [
                new(
                    CountryCode: "UKR",
                    Source: "VIIRS_SNPP_NRT",
                    LastAttemptAtUtc: DateTimeOffset.UnixEpoch,
                    LastSuccessAtUtc: DateTimeOffset.UnixEpoch,
                    IsStale: true,
                    Error: null,
                    IngestionMode: IngestionModes.Country)
            ],
            AnomalyCount: 0,
            Anomalies: []);

        using JsonDocument json = JsonSerializer.SerializeToDocument(
            snapshot,
            s_jsonOptions);

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
            json.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        JsonElement segment = Assert.Single(
            json.RootElement.GetProperty(propertyName: "segments").EnumerateArray());
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
            segment.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ManualTelegramSendResponseUsesClusterSpecificJsonNames()
    {
        var response = new ManualTelegramSendResponse(
            RequestedClusterCount: 5,
            EligibleClusterCount: 4,
            SelectedClusterCount: 3,
            SentClusterCount: 2,
            FailedClusterCount: 1,
            FailedClusterIds: ["cluster"]);

        using JsonDocument json = JsonSerializer.SerializeToDocument(
            response,
            s_jsonOptions);

        Assert.Equal(
            [
                "eligibleClusterCount",
                "failedClusterCount",
                "failedClusterIds",
                "requestedClusterCount",
                "selectedClusterCount",
                "sentClusterCount"
            ],
            json.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }
}
