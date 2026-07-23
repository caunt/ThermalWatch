using System.Collections.Immutable;

namespace ThermalWatch.Core;

public sealed record NotificationDiagnostic(
    string SelectedAnomalyId,
    string ClusterId,
    string RepresentativeId,
    ImmutableArray<string> MemberIds,
    int DetectionCount,
    double ClusterDiameterKilometers,
    bool IsEligible,
    ImmutableArray<NotificationCriterionResult> Criteria,
    GibsPreviewSource? PreviewBaseSource,
    ImmutableArray<NearbyFeature> NearbyFeatures);
