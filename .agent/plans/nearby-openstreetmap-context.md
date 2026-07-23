# Nearby OpenStreetMap context

## Purpose and observable outcome

ThermalWatch will enrich only prepared Telegram notification candidates and selected-anomaly viewer diagnostics with up to five named OpenStreetMap features within two kilometres. Results are ordered by distance and described as possible nearby sources while explicitly warning that mapped proximity does not establish cause. Overpass unavailability is a warning-only failure and never changes the raw anomaly snapshot, eligibility, polling, viewer map, or Telegram delivery.

## Context and repository orientation

`src/ThermalWatch.Core/NotificationCandidateEngine.cs` owns prepared candidates and diagnostics; `src/ThermalWatch.Telegram/TelegramMessageFormatter.cs` renders Core data; `src/ThermalWatch.Viewer/ViewerEndpoints.cs` and `wwwroot/app.js` expose and render diagnostics; `src/ThermalWatch.Api/Program.cs` composes resilient HTTP clients. Preserve the dependency directions and the unannotated `/api/anomalies` contract described in `docs/architecture.md` and `docs/domain/notification-policy.md`.

## Progress

- [x] 2026-07-23T20:43:50Z Confirmed a clean `main` aligned with `origin/main`, preserved the ignored `.env`, and reviewed routed guidance.
- [x] 2026-07-23T20:59:27Z Implemented the bounded Core Overpass client, contracts, caching, and focused tests.
- [x] 2026-07-23T20:59:27Z Enriched automatic/manual candidates and viewer diagnostics at the agreed on-demand points.
- [x] 2026-07-23T20:59:27Z Added conditional Telegram and viewer sections with focused formatter, endpoint, and JavaScript tests.
- [x] 2026-07-23T20:59:27Z Added ADR 0003 and synchronized the routed durable documentation.
- [x] 2026-07-23T21:10:40Z Passed complete automated, publish, live-startup, provider, responsive, and screenshot/vision validation.
- [x] 2026-07-23T21:15:08Z Finalized the verified delivery commit for a non-force push to the aligned `origin/main`.

## Surprises and discoveries

- Historical completed plans remain tracked under `.agent/plans/`; this plan will remain as the task's resolved implementation record unless repository validation or established convention requires removing it.
- The standard .NET resilience handler rejects zero retry attempts during host option validation. The Overpass client therefore uses a single bounded 15-second `HttpClient` request without that handler, preserving the no-immediate-retry provider policy.

## Decision log

- 2026-07-23: Query named OSM nodes, ways, and relations on demand rather than enriching snapshots. This bounds free-provider traffic and preserves `/api/anomalies`.
- 2026-07-23: Automatic/manual Telegram candidates use the cluster representative; viewer diagnostics use the selected observation. Results never influence notification policy.
- 2026-07-23: Serialize upstream requests, cache outcomes, and convert provider/response failures to an empty collection plus one structured Warning. Caller cancellation still propagates.
- 2026-07-23: Use node coordinates and Overpass centers for ways/relations, then Haversine-sort and clamp to five.

## Concrete implementation steps

1. Add an immutable nearby-feature model and singleton Core client. POST a bounded Overpass QL request to `https://overpass-api.de/api/interpreter`, validate JSON/coordinates, sort deterministically, cache by six-decimal coordinates, and guard upstream calls with one semaphore.
2. Add the client to API HTTP composition with explicit identifying headers and bounded no-retry resilience. Extend prepared candidates and diagnostics, enriching only ready-to-deliver automatic candidates, already-ranked manual selections, and selected viewer observations.
3. Extend Telegram formatting and viewer diagnostic validation/rendering with a conditional possible-sources section, distances, viewer-only OSM links, and causal uncertainty text.
4. Add focused fake-HTTP, candidate, endpoint, formatter, logging, and browser helper tests without calling live providers.
5. Add ADR 0003 and update README, architecture, operations, domain policy, Telegram, viewer, and development validation guidance where behavior changed.

## Validation and acceptance criteria

Run JavaScript syntax/tests, focused .NET tests, documentation validation, `dotnet restore`, Release build/test, format verification, static publish assertions, `git diff --check`, and repository status review. Run the published application with `.env` credentials while suppressing Telegram, capture and open NASA and Google selected-anomaly screenshots at 1440×900 and 390×844 plus a provider round trip, and inspect non-empty and empty/unavailable nearby states. No provider failure may escape to the endpoint or sender.

## Recovery or rollback guidance

All changes are source/documentation additions or small contract extensions and are reversible with targeted patches. Never delete or overwrite `.env`, discard unrelated work, force-push, or use destructive Git commands. If `origin/main` advances, fetch and rebase only a clean committed change; stop on conflicts and preserve both sides for review.

## Outcomes and retrospective

Core now supplies at most five cached, distance-ordered named OSM features to ready Telegram candidates and selected Viewer diagnostics without changing raw observations or eligibility. Provider failure is warning-only, Telegram always keeps the bounded context within its caption, and the Viewer conditionally renders safe canonical OSM links plus the causal warning.

Validation passed with 117 .NET tests, 28 Node tests, documentation drift checks, Release build, format verification, static publish output, live Kestrel startup, real FIRMS/Overpass data, NASA and Google providers, and desktop/narrow visual inspection. Screenshots are under `/tmp/thermalwatch-viewer-JuTcB9/`. Live startup exposed and led to correction of the invalid zero-retry standard resilience configuration before delivery.

Known limitations are intentional: OSM completeness depends on volunteer mapping, way/relation distance uses the Overpass center, cache state is process-local, and an uncached lookup can add up to 15 seconds before degrading to no context.
