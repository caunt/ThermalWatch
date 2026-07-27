# FIRMS history and historical-FRP notification filter

## Purpose and observable outcome

ThermalWatch will retain an immutable in-memory FIRMS history for the current UTC day plus the preceding 30 completed UTC days. Startup will publish the current active snapshot first, then backfill the preceding dates through bounded, dated FIRMS requests. `GET /api/history` will expose daily raw anomalies, raw clusters, and completeness diagnostics. Notification eligibility will, by default, require a current cluster's total FRP to be strictly greater than every spatially matching historical cluster with available total FRP.

The history is rebuilt after every restart, uses the same configured countries and four NRT sources as active ingestion, and does not add persistent storage, SP products, Landsat, or a history visualization.

## Context and repository orientation

`src/ThermalWatch.Core/FirmsClient.cs` currently requests only a latest calendar-day range derived from `FIRMS_ACTIVE_WINDOW`. `src/ThermalWatch.Api/FirmsRefreshCycle.cs` publishes complete country/source results into `AnomalySnapshotStore`; the store then notifies the single Telegram consumer. `NotificationCandidateEngine` builds current clusters and applies the shared automatic, manual, eligible-list, and diagnostic policy. Viewer cluster summaries currently expose representative and aggregate fields but not member IDs.

The governing documentation is `docs/architecture.md`, `docs/components/firms-ingestion.md`, `docs/domain/notification-policy.md`, `docs/operations.md`, `docs/components/telegram-notifier.md`, and `docs/components/web-viewer.md`. The required validation sequence is in `docs/development.md`. The durable choice will be recorded as ADR 0008.

## Progress

- [x] 2026-07-27T14:46:10Z: Confirmed the worktree is clean, read repository guidance, routed documentation, current source/tests, the ExecPlan standard, and the documentation-maintenance skill.
- [x] 2026-07-27T15:07:25Z: Added date-aware FIRMS acquisition and immutable daily history state.
- [x] 2026-07-27T15:07:25Z: Orchestrated current-first startup, bounded backfill, retries, and incremental history publication.
- [x] 2026-07-27T15:07:25Z: Added the history HTTP contract and shared cluster summaries.
- [x] 2026-07-27T15:07:25Z: Added the historical-FRP criterion to all notification evaluation paths and preserved startup suppression semantics.
- [x] 2026-07-27T15:16:23Z: Updated all affected routed documentation and added/registered accepted ADR 0008; `docs/README.md` routing remains unchanged.
- [x] 2026-07-27T15:33:04Z: Passed focused and complete automated verification, Release publish/static-asset checks, credential-safe live history/backfill checks, and opened desktop/narrow NASA/Google screenshots for ready, unavailable, and disabled diagnostic states.

## Surprises and discoveries

- NASA FIRMS dated area requests accept at most five days, so 30 completed dates require exactly six dated windows per country/source.
- Current notification clusters can contain anomalies from historical daily buckets because the active window crosses UTC dates. Historical comparison must remove current member IDs and recluster each affected day to avoid self-comparison.
- The first ready snapshot cannot consume startup-incident lifecycle state while enabled history is unavailable, or existing incidents could become sendable only because backfill completed.
- The live validation provider entered the client's verified country-feature outage path, so current and historical UKR acquisition also exercised polygon-clipped area fallback without affecting history readiness or the current-first order.

## Decision log

- Decision: Retain 30 completed UTC dates plus a live current-date bucket in memory and rebuild it on startup. Reason: this supplies the requested baseline while preserving the repository's stateless runtime. Date: 2026-07-27. Consequence: startup performs bounded external work and restart loses/rebuilds history.
- Decision: Use only the existing MODIS and three VIIRS NRT sources. Reason: historical and current clusters must have the same sensor universe and parser contracts. Date: 2026-07-27. Consequence: SP products and Landsat remain out of scope.
- Decision: Fail the enabled criterion closed until every required historical country/source/date slice is complete and fresh. Reason: a partial baseline could misclassify an always-hot source as novel. Date: 2026-07-27. Consequence: FIRMS history failures pause notification eligibility but not current APIs.
- Decision: Match locations when any current and historical members are within the configured cluster radius, and compare strict total-FRP maxima after excluding current IDs and reclustering. Reason: this is robust for single-point clusters and consistent with current spatial clustering. Date: 2026-07-27. Consequence: equality fails; historical null FRP values are ignored; current null total FRP fails.
- Decision: Add member IDs to the shared latest/history cluster summary. Reason: consumers must relate daily clusters to the separately exposed anomaly array without duplicating full anomaly objects. Date: 2026-07-27. Consequence: the eligible-cluster JSON contract gains one additive field.

