# FIRMS fallback timeout hardening

## Purpose and observable outcome

ThermalWatch will continue using country-first FIRMS ingestion and atomic area fallback, but a fallback cycle will no longer amplify an intermittent request timeout through unnecessary 10-degree tiling. Operators will observe one polygon-clipped area-envelope request per country/source, a full configured pause after each completed cycle, total-provider-failure backoff, response-body timeouts, and safe timing summaries. Public HTTP contracts, environment names/defaults, snapshot semantics, and notification behavior remain unchanged.

## Context and repository orientation

`src/ThermalWatch.Core/FirmsClient.cs` owns country/area acquisition and its global upstream gate. `src/ThermalWatch.Core/CountryBoundaryCatalog.cs` prepares embedded country geometry and its area-request envelope. `src/ThermalWatch.Api/FirmsPollingService.cs` schedules and publishes country/source segments, while `src/ThermalWatch.Api/Program.cs` composes the FIRMS resilience pipeline. `tests/` contains direct fake-HTTP FIRMS and poller coverage. Durable behavior is routed through `docs/operations.md` and `docs/components/firms-ingestion.md`.

Preserve the Core dependency boundary, all-or-nothing fallback segments, retained stale data, the four fixed sources, the current configuration defaults, and the unauthenticated API contracts. The ignored `.env` must be preserved and never printed.

## Progress

- [x] 2026-07-23T22:20:19Z Confirmed a clean `main` at `9e67ff4`, reviewed the agent guide, routed documentation, current source, documentation skill, and ExecPlan rules.
- [x] 2026-07-23T22:32:45Z Refactored refresh execution and completion-relative scheduling with bounded total-failure backoff.
- [x] 2026-07-23T22:32:45Z Bounded and canceled area-tile work and made the FIRMS timeout cover response bodies.
- [x] 2026-07-23T22:39:57Z Added deterministic fake-time, fake-HTTP, concurrency, cancellation, timeout, service-provider construction across the accepted timeout range, and resilience-policy coverage; 17 focused FIRMS tests pass.
- [x] 2026-07-23T22:39:57Z Synchronized durable documentation and passed restore, Release build, 134 tests, format, documentation, diff, and bounded live-startup validation.
- [x] 2026-07-23T23:17:22Z Reopened the plan after production logs and bounded provider probes proved that 10-degree fallback tiling, rather than the timeout budget, is the remaining failure amplifier.
- [x] 2026-07-23T23:23:15Z Replaced area tiles with one country-envelope request per segment, removed tile-worker state, synchronized logs and durable documentation, and expanded the focused FIRMS suite to 19 passing tests.
- [x] 2026-07-23T23:25:30Z Passed restore, warning-free Release build, 136 tests, format, documentation, and diff validation; a Telegram-disabled UKR+RUS live cycle refreshed all eight envelope segments and published a complete snapshot in 4.34 seconds.

## Surprises and discoveries

- The existing `PeriodicTimer` ticks from construction time, so successful cycles can have only a short gap despite documentation claiming a full post-cycle interval.
- Before the first hardening phase, RUS produced 65 fallback tiles per source and UKR produced 5; unbounded task creation let siblings finish after one tile had already doomed the segment.
- `HttpCompletionOption.ResponseHeadersRead` leaves CSV body consumption outside the standard resilience timeout.
- `Microsoft.Extensions.TimeProvider.Testing` does not publish the repository-aligned `10.0.10` version; the exact available `10.1.0` test-only package restores cleanly.
- The first live start exposed that Polly's circuit-breaker sampling window must be at least twice the attempt timeout. FIRMS now derives a valid window, and a service-provider construction test guards startup validation.
- The bounded UKR live smoke reproduced one real tile timeout while three segments succeeded. It verified primary-tile logging, partial-stale publication, and a 5-minute-plus-jitter post-completion delay; live provider success is not a deterministic acceptance condition.
- Production evidence showed random RUS tiles failing after the configured retry budget while the exact same coordinates later completed in under one second. The healthy MAP_KEY reported 488 of 5000 transactions, excluding quota exhaustion.
- FIRMS documents and accepts area bounds up to the entire world. A bounded northern-band probe returned MODIS and NOAA-20 data in under two seconds, so the repository's 10-degree subdivision is not an upstream requirement.
- The former UKR/RUS fallback expanded eight country/source segments into 280 HTTP calls and approximately 490 weighted transactions. Full country envelopes require eight calls and approximately 46 weighted transactions while local polygon clipping preserves country accuracy.

