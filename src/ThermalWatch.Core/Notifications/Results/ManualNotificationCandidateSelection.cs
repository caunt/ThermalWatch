using System.Collections.Immutable;

namespace ThermalWatch.Core;

/// <summary>The limited candidates prepared for an explicit manual send.</summary>
/// <param name="EligibleClusterCount">The total eligible count before applying the send limit.</param>
/// <param name="SelectedCandidates">The candidates selected in notification order.</param>
public sealed record ManualNotificationCandidateSelection(int EligibleClusterCount, ImmutableArray<PreparedNotificationCandidate> SelectedCandidates);
