# 0008: Use an in-memory daily FIRMS baseline

## Status

Accepted

## Context

ThermalWatch evaluates each active FIRMS snapshot independently. A fixed visibility threshold can suppress weak detections, but it cannot distinguish an unusual increase at an industrial location from that location's ordinary recurring heat. The requested discriminator needs observations and clusters from prior dates at the same location.

The service is intentionally stateless: current segments, notification lifecycle state, and caches live only in memory. FIRMS dated requests are limited to five days, the current snapshot must remain available promptly after startup, and a partial baseline must not classify a persistent source as anomalous merely because its stronger prior dates are missing. Active clusters can also contain observations from preceding UTC dates, so a direct comparison with stored daily clusters can accidentally compare an observation with itself.

## Decision drivers

- Compare a current cluster with the existing MODIS and three VIIRS NRT source universe rather than introducing differently normalized historical products.
- Preserve the in-memory deployment model and avoid a database, migration, or persistent-volume requirement.
- Publish current observations promptly while bounding startup request concurrency and FIRMS date ranges.
- Expose enough raw history and diagnostics to audit the baseline independently of notification eligibility.
- Prevent partial history and current-window overlap from producing false novelty.
- Apply one criterion consistently to automatic, manual, eligible-list, and diagnostic evaluation paths.

## Considered options

- Keep only fixed current-snapshot FRP thresholds. This adds no startup work but cannot distinguish persistent heat from a location-specific increase.
- Persist a long-term baseline in a database or object store. This survives restart and can support longer analytics, but adds storage ownership, schema migration, retention, and recovery obligations outside ThermalWatch's current architecture.
- Retain one undivided rolling observation collection. This is simple internally but obscures UTC-day completeness, makes partial upstream results harder to audit, and does not match the requested daily HTTP contract.
- Retain a bounded in-memory daily baseline and rebuild it with dated FIRMS requests. This preserves stateless operation and explicit per-day completeness at the cost of startup requests and temporary notification unavailability.

## Decision

Core retains the current UTC date plus the preceding 30 completed UTC dates in memory. Each date contains one replaceable slice for every configured country and each of the four existing NRT sources. Daily public state exposes raw anomalies, unfiltered connected-cluster summaries, and slice diagnostics. History and current eligible-cluster summaries use the same cluster-summary contract, including member IDs. `GET /api/history` reads that state, supports an inclusive retained date range, and returns partial history with HTTP `200` and explicit readiness/staleness flags; it never initiates FIRMS acquisition.

The poller completes and publishes the current refresh first. It then backfills the 30 completed dates in six five-day windows per country/source, with bounded concurrency, and retries only windows containing incomplete or stale slices after later current cycles. Current results also update every UTC date they cover before publishing the active snapshot. Backfill failures do not contribute to active-cycle exponential backoff. At UTC rollover the retained range rotates. Restart discards and rebuilds all history.

The historical location FRP criterion is enabled by default and fails closed until all 30 completed dates are fresh for every configured country/source; today's live bucket is excluded from readiness and comparison. Automatic processing does not consume first-ready startup-suppression state while that baseline is unavailable. Disabling the criterion removes this readiness gate without disabling history acquisition or its API.

For comparison, Core removes all current cluster member IDs from each historical day and reclusters the remaining daily anomalies. A historical cluster matches the current location when any current/historical member pair is within the configured cluster radius. Historical clusters without any available FRP are ignored. The current total cluster FRP must be available and strictly greater than the maximum total FRP of every comparable historical cluster; equality fails, while no comparable historical FRP passes.

## Consequences

- Persistent hot sources are filtered relative to their own recent location baseline instead of only a global threshold.
- Startup performs 24 dated range requests per configured country on an empty history, in addition to the current requests, and notification eligibility remains unavailable until all required slices succeed.
- Current anomalies and `/api/anomalies` remain available before backfill completes; a history failure does not remove or annotate them.
- The baseline disappears on restart, cannot represent more than 30 completed dates, and cannot support cross-restart trend analysis.
- Daily clusters are recomputed from raw anomalies after self-ID removal during evaluation, adding bounded CPU work but avoiding self-comparison and preserving correct connected components.
- Member-radius matching is consistent with current clustering but is a proximity heuristic, not polygon-intersection analysis or proof that observations share a physical source.
- Adding `memberIds` is an additive change to eligible-cluster summaries and lets clients relate summaries to raw measurements.

## Validation or evidence

- [FIRMS client tests](../../tests/FirmsClientTests.cs) cover dated country and area request paths and the five-day limit.
- [History backfill tests](../../tests/FirmsHistoryBackfillTests.cs) cover current-first orchestration, six-window acquisition, all four sources, retry selection, and failure isolation.
- [History store tests](../../tests/FirmsHistoryStoreTests.cs) cover replacement, staleness, daily raw clustering, readiness, and UTC rotation.
- [History endpoint tests](../../tests/FirmsHistoryEndpointTests.cs) cover the retained daily contract, member IDs, partial responses, and query validation.
- [Historical-FRP policy tests](../../tests/NotificationHistoricalFrpPolicyTests.cs) cover strict comparison, member-radius matching, self-ID removal and reclustering, missing FRP, readiness, and disablement.
- [Candidate-engine tests](../../tests/NotificationCandidateEngineTests.cs) cover automatic, manual, Viewer-list, diagnostic, and startup-suppression integration.

## Related source files and documents

- [History store](../../src/ThermalWatch.Core/FirmsHistoryStore.cs)
- [History backfill](../../src/ThermalWatch.Api/FirmsHistoryBackfill.cs)
- [History endpoint](../../src/ThermalWatch.Api/FirmsHistoryEndpoints.cs)
- [Historical-FRP policy](../../src/ThermalWatch.Core/NotificationHistoricalFrpPolicy.cs)
- [FIRMS ingestion](../components/firms-ingestion.md)
- [Notification policy](../domain/notification-policy.md)
- [Operations](../operations.md)

## Supersedes / Superseded by

- Supersedes: None.
- Superseded by: None.
