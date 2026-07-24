using System.Collections.Immutable;

namespace ThermalWatch.Core;

public sealed record AutomaticNotificationProcessingSummary(
    int ActiveClusterCount,
    int EvaluatedClusterCount,
    int DeliveredClusterCount,
    int RejectedClusterCount,
    int StartupSuppressedIncidentCount,
    int DuplicateEpisodeCount,
    int SendFailureCount,
    int LandCoverCandidateCount,
    int VegetationSuppressedCount,
    int LandCoverUnavailableCount,
    int? LandCoverYear,
    ImmutableDictionary<NotificationRejectionReason, int> RejectionCounts)
{
    public int RejectionCount(NotificationRejectionReason reason) =>
        RejectionCounts.GetValueOrDefault(reason);
}
