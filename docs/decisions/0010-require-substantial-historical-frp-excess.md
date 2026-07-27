# 0010: Require substantial historical FRP excess

## Status

Accepted

## Context

[ADR 0009](0009-use-95th-percentile-historical-frp-threshold.md) reduced the influence of isolated historical outliers by comparing current total cluster FRP with the inclusive, linearly interpolated 95th percentile of comparable historical cluster totals. Merely exceeding that percentile can still admit a current cluster whose absolute and relative increase over the local baseline is small.

The retained baseline, percentile calculation, readiness gate, spatial matching, missing-FRP behavior, and shared automatic/manual/Viewer evaluation paths remain appropriate. The threshold derived from the percentile needs to require a meaningful increase in both proportional and absolute terms.

## Decision drivers

- Require a substantial relative increase over locations with high historical FRP.
- Require a substantial absolute increase over locations with low historical FRP.
- Preserve strict equality behavior and deterministic diagnostics.
- Avoid adding runtime configuration for a fixed domain-policy choice.
- Preserve existing history acquisition, matching, and failure boundaries.

## Considered options

- Retain the direct p95 comparison. This preserves more candidates but permits marginal increases over the historical baseline.
- Apply only a `1.5` multiplier. This scales with the baseline but produces a small absolute margin at low-FRP locations.
- Apply only a `75 MW` offset. This gives a stable absolute margin but becomes proportionally weak at high-FRP locations.
- Require both the multiplier and offset comparisons. This enforces the stronger derived threshold for every historical p95 value.

## Decision

Retain the 30-completed-day baseline and inclusive, linearly interpolated 95th-percentile calculation established by ADR 0009. When comparable historical FRP exists, require current total cluster FRP to be strictly greater than both `historical p95 × 1.5` and `historical p95 + 75 MW`. Equality with either threshold fails. This is equivalent to requiring the current total to exceed the larger derived threshold.

The multiplier and offset are fixed policy constants. Diagnostics report the current total, historical p95, both derived thresholds, and the comparison outcome. No comparable historical FRP continues to pass; incomplete history and unavailable current FRP retain their existing fail-closed behavior.

## Consequences

- Locations below a historical p95 of `150 MW` are governed by the larger additive threshold.
- Locations above a historical p95 of `150 MW` are governed by the larger multiplicative threshold; both thresholds equal `225 MW` at the crossover.
- Fewer clusters qualify than under the direct p95 comparison, across automatic, manual, and Viewer paths.
- API schemas, baseline acquisition, spatial indexing, percentile calculation, and notification lifecycle behavior do not change.

## Validation or evidence

- [Historical-FRP policy tests](../../tests/NotificationHistoricalFrpPolicyTests.cs) verify strict boundaries below, at, and above the threshold crossover, percentile interpolation, spatial matching, missing FRP, readiness, and disablement.
- [Candidate-engine tests](../../tests/NotificationCandidateEngineTests.cs) verify the shared automatic, manual, eligible-list, and diagnostic integration paths.
- [Notification policy](../domain/notification-policy.md) defines the resulting current domain behavior.

## Related source files and documents

- [Historical-FRP policy](../../src/ThermalWatch.Core/NotificationHistoricalFrpPolicy.cs)
- [Notification candidate engine](../../src/ThermalWatch.Core/NotificationCandidateEngine.cs)
- [Notification policy](../domain/notification-policy.md)
- [Operations](../operations.md)
- [Web viewer](../components/web-viewer.md)

## Supersedes / Superseded by

- Supersedes: [0009](0009-use-95th-percentile-historical-frp-threshold.md).
- Superseded by: None.
