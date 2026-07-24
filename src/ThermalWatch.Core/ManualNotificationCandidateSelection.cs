using System.Collections.Immutable;

namespace ThermalWatch.Core;

public sealed record ManualNotificationCandidateSelection(
    int EligibleClusterCount,
    ImmutableArray<PreparedNotificationCandidate> SelectedCandidates);
