# 0005: Eligibility-based startup incident suppression

## Status

Accepted

## Context

[ADR 0004](0004-snapshot-based-notification-evaluation.md) established complete-snapshot reevaluation without retained unsent candidates, but represented default startup suppression as a fixed set containing every detection ID from the first ready snapshot. Core skipped clusters composed only of those IDs before applying notification policy.

That detection-level baseline conflated observations with notification incidents. In particular, a startup cluster blocked because required exact-date GIBS imagery was unavailable stayed suppressed after imagery recovered: its detections remained baseline IDs, so automatic processing never reevaluated its content eligibility. Viewer diagnostics could correctly report the current cluster eligible while automatic lifecycle state still skipped it.

Startup suppression must prevent a restart flood of incidents that are already eligible without preventing an initially ineligible incident from becoming a notification later. The service must retain no unsent candidate copy, and Core must remain the lifecycle owner.

## Decision drivers

- Suppression state must represent notification incidents rather than all raw startup observations.
- Required-preview recovery and other eligibility changes must remain retryable from later complete snapshots.
- An incident already eligible at startup must remain suppressed when related detections later extend it.
- Default restarts must not send every currently eligible incident.
- The solution must preserve snapshot-triggered evaluation, bounded in-memory state, and the Core/Telegram boundary.

## Considered options

- Retain the fixed startup detection-ID baseline. This is inexpensive and prevents restart floods, but it permanently blocks policy reevaluation for unchanged startup observations and does not model incident continuity.
- Record every first-snapshot cluster as a startup incident without evaluating it. This models episode continuity but still suppresses clusters that are not yet eligible.
- Evaluate first-snapshot clusters and record only eligible ones as startup incidents. This prevents an immediate restart flood, preserves later eligibility recovery, and uses the same incident rules as delivery deduplication.
- Enable `TELEGRAM_NOTIFY_EXISTING_ON_STARTUP` by default. This makes every initially ineligible cluster retryable but can immediately send all eligible incidents after a restart and removes the existing safe default.

## Decision

When `TELEGRAM_NOTIFY_EXISTING_ON_STARTUP` is false, Core evaluates every cluster in the first ready snapshot through the enabled metadata, land-cover, and required-preview policy. It records each eligible cluster as a startup incident without sending it. It does not record an ineligible cluster, so later snapshot publications reevaluate that cluster from current data without retaining a candidate copy.

Startup incidents use the same configured radius, time window, retention, transitive continuation, and bounded detection tracking as delivered episodes, but remain a separate history so operational summaries distinguish `startup incidents suppressed` from duplicate delivered episodes. A related later detection extends an existing startup incident rather than making it deliverable; a new incident outside the relationship limits remains eligible for normal processing.

When `TELEGRAM_NOTIFY_EXISTING_ON_STARTUP` is true, the first ready snapshot follows ordinary automatic processing and can deliver eligible incidents. Viewer and manual evaluations remain lifecycle-independent. GIBS availability changes do not create their own notification trigger; the next consumed FIRMS snapshot remains the reevaluation boundary.

## Consequences

- A startup incident blocked by required imagery can send after imagery becomes available and a later snapshot is published.
- Startup performs policy and required-preview work before deciding what to suppress, so default startup processing can take longer than fixed-ID capture.
- Already eligible startup incidents remain silent across related later detections instead of becoming eligible merely because their membership changed.
- Core retains two bounded episode histories—startup-suppressed and delivered—but still retains no unsent candidate, preview queue, or retry deadline.
- Aggregate logs count suppressed startup incidents rather than raw startup detections, and rejected startup clusters appear in the normal per-criterion summary.

## Validation or evidence

- [Candidate-engine tests](../../tests/NotificationCandidateEngineTests.cs) cover required-preview recovery for an initially ineligible startup incident, suppression and transitive extension of an eligible startup incident, startup-enabled delivery, and Viewer lifecycle independence.
- [Episode-history tests](../../tests/NotificationEpisodeHistoryTests.cs) cover cross-satellite continuation, transitive extension, radius/time boundaries, expiry, and untracked incidents.
- [Notification policy](../domain/notification-policy.md) defines the resulting automatic lifecycle and criteria-only Viewer boundary.

## Related source files and documents

- [Core candidate engine](../../src/ThermalWatch.Core/NotificationCandidateEngine.cs)
- [Episode history](../../src/ThermalWatch.Core/NotificationEpisodeHistory.cs)
- [Processing summary](../../src/ThermalWatch.Core/NotificationProcessingSummary.cs)
- [Telegram notifier](../components/telegram-notifier.md)
- [Operations](../operations.md)

## Supersedes / Superseded by

- Supersedes: [0004](0004-snapshot-based-notification-evaluation.md).
- Superseded by: None.
