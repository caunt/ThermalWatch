# Eligible notification clusters panel

## Purpose and observable outcome

Add a persistent Viewer panel that evaluates the current in-memory snapshot and lists every cluster passing all enabled notification content criteria. A user can activate a cluster row to place its representative coordinates into the existing coordinate search and execute the normal search, URL, map-focus, nearest-selection, and diagnostic flow. The evaluation is read-only: it does not apply or mutate startup-baseline, delivered-episode, or Telegram delivery state.

## Context and repository orientation

`ThermalWatch.Core/NotificationCandidateEngine.cs` already owns clustering, metadata and land-cover filters, exact-date preview requirements, manual ranking, automatic lifecycle, and selected-anomaly diagnostics. `ThermalWatch.Viewer/ViewerEndpoints.cs` exposes the current single-anomaly diagnostic, while `wwwroot/app.js`, `index.html`, and `styles.css` implement a two-column map/inspector viewer and local coordinate search. The durable references are `docs/architecture.md`, `docs/domain/notification-policy.md`, `docs/components/web-viewer.md`, `README.md`, and `docs/development.md`.

## Progress

- [x] Add the Core read-only eligible-cluster query and shared notification-priority ordering. (2026-07-24T08:38:00Z)
- [x] Expose and test the Viewer HTTP contract. (2026-07-24T08:38:00Z)
- [x] Add and test the responsive right-rail panel and search activation. (2026-07-24T08:51:00Z)
- [x] Synchronize durable documentation. (2026-07-24T08:51:00Z)
- [x] Complete focused, full, publish, and live visual validation. (2026-07-24T08:51:00Z)
- [x] Commit the completed change and push `main` to `origin` as `e006726`. (2026-07-24T08:53:00Z)

## Surprises and discoveries

- 2026-07-24: Repository naming rules apply the lower-camel convention to type parameters; the shared generic ranking helper therefore uses `candidateType` rather than the usual `T` prefix.
- 2026-07-24: The authorized live snapshot contained 13,496 observations and 943 connected clusters. Criteria evaluation completed in 9–22 seconds during successful direct/live runs, validating the need for the panel's independent loading state; the map and controls remained usable throughout.

## Decision log

- 2026-07-24: Eligibility means enabled content criteria only. Startup-baseline and delivered-episode suppression remain automatic-delivery lifecycle concerns and are neither read nor changed by the Viewer query.
- 2026-07-24: The list loads automatically and independently during initial load and Refresh so potentially slow bounded GIBS checks do not block the anomaly map.
- 2026-07-24: Results use the existing manual-send priority and the implementation will share that ordering to prevent drift.
- 2026-07-24: Desktop places the list above the inspector in a right rail; narrow layouts stack map, list, and inspector.

## Concrete implementation steps

1. Add Core result records and a `NotificationCandidateEngine` method that clusters one captured snapshot, reuses the current metadata/land-cover evaluation, checks exact-date preview only when required, omits nearby lookups and lifecycle state, and returns eligible summaries ordered by shared notification priority.
2. Add `GET /api/viewer/eligible-notification-clusters`, preserving camel-case JSON and cancellation handling. Extend endpoint and engine tests for filtering, preview/land-cover policy, ordering, summary fields, empty snapshots, nearby isolation, and lifecycle isolation.
3. Add an accessible panel above the inspector with independent loading, ready, empty, and error states. Start and cancel list requests separately from the main snapshot load. Validate response data defensively. On row activation, write canonical six-decimal representative coordinates and call the existing search path.
4. Add provider-neutral JavaScript helper coverage, responsive styling, and durable documentation for the route, policy semantics, costs, failure isolation, and layout.
5. Run all required validation, exercise a real Telegram-disabled local viewer when credentials are available, capture and inspect NASA and Google desktop/narrow states plus provider round-trip and panel interaction, then commit and push the verified work.

## Validation and acceptance criteria

Run focused Core/endpoint tests and the Node suite while iterating, followed by `dotnet restore ThermalWatch.slnx`, Release build/test, format verification, both JavaScript syntax checks, documentation validation, `git diff --check`, and Release publish/static-asset assertion. With a real key when available, run the published host with Telegram variables removed and capture/open NASA and Google screenshots at 1440×900 and 390×844, including a populated cluster click and the initial provider after a provider round trip. Verify loading/empty/error states as practical, no horizontal overflow, panel and inspector scrolling, canonical search input/URL, representative focus, and unchanged provider marker behavior. Finish only after the commit is pushed to `origin/main`.

## Recovery or rollback guidance

All implementation changes are source-only and repeatable. Cancelled eligible-list requests have no external effects. Use this plan and `git diff` to resume partial work; never discard unrelated worktree changes. The only external write is the explicitly requested final Git push, performed after validation. If push fails, retain the local commit and report the remote error without rewriting history.

## Outcomes and retrospective

Core now exposes a criteria-only eligible-cluster query whose deterministic notification priority is shared with manual send. The Viewer route and right-rail panel list those summaries without nearby lookups or delivery lifecycle state, and row activation reuses canonical coordinate search. Loading, empty, failure, and populated list states are isolated from the anomaly map and stack cleanly above the inspector on narrow screens.

Validation completed with zero build warnings, 155 passing .NET tests, 35 passing Node tests, seven passing documentation checks, clean format/diff checks, and a successful Release publish/static-asset assertion. A Telegram-disabled live run used 13,496 current observations; the real endpoint returned zero eligible clusters from 943 evaluated clusters, while a route-controlled passing summary derived from a live representative verified click-through search and URL behavior. All 16 images under `/tmp/thermalwatch-eligible-panel-final-aZAAhw/` were opened and inspected across NASA and Google desktop/narrow layouts, a provider round trip, and populated, empty, error, and loading states. No horizontal overflow, clipped controls, unscrollable content, or provider regression was visible.

Durable behavior is recorded in the root README plus the architecture, operations, notification-policy, and web-viewer documents. No ADR was warranted because the change extends the existing Core/Viewer boundary without introducing a new architectural dependency. Implementation commit `e006726` was pushed to `origin/main`; this plan closure is the only follow-up publication step.
