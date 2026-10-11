using System.Collections.Immutable;

namespace ThermalWatch.Core;

/// <summary>Explains notification eligibility for the cluster containing a selected observation.</summary>
/// <param name="SelectedAnomalyId">The observation requested by the viewer.</param>
/// <param name="ClusterId">The containing cluster's identifier.</param>
/// <param name="RepresentativeId">The member used for previews and presentation.</param>
/// <param name="MemberIds">The observations belonging to the cluster.</param>
/// <param name="DetectionCount">The number of cluster members.</param>
/// <param name="ClusterDiameterKilometers">The largest pairwise member distance.</param>
/// <param name="TotalFrpMegawatts">The sum of available member radiative power.</param>
/// <param name="IsEligible">Whether all blocking criteria passed.</param>
/// <param name="Criteria">The evaluated criteria and their explanations.</param>
/// <param name="PreviewBaseSource">The satellite product supplying a successful preview's base imagery.</param>
/// <param name="NearbyFeatures">The available nearby mapped context.</param>
public sealed record NotificationDiagnostic(
    string SelectedAnomalyId,
    string ClusterId,
    string RepresentativeId,
    ImmutableArray<string> MemberIds,
    int DetectionCount,
    double ClusterDiameterKilometers,
    double? TotalFrpMegawatts,
    bool IsEligible,
    ImmutableArray<NotificationCriterionResult> Criteria,
    GibsPreviewSource? PreviewBaseSource,
    ImmutableArray<NearbyFeature> NearbyFeatures
);
