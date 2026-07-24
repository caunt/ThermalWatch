# 0004: Snapshot-based automatic notification evaluation

## Status

Superseded

## Context

The Core-owned automatic lifecycle selected in [ADR 0002](0002-core-owned-notification-candidates.md) marked detection IDs seen before filtering and retained accepted clusters in a pending list while exact-date imagery was unavailable. That made a preview retry deadline necessary: without the pending copy, an unchanged but still-active cluster was excluded from later processing because none of its detections was new.

The immutable FIRMS snapshot already contains every active observation and is published after each polling cycle. Rejected clusters, changing GIBS availability, and transient delivery failures can therefore be reevaluated from current source data instead of from retained notification candidates. Successful deliveries still require episode history to prevent repeated messages, and the default startup behavior must still avoid notifying every observation after a restart.

## Decision drivers

- Automatic decisions should use the complete current snapshot rather than a stored copy of an earlier cluster.
- Missing exact-date imagery and transient sends must be able to recover on later snapshot publications.
- Five-minute reevaluation must not resend an already delivered ongoing episode.
- Default startup behavior must avoid a restart flood without restoring general seen-ID processing.
- Core must remain the single policy owner while Telegram remains a delivery adapter.

## Considered options

- Retain new-detection selection, pending candidates, and a preview retry deadline. This preserves the previous behavior but keeps duplicated unsent state and can discard a still-active cluster at an arbitrary deadline.
- Remove pending candidates while continuing to evaluate only new detections. This loses unchanged clusters after their first failed evaluation.
- Cluster and evaluate the complete active snapshot after every publication, retaining only a startup baseline and successful delivered-episode history. This makes retries a consequence of current data and keeps deduplication explicit.

## Decision

Core clusters every observation in each consumed ready snapshot and evaluates every cluster that is neither startup-baseline-only nor part of an already delivered episode. It retains no unsent candidate.

When exact imagery is required but unavailable, the cluster is rejected for that snapshot and can qualify after a later publication. When imagery is optional, the same evaluation sends text immediately. A transient transport failure records no delivery, so the next snapshot reevaluates the cluster. Only `Delivered` establishes or extends delivered-episode history.

When `TELEGRAM_NOTIFY_EXISTING_ON_STARTUP` is false, Core retains the first ready snapshot's IDs as a fixed startup baseline. A cluster containing only baseline detections stays suppressed; any later detection makes the current complete cluster eligible. The legacy `TELEGRAM_SEEN_RETENTION` name remains the delivered-history retention setting. `TELEGRAM_PREVIEW_RETRY_WINDOW` is no longer recognized and an existing value is ignored.

## Consequences

- GIBS availability, live cluster membership, representatives, and filters are reevaluated from current data after every consumed snapshot.
- Core holds no preview queue, retry deadline, or general seen-ID history.
- Complete-snapshot clustering and repeated policy checks do more work than new-detection-only processing; bounded caches and delivered-episode suppression avoid repeated downstream work where possible.
- Optional missing previews now send text during the same cycle instead of waiting for a timeout.
- The Viewer diagnostic remains read-only and describes current eligibility consistently with automatic evaluation.
- Core continues to own candidate policy and lifecycle, and Telegram continues to own only transport-specific behavior.

## Validation or evidence

- [Candidate-engine tests](../../tests/NotificationCandidateEngineTests.cs) cover preview recovery, immediate optional text delivery, transient-send reevaluation, startup-baseline behavior, and diagnostic wording.
- [Episode-history tests](../../tests/NotificationEpisodeHistoryTests.cs) cover once-per-episode suppression, transitive continuation, boundaries, and expiry.
- [Clustering tests](../../tests/NotificationClusteringTests.cs) cover complete active-snapshot connected components and representative selection.
- [Configuration tests](../../tests/ApplicationConfigurationTests.cs) cover delivered retention and ignored retired preview configuration.

## Related source files and documents

- [Core candidate engine](../../src/ThermalWatch.Core/NotificationCandidateEngine.cs)
- [Episode history](../../src/ThermalWatch.Core/NotificationEpisodeHistory.cs)
- [Notification policy](../domain/notification-policy.md)
- [Telegram notifier](../components/telegram-notifier.md)
- [Operations](../operations.md)

## Supersedes / Superseded by

- Supersedes: [0002](0002-core-owned-notification-candidates.md).
- Superseded by: [0005](0005-eligibility-based-startup-incident-suppression.md).
