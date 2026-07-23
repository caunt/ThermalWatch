# 0002: Core-owned notification candidates

## Status

Accepted

## Context

Notification clustering already depended on Core models, geography, and GIBS access, but active-context clustering, filters, candidate lifecycle, preview selection, and ranking lived in `ThermalWatch.Telegram`. That made the Telegram adapter the only place able to explain why current anomalies did or did not become candidates. The Viewer needs the same rules for an interactive selected-anomaly diagnostic without depending on Telegram or duplicating policy in JavaScript.

The dependency graph requires both Viewer and Telegram to depend on Core, never on each other. `/api/anomalies` must remain the complete raw-observation boundary, and diagnostic evaluation must not mutate automatic notification state.

## Decision drivers

- Clustering, thresholds, land-cover behavior, preview requirements, and ranking need one source of truth.
- Viewer and Telegram cannot reference each other or introduce provider-specific naming into Core.
- Automatic seen/delivered/pending behavior and manual-send semantics must remain compatible.
- The framework-free browser must consume a bounded same-origin diagnostic contract rather than reimplementing domain rules.
- Existing `TELEGRAM_*` deployment keys must remain compatible even though policy ownership changes.

## Considered options

- Keep policy in Telegram and make Viewer depend on it. This would invert the intended component boundary and couple a read-only UI to a transport adapter.
- Duplicate enough rules in Viewer or its endpoint. This would create divergent clustering/filter behavior and require browser or Viewer code to understand GIBS policy.
- Move only stateless filters into Core. This would still leave candidate clustering context, lifecycle, preview selection, and manual ranking owned by the transport project.
- Move the complete anomaly-to-prepared-candidate pipeline into Core and leave message construction/delivery in Telegram. Both consumers then share one neutral policy boundary.

## Decision

Core owns all behavior that turns anomaly snapshots into prepared notification candidates: clustering context, representative selection, visibility and land-cover rules, preview selection/acquisition, automatic seen/delivered/pending lifecycle, preview retry handling, and manual ranking.

Telegram owns only Telegram credentials/channel validation, message construction, outbound transport, and transport-specific acknowledgement/error mapping. Its hosted service passes snapshots to Core and supplies a delivery callback.

Viewer exposes a read-only selected-anomaly diagnostic backed directly by Core. It clusters the complete current snapshot and exhaustively reports every criterion without reading or mutating automatic state. Core and Viewer use neutral `Notification*` names. The API configuration layer alone maps retained `TELEGRAM_*` policy environment keys into Core options.

## Consequences

- Automatic, manual, and Viewer paths share clustering, thresholds, land cover, GIBS preview, and representative behavior.
- Viewer selection can make bounded cached GIBS requests and may be slower while land-cover or exact-preview data is checked.
- Core owns additional in-memory notification state and public candidate/diagnostic types, while remaining independent of Telegram and Viewer.
- Telegram becomes a smaller outbound adapter but must report delivery outcomes so Core can acknowledge or retain pending candidates.
- The unauthenticated HTTP surface gains a read-only diagnostic endpoint that exposes non-secret configured requirements and current policy results.
- Existing environment names, `/api/anomalies`, message formatting, and restart semantics remain compatible.

## Validation or evidence

- [Core policy tests](../../tests/NotificationPolicyTests.cs) verify exhaustive criterion reporting.
- [Core candidate-engine tests](../../tests/NotificationCandidateEngineTests.cs) verify active clustering and diagnostic non-mutation.
- [Automatic lifecycle tests](../../tests/NotificationAutomaticStateTests.cs) preserve delivered-episode and pending behavior.
- [Viewer endpoint tests](../../tests/ViewerNotificationDiagnosticEndpointTests.cs) verify the HTTP cluster/criteria contract.
- [Browser support tests](../../tests/viewer-map-support.test.js) verify provider-neutral cluster member mapping and marker styles.

## Related source files and documents

- [Core candidate engine](../../src/ThermalWatch.Core/NotificationCandidateEngine.cs)
- [Telegram adapter](../../src/ThermalWatch.Telegram/TelegramNotificationService.cs)
- [Viewer endpoints](../../src/ThermalWatch.Viewer/ViewerEndpoints.cs)
- [Architecture](../architecture.md)
- [Notification policy](../domain/notification-policy.md)
- [Web viewer](../components/web-viewer.md)

## Supersedes / Superseded by

- Supersedes: None.
- Superseded by: None.
