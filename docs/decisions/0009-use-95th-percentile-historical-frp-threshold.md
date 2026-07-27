# 0009: Use a 95th-percentile historical FRP threshold

## Status

Accepted

## Context

[ADR 0008](0008-use-in-memory-daily-firms-baseline.md) established the in-memory 30-day daily baseline and required current total cluster FRP to exceed every comparable historical cluster. That maximum-based threshold lets one exceptional historical cluster control the complete baseline, even when the location's remaining history is substantially lower. The location filter needs to identify unusually strong current heat without treating one prior outlier as the permanent threshold for the full retention window.

The existing baseline readiness, spatial matching, current-member removal, daily reclustering, missing-FRP handling, and shared automatic/manual/Viewer evaluation paths remain appropriate. Only the statistic derived from comparable historical cluster totals needs to change.

## Decision drivers

- Reduce the influence of a single extreme historical cluster while retaining a high anomaly threshold.
- Keep strict equality behavior so a current cluster exactly at the threshold remains filtered.
- Produce deterministic results for one, few, or many comparable clusters.
- Keep the statistic explainable in Viewer diagnostics and independent of a new runtime option.
- Preserve the complete-baseline fail-closed rule and all current history acquisition boundaries.

## Considered options

- Retain the historical maximum. This is simplest and most conservative but allows one outlier to dominate the entire 30-day window.
- Use the nearest-rank 95th percentile. This is deterministic but changes discontinuously as samples are added and can equal the maximum for small histories.
- Use the inclusive, linearly interpolated 95th percentile. This is a common percentile definition, behaves continuously between ordered samples, and has a clear one-sample result.
- Use a mean and standard-deviation score. This can model dispersion but is less robust to skew, harder to explain, and introduces additional threshold choices.

## Decision

Retain the 30-completed-day in-memory baseline, current-first backfill, fail-closed readiness, member-radius spatial matching, current-ID removal, daily reclustering, and shared evaluation paths established by ADR 0008.

For every current cluster, collect the available total FRP values from all spatially comparable rebuilt historical clusters and sort them in ascending order. Compute the 95th percentile at zero-based position `(sample count - 1) * 0.95`, linearly interpolating between the surrounding values. A single comparable value is its own percentile threshold. The current total FRP must be available and strictly greater than that threshold; equality fails. Historical clusters without available total FRP remain excluded, and no comparable historical value still passes.

Diagnostics report the current total, the historical 95th-percentile value, the comparable cluster/day counts, and whether the strict comparison passed. The percentile is fixed rather than configurable.

## Consequences

- A single historical maximum no longer necessarily blocks a current cluster whose FRP is above the rest of the recent distribution.
- The threshold is never greater than the historical maximum, so some clusters previously filtered by the maximum rule can now pass.
- With few comparable samples, the interpolated percentile remains close to the largest values and can still be strongly influenced by an outlier.
- Multiple comparable clusters on one date each contribute a sample, preserving the existing cluster-based rather than day-maximum comparison unit.
- Baseline completeness, acquisition work, API contracts, spatial matching, and notification lifecycle behavior do not change.

## Validation or evidence

- [Historical-FRP policy tests](../../tests/NotificationHistoricalFrpPolicyTests.cs) verify linear interpolation, strict equality, one-sample behavior, spatial matching, self-ID removal, missing FRP, readiness, and disablement.
- [Candidate-engine tests](../../tests/NotificationCandidateEngineTests.cs) verify the shared automatic, manual, eligible-list, and diagnostic integration paths.
- [Notification policy](../domain/notification-policy.md) defines the resulting current domain behavior.

## Related source files and documents

- [Historical-FRP policy](../../src/ThermalWatch.Core/NotificationHistoricalFrpPolicy.cs)
- [Notification candidate engine](../../src/ThermalWatch.Core/NotificationCandidateEngine.cs)
- [Notification policy](../domain/notification-policy.md)
- [Operations](../operations.md)
- [Web viewer](../components/web-viewer.md)

## Supersedes / Superseded by

- Supersedes: [0008](0008-use-in-memory-daily-firms-baseline.md).
- Superseded by: [0010](0010-require-substantial-historical-frp-excess.md).
