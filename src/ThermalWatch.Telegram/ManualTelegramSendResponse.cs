using System.Collections.Immutable;

namespace ThermalWatch.Telegram;

/// <summary>The candidate and transport counts returned by a completed manual send.</summary>
/// <param name="RequestedClusterCount">The requested send limit.</param>
/// <param name="EligibleClusterCount">The eligible count before limiting.</param>
/// <param name="SelectedClusterCount">The number prepared for delivery.</param>
/// <param name="SentClusterCount">The number of successful channel posts.</param>
/// <param name="FailedClusterCount">The number of failed channel posts.</param>
/// <param name="FailedClusterIds">The immutable identifiers of failed candidates.</param>
public sealed record ManualTelegramSendResponse(
    int RequestedClusterCount,
    int EligibleClusterCount,
    int SelectedClusterCount,
    int SentClusterCount,
    int FailedClusterCount,
    ImmutableArray<string> FailedClusterIds
);
