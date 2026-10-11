using System.Collections.Immutable;

namespace ThermalWatch.Core;

/// <summary>Counts policy decisions and actual transport outcomes for one automatic processing pass.</summary>
/// <param name="ActiveClusterCount">The active clusters before policy evaluation.</param>
/// <param name="EvaluatedClusterCount">The clusters evaluated during this pass.</param>
/// <param name="DeliveredClusterCount">The clusters successfully sent by the transport.</param>
/// <param name="RejectedClusterCount">The clusters rejected by selection policy.</param>
/// <param name="StartupSuppressedIncidentCount">The incidents suppressed by the startup baseline.</param>
/// <param name="DuplicateEpisodeCount">The candidates suppressed by prior episode delivery.</param>
/// <param name="SendFailureCount">The unsuccessful transport attempts.</param>
/// <param name="LandCoverCandidateCount">The candidates evaluated for land cover.</param>
/// <param name="VegetationSuppressedCount">The candidates suppressed by vegetation policy.</param>
/// <param name="LandCoverUnavailableCount">The candidates with unavailable land-cover data.</param>
/// <param name="LandCoverYear">The available sampling year.</param>
/// <param name="RejectionCounts">Counts grouped by policy rejection reason.</param>
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
    ImmutableDictionary<NotificationRejectionReason, int> RejectionCounts
)
{
    /// <summary>Returns the rejection count, or zero when no candidate had this reason.</summary>
    public int RejectionCount(NotificationRejectionReason reason)
    {
        return RejectionCounts.GetValueOrDefault(reason);
    }
}
