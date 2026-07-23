# Shared notification candidate debugging

## Purpose and observable outcome

Move the complete anomaly-to-candidate pipeline into `ThermalWatch.Core` so Telegram only validates its transport, builds messages, and sends them. Add a read-only Viewer diagnostic that clusters a selected anomaly against the active snapshot, highlights every member, and explains all notification criteria without mutating automatic notification state. Preserve existing notification behavior, environment names, and `/api/anomalies` output.

## Context and repository orientation

Core already owns `NotificationClustering`, anomaly models, snapshots, and NASA GIBS access. Telegram currently owns active-context clustering, visibility and land-cover filters, preview selection, seen/delivered/pending state, manual ranking, and delivery orchestration. Viewer is a framework-free static-web-assets library with provider-neutral selection in `wwwroot/app.js` and endpoints in `ViewerEndpoints.cs`. The durable references are `docs/architecture.md`, `docs/domain/notification-policy.md`, `docs/components/telegram-notifier.md`, `docs/components/web-viewer.md`, `docs/operations.md`, and `docs/development.md`.

## Progress

- [x] Move options, rules, state, preview preparation, and candidate orchestration into Core. (2026-07-23T16:48:55Z)
- [x] Adapt Telegram and application composition without configuration or notification behavior regressions. (2026-07-23T16:48:55Z)
- [x] Add the selected-anomaly diagnostic endpoint and cluster-aware Viewer UI. (2026-07-23T16:48:55Z)
- [x] Move and extend tests, update durable documentation, and add the boundary ADR. (2026-07-23T17:07:52Z)
- [x] Complete focused, full, publish, and screenshot validation. (2026-07-23T17:20:34Z)

## Surprises and discoveries

- 2026-07-23: Repository analyzers require each namespace-level type to live in a matching file; the new Core contracts were split accordingly.
- 2026-07-23: The user clarified that neither Core nor Viewer may contain Telegram naming. Legacy `TELEGRAM_*` keys are therefore translated only in the API configuration layer.
- 2026-07-23: Live narrow-to-desktop verification exposed stale Leaflet dimensions and misaligned tiles after crossing the responsive breakpoint. The provider contract now includes animation-frame-throttled resize handling for both Leaflet and Google Maps.

## Decision log

- 2026-07-23: Core owns everything from anomalies through prepared candidates, including automatic lifecycle and manual ranking. Telegram retains credentials, formatter, transport, and transport errors. This is the boundary explicitly requested by the user.
- 2026-07-23: Viewer diagnostics use all current snapshot observations, evaluate all criteria, perform live cached land-cover and exact-preview checks, and never inspect or mutate automatic state.
- 2026-07-23: Existing `TELEGRAM_*` names and defaults remain compatibility contracts even though most parsed values become Core notification options.
- 2026-07-23: Core and Viewer use only neutral `Notification*` identifiers and routes; Telegram remains a provider adapter at the outer boundary.
- 2026-07-23: The browser controller schedules one resize per animation frame and delegates provider-specific refresh behavior through the adapter boundary, preserving a provider-neutral responsive workflow.

## Concrete implementation steps

1. Introduce Core notification option/result/candidate types, move current clustering extensions, filters, automatic state, and preview selection, and implement a candidate engine for automatic, manual, and diagnostic evaluation.
2. Refactor `TelegramNotificationService` to consume prepared Core candidates, provide transport delivery outcomes, and retain only validation, formatting, sending, and transport-specific summaries/logging.
3. Register the Core engine and split policy versus transport configuration in the API host. Add `GET /api/viewer/notification-diagnostics/{anomalyId}` without changing `/api/anomalies`.
4. Extend Viewer controller/provider selection to fetch diagnostics, guard stale requests, render cluster members consistently, list structured criteria in the inspector, and refresh provider dimensions across responsive layout changes.
5. Move existing behavioral tests to Core-facing names, add diagnostic endpoint and UI tests, then reconcile routed documentation and add ADR 0002.

## Validation and acceptance criteria

Run both JavaScript syntax checks and the Node test suite, focused .NET tests while iterating, then the documented restore/build/test/format sequence. Run documentation validation, `git diff --check`, Release publish/static asset assertion, and the live Viewer screenshot workflow for NASA and Google at desktop and narrow widths. A selected multi-member anomaly must highlight its full cluster and show every criterion; a singleton must highlight only itself; provider switching and snapshot refresh must preserve a valid selection; diagnostics must never send or mutate automatic notification state.

## Recovery or rollback guidance

All changes are source-only and idempotent. Re-run focused tests after each boundary step. If the refactor is interrupted, use this plan and `git diff` to distinguish completed moves from remaining adapters; never discard unrelated work. No migrations, external writes, or irreversible operations are involved.

## Outcomes and retrospective

The anomaly-to-candidate boundary now belongs entirely to Core under neutral `Notification*` names. Telegram consumes prepared candidates and retains only transport validation, formatting, sending, and delivery-outcome mapping. The Viewer exposes a read-only diagnostic for a selected active anomaly, highlights its complete cluster consistently across both providers, and renders all seven policy outcomes without mutating automatic state.

Repository verification completed with zero build warnings, 87 passing .NET tests, 19 passing Node tests, seven passing focused documentation checks, clean formatting/diff checks, a successful Release publish with root-mounted Viewer assets, and a case-insensitive scan proving that Core and Viewer contain no Telegram naming. A Telegram-disabled live run against 157 current UKR observations verified a four-member cluster and a singleton in NASA and Google desktop/narrow layouts. All ten screenshots under `/tmp/thermalwatch-viewer-final-aqppExTa/` were opened and inspected; provider switching, narrow-to-desktop resizing, marker highlighting, criteria readability, imagery, footer bounds, and horizontal overflow were clean.

Cold diagnostics can still take a few seconds because enabled land-cover and exact-preview criteria make bounded GIBS requests; cache hits in the same live run completed in milliseconds. This is an intentional consequence documented in the architecture, operations, domain-policy, and Viewer component guidance rather than an unresolved implementation gap.