## Decision log

- 2026-07-23: Preserve the five-minute poll and concurrency-four defaults; fix the implementation rather than silently reduce normal freshness.
- 2026-07-23: Apply exponential backoff only when a cycle has zero successful segments, so one stale segment does not delay healthy sources.
- 2026-07-23: Keep `FIRMS_MAX_CONCURRENCY` as the only public concurrency setting and use an internal maximum of two concurrent segments.
- 2026-07-23: Keep the existing Core/API ownership and avoid an ADR; this changes operational mechanics, not component boundaries or public contracts.
- 2026-07-23: Use one admitted timeout for the complete country capability operation, including an ambiguous-error MAP_KEY status check, so the capability gate cannot hold an upstream slot beyond the configured budget.
- 2026-07-23: Set FIRMS circuit-breaker sampling to at least twice the derived attempt timeout, with the standard 30-second window as its floor, so every accepted request-timeout value creates a valid pipeline.
- 2026-07-23: Replace tile fallback with one complete geometry-envelope request per country/source. FIRMS supports world-sized bounds, response size remains bounded, and exact country membership remains enforced locally.
- 2026-07-23: Remove tile-worker cancellation machinery and retain `FIRMS_MAX_CONCURRENCY` as the cross-segment HTTP admission bound. Do not change timeouts, retries, polling, public configuration, or the separate Overpass integration.

## Concrete implementation steps

1. Extract the refresh cycle behind an internal API seam and make the hosted service run immediately, then delay from completion. Add positive jitter and capped exponential backoff for consecutive cycles with zero successful segments. Use `TimeProvider` and a deterministic schedule seam for tests.
2. Replace area-tile fan-out with one complete geometry-envelope request per country/source, then perform the existing local polygon clip and deduplication before atomic segment publication.
3. Start an exchange timeout only after admission through the request gate and keep it active through bounded content reads. Reduce FIRMS retries to one and derive its attempt timeout as 40 percent of the configured total without a 15-second ceiling. Do not change GIBS or Telegram policies.
4. Add safe component logs for envelope request failures and Debug successes, cycle duration/result counts, next delay, and backoff state. Never log keys, credential-bearing URIs, or response content.
5. Add focused poller, schedule, resilience, single-request envelope, antimeridian-spanning bounds, clipping, deduplication, failure, cancellation, and admission tests. Update existing durable documentation in place.

## Validation and acceptance criteria

Run focused FIRMS/polling tests, documentation validation, `dotnet restore ThermalWatch.slnx`, Release build/test, format verification, and `git diff --check`. Run a bounded live smoke with `.env`, Telegram removed, and `FIRMS_COUNTRIES=UKR,RUS`; it must start cleanly, issue one fallback envelope request per country/source, and publish provider outcomes atomically without exposing credentials. Do not require every live segment to succeed.

Acceptance requires immediate but non-overlapping startup refresh, at least one configured interval between cycle completion and the next start, resettable capped total-failure backoff, exactly one area request per fallback segment, no more than the configured HTTP concurrency, response-body timeout enforcement, local polygon clipping, unchanged snapshot/API semantics, and synchronized documentation.

## Recovery or rollback guidance

All work is source, test, and documentation changes with no migration or persistent state. Apply edits incrementally and keep the solution buildable at checkpoints. Reverse only task-owned hunks with targeted patches; never discard unrelated work, remove `.env`, or use destructive Git commands. A deployed rollback is a container/image rollback and process restart; in-memory snapshot and notification state will reset as already documented.

## Outcomes and retrospective

ThermalWatch now spaces cycles from completion, backs off only total failures, limits segment concurrency, bounds response reads, and exposes safe cycle and area-envelope timing without changing public contracts or defaults. Verified country-feature outages use one complete area-envelope request per country/source, followed by the existing local polygon clip and deduplication. This removes the 10-degree request fan-out and its tile-worker cancellation state while retaining atomic segment failure and stale-data recovery.

Restore, the warning-free Release build, all 136 tests, format verification, seven documentation checks, and diff checks pass. A Telegram-disabled UKR+RUS live smoke issued eight envelope requests, refreshed all eight segments successfully, published 8575 detections without partial staleness, and completed in 4.34 seconds; the four RUS requests each finished in 0.75 to 1.33 seconds. Live success is not a permanent provider guarantee, but the observed cycle verifies the low-fan-out request shape and unchanged publication behavior. The ignored `.env` remained preserved.
