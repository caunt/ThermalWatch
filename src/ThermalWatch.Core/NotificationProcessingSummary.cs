using System.Collections.Immutable;

namespace ThermalWatch.Core;

public sealed record NotificationProcessingSummary(
    int PrimedDetectionCount,
    int NewDetectionCount,
    int CandidateClusterCount,
    int AcceptedClusterCount,
    int RejectedClusterCount,
    int DuplicateEpisodeCount,
    int PendingPreviewCount,
    int PreviewTimeoutCount,
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
