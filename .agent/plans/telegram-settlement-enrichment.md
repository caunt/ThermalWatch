# Telegram settlement enrichment

## Purpose and observable outcome

Telegram main posts should append the exact mapped settlement containing the representative thermal-anomaly coordinate after the cluster country list. A mapped city, town, or village appears as `Country, Settlement`; rural coordinates, unavailable lookups, and uncertain results retain the current country-only line. Settlement context remains presentational and must not affect notification eligibility, ranking, lifecycle, nearby-feature ordering, Viewer contracts, or `/api/anomalies`.

## Context and repository orientation

Core already performs one serialized, cached OpenStreetMap Overpass lookup for the representative of each automatic candidate and each selected manual candidate through `src/ThermalWatch.Core/NearbyFeatureClient.cs`. `NotificationCandidateEngine` attaches up to five results to `PreparedNotificationCandidate`, and `TelegramNotificationService` passes that prepared data to `TelegramMessageFormatter`. Viewer diagnostics use the existing public nearby-feature lookup and must remain unchanged. Durable behavior is routed through `docs/README.md`; the relevant documents are architecture, operations, notification policy, and Telegram notifier guidance.

## Progress

- [x] 2026-07-26: Inspected repository guidance, routed documentation, source, tests, worktree, and the live feasibility of Overpass `is_in` settlement-area queries.
- [x] 2026-07-26: Extended the cached Overpass lookup with exact containing-settlement context.
- [x] 2026-07-26: Propagated optional settlement data through prepared candidates and Telegram formatting.
- [x] 2026-07-26: Added and updated focused tests; all 39 targeted tests passed.
- [x] 2026-07-26: Synchronized affected durable documentation and passed all 7 documentation validation tests.
- [x] 2026-07-26: Completed focused validation and the full restore/build/test/format sequence; all 282 repository tests passed.

## Surprises and discoveries

- The repository retains several completed ExecPlans under `.agent/plans`; this plan uses a distinct task-specific filename and does not modify them.
- The existing Overpass request is already made at the correct post-eligibility/post-ranking point, so settlement context can share the request, cache, serialization, and failure boundary without a new provider or configuration.

## Decision log

- Decision: Accept only exact enclosing OSM area boundaries tagged `place=city`, `place=town`, or `place=village`; do not approximate the nearest settlement. Reason: this implements the requested omission for random/rural points. Date: 2026-07-26. Consequence: unmapped settlements are omitted rather than guessed.
- Decision: Prefer `name:en`, then `name`, and choose overlapping boundaries deterministically by highest numeric `admin_level`, then village/town/city specificity and stable area identity. Reason: messages use English country labels while still supporting settlements without English names. Date: 2026-07-26. Consequence: one stable representative settlement is shown.
- Decision: Preserve public nearby-feature and formatter entry points and add settlement-aware internal paths. Reason: the feature does not require breaking consumers of the current helpers. Date: 2026-07-26. Consequence: existing call sites can continue to omit settlement explicitly.
- Decision: No ADR. Reason: this is a focused extension of the accepted Overpass presentation-context boundary, without a new durable architectural alternative. Date: 2026-07-26.

## Concrete implementation steps

1. In Core, introduce an internal mapped-context result containing an optional settlement name and nearby features. Extend the Overpass query with an `is_in` area selection for named city/town/village boundaries, parse area tags independently from nearby node/way/relation elements, and cache the combined result under the existing rounded coordinate key and durations. Keep `FindNearbyAsync` as a compatibility wrapper.
2. Add an optional init-only `SettlementName` property to `PreparedNotificationCandidate` so its positional constructor remains unchanged. Have automatic and selected manual candidate preparation use the combined lookup and attach both context values. Leave startup priming, unselected manual candidates, Viewer eligibility, and diagnostic response contracts unchanged.
3. Extend internal Telegram formatting to accept the optional settlement. Keep existing public `Format` overloads forwarding `null`, add settlement-aware overloads as needed by tests, and render an HTML-encoded compacted suffix after the country list. Pass the prepared property from the delivery service.
4. Expand fake-handler tests for query shape, exact settlement parsing, name fallback, deterministic overlap resolution, failure/cache behavior, representative-only enrichment, omission, escaping, multi-country output, and caption bounds. Update prepared-candidate fixtures.
5. Update the root README and routed architecture, operations, notification-policy, and Telegram-notifier documents. Do not change `docs/README.md` because document purposes and routing remain unchanged.

## Validation and acceptance criteria

Run focused tests for `NearbyFeatureClientTests`, `NotificationCandidateEngineTests`, `TelegramMessageFormatterTests`, and `TelegramNotificationDeliveryTests`; all must pass without live services. Run the documentation drift test, `git diff --check`, and the complete sequence:

    dotnet restore ThermalWatch.slnx
    dotnet build ThermalWatch.slnx -c Release --no-restore --nologo
    dotnet test ThermalWatch.slnx -c Release --no-build --nologo
    dotnet format ThermalWatch.slnx --verify-no-changes --no-restore

Acceptance requires exact `Country, Settlement` output for a containing city/town/village, unchanged country-only output otherwise, one Overpass request per uncached coordinate, preserved fail-open delivery, no Viewer/API contract change, aligned documentation, and a clean validation result.

## Recovery or rollback guidance

All changes are source, test, documentation, and this working plan. They are idempotent and have no migration or irreversible operation. If interrupted, inspect `git diff` and resume from the Progress checklist. Reverse only task-owned hunks; never discard unrelated user work or other retained ExecPlans.

## Outcomes and retrospective

ThermalWatch now shares one cached Overpass request between nearby-feature and exact containing-settlement enrichment. Automatic and selected manual candidates carry the representative's city, town, or village when mapped, and Telegram renders it after the unchanged cluster country list. Rural/unmapped coordinates and provider failures preserve the country-only post without affecting eligibility or delivery. Existing public nearby-feature and formatter entry points, Viewer diagnostic JSON, and anomaly APIs remain unchanged. Focused tests, documentation validation, Release build, all 282 repository tests, format verification, and diff checks passed. No migration, configuration, new dependency, ADR, or follow-up work remains.
