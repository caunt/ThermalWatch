using System.Collections.Immutable;

namespace ThermalWatch.Core;

public sealed record ManualNotificationCandidates(
    int EligibleCount,
    ImmutableArray<PreparedNotificationCandidate> Selected);