## Concrete implementation steps

1. Introduce an explicit FIRMS request window and update `FirmsClient` country/area paths to support latest and dated one-to-five-day requests while retaining capability probing, fallback, parsing, clipping, concurrency, and timeout behavior.
2. Add immutable history models and a synchronized history store keyed by UTC date and country/source. Store complete daily slices, segment status, raw daily clusters, readiness, staleness, and 31-date rotation. Successful covered ranges replace even empty slices; failures retain prior slices and mark them stale.
3. Extend refresh results with their covered UTC dates. Publish active results to history before the active snapshot, then have the poller attempt six five-day backfill windows after the first current cycle and retry incomplete windows after later cycles without feeding failures into active-cycle backoff.
4. Add `GET /api/history` with optional inclusive `from`/`to` dates, retained-range and 31-date validation, chronological days, partial `200` responses, and per-day segment diagnostics. Reuse a shared cluster-summary type and add `memberIds` to current eligible summaries.
5. Add `NOTIFICATION_HISTORICAL_FRP_FILTER_ENABLED` with default `true`. Implement a pure historical-FRP evaluator and integrate it after metadata and before land-cover/preview work for automatic, manual, Viewer-list, and diagnostic paths. Keep the first-ready lifecycle pending while enabled history is unavailable.
6. Add deterministic client, history-store, orchestration, endpoint, policy, candidate-engine, configuration, and contract tests. Update routed documentation and add/register ADR 0008.

## Validation and acceptance criteria

- Focused tests prove dated five-day windows, current-first startup, six-window backfill, retries, replacement/retention, UTC rotation, all four NRT sources, raw daily clustering, query validation, shared member IDs, strict FRP comparison, spatial radius matching, self-ID exclusion/reclustering, missing-FRP handling, fail-closed readiness, disabling, all evaluation paths, and deferred startup priming.
- `GET /api/history` returns current day plus 30 completed dates by default, `200` partial status during failures, and no request triggers FIRMS acquisition.
- Viewer diagnostics show a ninth historical-FRP criterion and existing viewer behavior remains usable.
- Run `dotnet restore ThermalWatch.slnx`, Release build/test, format verification, documentation validation, JavaScript checks, `git diff --check`, and required desktop/narrow NASA/Google screenshot inspection for affected diagnostic states.

## Recovery or rollback guidance

All changes are additive and in memory. Stop the process normally to cancel current/backfill requests. Retrying startup is idempotent because successful history slices replace complete date/source/country cells and anomaly IDs deduplicate deterministically. Revert only task-owned files if rollback is required; do not discard unrelated worktree changes. No data migration or irreversible operation exists.

## Outcomes and retrospective

ThermalWatch now retains and serves the current UTC day plus 30 completed dates of raw FIRMS measurements and raw cluster summaries from all four existing NRT sources. Startup publishes the current snapshot first, fills incomplete history through bounded five-day requests, retries stale ranges, and rotates at UTC rollover. The default historical location FRP criterion is shared by every notification evaluation path, removes current IDs before daily reclustering, spatially matches any member pair within the configured radius, and requires strict total-FRP novelty against a complete baseline.

Deterministic evidence includes 128 focused FIRMS/history/policy/candidate tests, the complete 311-test Release suite, seven documentation checks, 36 Viewer Node tests, zero-warning build and clean format/diff checks, and a Release publish containing root-mounted assets. A Telegram-disabled live run used 979 current UKR observations and completed a 30-day backfill through verified area fallback. Seventeen screenshots under `/tmp/thermalwatch-history-visual.5OuJ9d/` were opened: NASA and Google desktop/narrow views, provider round trips, and ready-failed, history-unavailable, and disabled historical criteria were readable with nine total criteria and no 390 px overflow. The ignored mode-600 `.env` remained unmodified and no live Telegram operation occurred.

Durable behavior was updated in `README.md`, `docs/architecture.md`, `docs/components/firms-ingestion.md`, `docs/domain/notification-policy.md`, `docs/operations.md`, `docs/components/telegram-notifier.md`, and `docs/components/web-viewer.md`; ADR 0008 records the significant choice and is registered in `docs/decisions/README.md`. `docs/README.md`, `docs/development.md`, `AGENTS.md`, and existing ADRs remain unchanged because routing, toolchain/workflow, repository-wide instructions, and historical decisions did not change.

The deliberate limitations remain: history is lost and rebuilt on restart, spans only the existing NRT feeds and 30 completed dates, has no viewer chart/map, and does not itself trigger notification processing when backfill completes; the next current snapshot publication performs the baseline-ready evaluation. The implementation avoided persistent storage and preserved current anomaly availability and segment isolation while the baseline is incomplete.
