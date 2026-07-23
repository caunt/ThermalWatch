# FIRMS fallback timeout hardening

## Purpose and observable outcome

ThermalWatch will continue using country-first FIRMS ingestion and atomic area fallback, but a fallback cycle will no longer amplify an intermittent tile timeout into sustained provider pressure. Operators will observe a full configured pause after each completed cycle, total-provider-failure backoff, bounded and cancellable tile work, response-body timeouts, and safe timing summaries. Public HTTP contracts, environment names/defaults, snapshot semantics, and notification behavior remain unchanged.

## Context and repository orientation

`src/ThermalWatch.Core/FirmsClient.cs` owns country/area acquisition and its global upstream gate. `src/ThermalWatch.Api/FirmsPollingService.cs` schedules and publishes country/source segments, while `src/ThermalWatch.Api/Program.cs` composes the FIRMS resilience pipeline. `tests/` has fake HTTP patterns but no direct FIRMS client or poller coverage. Durable behavior is routed through `docs/architecture.md`, `docs/operations.md`, and `docs/components/firms-ingestion.md`.

Preserve the Core dependency boundary, all-or-nothing fallback segments, retained stale data, the four fixed sources, the current configuration defaults, and the unauthenticated API contracts. The ignored `.env` must be preserved and never printed.

## Progress

- [x] 2026-07-23T22:20:19Z Confirmed a clean `main` at `9e67ff4`, reviewed the agent guide, routed documentation, current source, documentation skill, and ExecPlan rules.
- [x] 2026-07-23T22:32:45Z Refactored refresh execution and completion-relative scheduling with bounded total-failure backoff.
- [x] 2026-07-23T22:32:45Z Bounded and canceled area-tile work and made the FIRMS timeout cover response bodies.
- [x] 2026-07-23T22:39:57Z Added deterministic fake-time, fake-HTTP, concurrency, cancellation, timeout, service-provider construction across the accepted timeout range, and resilience-policy coverage; 17 focused FIRMS tests pass.
- [x] 2026-07-23T22:39:57Z Synchronized durable documentation and passed restore, Release build, 134 tests, format, documentation, diff, and bounded live-startup validation.

## Surprises and discoveries

- The existing `PeriodicTimer` ticks from construction time, so successful cycles can have only a short gap despite documentation claiming a full post-cycle interval.
- RUS produces 65 fallback tiles per source and UKR produces 5; the current `Task.WhenAll` starts every tile task and lets siblings finish after one tile has already doomed the segment.
- `HttpCompletionOption.ResponseHeadersRead` leaves CSV body consumption outside the standard resilience timeout.
- `Microsoft.Extensions.TimeProvider.Testing` does not publish the repository-aligned `10.0.10` version; the exact available `10.1.0` test-only package restores cleanly.
- The first live start exposed that Polly's circuit-breaker sampling window must be at least twice the attempt timeout. FIRMS now derives a valid window, and a service-provider construction test guards startup validation.
- The bounded UKR live smoke reproduced one real tile timeout while three segments succeeded. It verified primary-tile logging, partial-stale publication, and a 5-minute-plus-jitter post-completion delay; live provider success is not a deterministic acceptance condition.

## Decision log

- 2026-07-23: Preserve the five-minute poll and concurrency-four defaults; fix the implementation rather than silently reduce normal freshness.
- 2026-07-23: Apply exponential backoff only when a cycle has zero successful segments, so one stale segment does not delay healthy sources.
- 2026-07-23: Keep `FIRMS_MAX_CONCURRENCY` as the only public concurrency setting and use an internal maximum of two concurrent segments.
- 2026-07-23: Keep the existing Core/API ownership and avoid an ADR; this changes operational mechanics, not component boundaries or public contracts.
- 2026-07-23: Use one admitted timeout for the complete country capability operation, including an ambiguous-error MAP_KEY status check, so the capability gate cannot hold an upstream slot beyond the configured budget.
- 2026-07-23: Set FIRMS circuit-breaker sampling to at least twice the derived attempt timeout, with the standard 30-second window as its floor, so every accepted request-timeout value creates a valid pipeline.

## Concrete implementation steps

1. Extract the refresh cycle behind an internal API seam and make the hosted service run immediately, then delay from completion. Add positive jitter and capped exponential backoff for consecutive cycles with zero successful segments. Use `TimeProvider` and a deterministic schedule seam for tests.
2. Replace unbounded `Task.WhenAll` tile creation with lazily enumerated bounded work. Capture the first tile failure, cancel sibling work with a linked token, suppress cancellation noise, and retain atomic segment failure/publication.
3. Start an exchange timeout only after admission through the request gate and keep it active through bounded content reads. Reduce FIRMS retries to one and derive its attempt timeout as 40 percent of the configured total without a 15-second ceiling. Do not change GIBS or Telegram policies.
4. Add safe component logs for primary tile failures, Debug tile durations, cycle duration/result counts, next delay, and backoff state. Never log keys, credential-bearing URIs, or response content.
5. Add focused poller, schedule, resilience calculation, and FIRMS fake-handler tests. Update existing durable documentation in place and correct stale cadence/timeout claims.

## Validation and acceptance criteria

Run focused FIRMS/polling tests, documentation validation, `dotnet restore ThermalWatch.slnx`, Release build/test, format verification, and `git diff --check`. A bounded live smoke may run with `.env`, Telegram removed, and `FIRMS_COUNTRIES=UKR`; it must start cleanly, publish the provider outcomes atomically, and log a full post-completion delay without exposing credentials. Do not require every live segment to succeed and do not run a live RUS fan-out as part of local validation.

Acceptance requires immediate but non-overlapping startup refresh, at least one configured interval between cycle completion and the next start, resettable capped total-failure backoff, no more than the configured HTTP concurrency, no new tile starts after a primary tile failure, prompt sibling cancellation, response-body timeout enforcement, unchanged snapshot/API semantics, and synchronized documentation.

## Recovery or rollback guidance

All work is source, test, and documentation changes with no migration or persistent state. Apply edits incrementally and keep the solution buildable at checkpoints. Reverse only task-owned hunks with targeted patches; never discard unrelated work, remove `.env`, or use destructive Git commands. A deployed rollback is a container/image rollback and process restart; in-memory snapshot and notification state will reset as already documented.

## Outcomes and retrospective

ThermalWatch now spaces cycles from completion, backs off only total failures, limits segment concurrency, cancels sibling area tiles after the primary failure, bounds content reads, and exposes safe cycle/tile timing without changing public contracts or defaults. Deterministic coverage expanded to direct FIRMS and hosted-scheduler behavior, including startup validation of the HTTP resilience pipeline.

Restore, Release build, all 134 tests, format verification, documentation validation, and diff checks pass. A Telegram-disabled UKR live smoke entered verified area fallback, published three successful segments plus one stale failure, and scheduled the next cycle after 5 minutes 9 seconds. Intermittent NASA timeouts remain an external limitation; the change contains their cost and preserves the last complete segment rather than claiming the provider will always respond.
