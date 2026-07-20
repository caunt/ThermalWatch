# Fresh GIBS imagery and viewer verification

## Purpose and observable outcome

ThermalWatch's NASA browser map currently reveals static Blue Marble when its dated Suomi-NPP tiles are absent. Replace that behavior with NASA's latest daily corrected-reflectance imagery, preferring MODIS Terra and resolving missing tiles through other current satellite products. A user must never see Blue Marble or another historical basemap as an implicit fallback. The optional Google provider must remain usable, surface bounded loader/authentication failures, and be covered by deterministic loader tests plus a credentialed live smoke check.

This work changes only static viewer assets, dependency-free JavaScript tests, PR validation, and routed documentation. It does not change HTTP contracts, environment variables, FIRMS ingestion, Telegram imagery, or provider-neutral marker behavior.

## Context and repository orientation

The framework-free viewer is served from `src/ThermalWatch.Api/wwwroot/`. `app.js` owns controller state and the Leaflet/Google adapters; `index.html` orders static scripts. The current `GibsMapProvider` adds Blue Marble below one dated Suomi-NPP tile layer, with the date derived from the newest anomaly. The current Google loader has no timeout and is embedded inside the controller, making its failure states hard to test.

`docs/components/web-viewer.md` defines durable viewer behavior, `docs/development.md` defines validation, `AGENTS.md` lists recurring commands, and `.github/workflows/pr.yml` is the PR gate. The worktree was clean at task start. NASA's public EPSG:3857 capabilities reported 2026-07-20 as the default date for all five selected products during planning, and a representative Kyiv tile showed why fallback must be per tile: Terra returned an image while the alternatives returned 404. Those live results are transient evidence, not durable documentation or deterministic assertions.

## Progress

- [x] 2026-07-20T09:57:33Z — Read repository guidance, routed viewer/development documentation, ADR policy, and documentation-maintenance skill; confirmed a clean worktree.
- [x] 2026-07-20T10:02:54Z — Implemented the browser/Node map-support module and integrated current GIBS per-tile fallback plus the bounded Google loader.
- [x] 2026-07-20T10:20:43Z — Added 12 passing dependency-free Node regression tests and the Node 22 PR gate.
- [x] 2026-07-20T10:20:43Z — Synchronized durable viewer/development/agent documentation.
- [x] 2026-07-20T10:20:43Z — Passed deterministic validation, live GIBS/fallback/exhaustion browser checks, marker/provider-switch checks, and Google authentication-failure verification.
- [x] 2026-07-20T10:31:11Z — Verified successful Google satellite imagery, both anomaly markers, marker selection, fit behavior, and switching back to GIBS after Maps JavaScript API activation.
- [x] 2026-07-20T10:20:43Z — Reconciled this plan and recorded the delivered outcome and remaining external limitation.

## Surprises and discoveries

- NASA's `default/default` WMTS route is explicitly non-cacheable in the sampled response, so it can represent each product's current default without coupling map imagery to FIRMS observation dates.
- A current date can exist in capabilities while a particular product has no tile for a location. Product selection therefore cannot be global.
- No `FIRMS_MAP_KEY`, `GOOGLE_MAPS_API_KEY`, or installed browser was present at task start. Public GIBS checks remain possible; credentialed Google rendering requires a fresh temporary key and a disposable browser later.
- A fresh key was later supplied and reached Google Maps successfully, but Google returned `ApiNotActivatedMapError`. The loader and visible authentication-failure path behaved correctly; the Google Cloud project must enable Maps JavaScript API before a successful satellite render can be verified.
- After Maps JavaScript API was enabled, the same live path completed without provider errors: Chromium received successful Google imagery responses, rendered both test anomaly markers, selected the MODIS marker, and returned to GIBS.

## Decision log

- 2026-07-20 — Use one custom Leaflet grid layer that resolves each tile serially in this order: MODIS Terra, MODIS Aqua, VIIRS NOAA-21, VIIRS NOAA-20, then VIIRS Suomi-NPP. This preserves Terra priority, avoids eager requests for every fallback, and leaves an honest blank when all current products fail.
- 2026-07-20 — Use GIBS `default/default` URLs and describe the result as latest available corrected reflectance rather than displaying one exact date. Different products can have different current defaults.
- 2026-07-20 — Add a small UMD-style support module with a browser global and CommonJS export. Node's built-in test runner can then exercise fallback and Google loader behavior without npm dependencies, a package manifest, or a frontend framework.
- 2026-07-20 — Keep the existing Google map and marker API behavior, but add a 15-second script-load timeout and retry-safe cleanup. This is routine provider hardening and does not require an ADR.

## Concrete implementation steps

1. Add `src/ThermalWatch.Api/wwwroot/map-support.js`. Export immutable GIBS product metadata, a default WMTS URL builder, a serial image fallback function with injectable image creation, and a stateful Google Maps loader with injected browser/document/timers for tests. The loader must preserve any prior `gm_authFailure` handler, fan authentication failure out to subscribers, resolve preloaded/callback success, reject script errors and timeouts, remove failed script/callback state, and allow retry.
2. Load the support module before `app.js` in `index.html`. Update `app.js` to require the browser global, remove anomaly-derived imagery dates and all Blue Marble/Suomi-NPP-only code, implement the custom Leaflet grid layer, deduplicate fallback/exhaustion warnings, and use the extracted Google loader. Preserve all provider-neutral lifecycle and marker behavior.
3. Add `tests/viewer-map-support.test.js` using `node:test` and strict assertions. Cover exact product order and URLs, primary success, serial fallback success, total exhaustion, warning deduplication inputs, and Google preloaded/callback/error/timeout/retry/authentication paths.
4. Add pinned `actions/setup-node@249970729cb0ef3589644e2896645e5dc5ba9c38` with Node 22 to `.github/workflows/pr.yml`, then run syntax checks and the Node test file before the .NET suite.
5. Rewrite stale NASA behavior and validation statements in `docs/components/web-viewer.md`; update Node prerequisites, commands, and PR behavior in `docs/development.md`; add both viewer syntax checks and the Node test command to `AGENTS.md`. Do not change `docs/README.md`, architecture, operations, or add an ADR because routes, boundaries, configuration, and documentation routing are unchanged.

## Validation and acceptance criteria

Run from the repository root:

```bash
node --check src/ThermalWatch.Api/wwwroot/map-support.js
node --check src/ThermalWatch.Api/wwwroot/app.js
node --test tests/viewer-map-support.test.js
dotnet restore ThermalWatch.slnx
dotnet build ThermalWatch.slnx -c Release --no-restore --nologo
dotnet test ThermalWatch.slnx -c Release --no-build --nologo
dotnet format ThermalWatch.slnx --verify-no-changes --no-restore
dotnet publish src/ThermalWatch.Api/ThermalWatch.Api.csproj -c Release --no-restore --nologo
dotnet test tests/ThermalWatch.Tests.csproj -c Release --nologo --filter FullyQualifiedName~DocumentationValidationTests
git diff --check
```

After publishing, assert that the publish output contains both `wwwroot/index.html` and `wwwroot/map-support.js`. Also search tracked viewer assets and current-behavior documentation to prove Blue Marble is absent. Re-query public GIBS capabilities and representative current/default tiles without encoding transient results in tests. In a disposable local browser, exercise GIBS desktop/narrow layouts, markers, refresh, selection, provider switching, controlled fallback, and exhaustion. With a fresh temporary Google key restricted to localhost and never printed or persisted, verify satellite rendering, markers, bounds, selection, switching, and invalid/auth-rejected behavior. If credentials are unavailable, explicitly report the credentialed check as the only unverified acceptance item rather than claiming success.

## Recovery or rollback guidance

All changes are source-controlled text and are independently reversible. Stop running validation processes normally; no migration, external write, or irreversible operation exists. Failed GIBS and Google probes are read-only. Never discard unrelated worktree changes. Retry deterministic commands after correcting the focused failure. Remove only this task's support script/test references together if rolling back, so `index.html`, `app.js`, tests, CI, and documentation remain synchronized.

## Outcomes and retrospective

The viewer now requests NASA's latest corrected-reflectance defaults per tile, prioritizes MODIS Terra, falls back serially through Aqua and three VIIRS products, and leaves exhausted areas blank without any historical basemap. Google loading is isolated behind a tested 15-second, retry-safe loader. Twelve Node tests, the Release build, all 36 .NET tests, format verification, publish checks, and seven documentation tests passed.

Disposable Chromium runs verified live GIBS imagery, an observed alternate request, controlled fallback, exhausted blank tiles, marker selection, provider switching, invalid-key reporting, narrow layout, successful Google satellite imagery responses, both Google anomaly markers, Google marker selection, and switching back to GIBS. The earlier `ApiNotActivatedMapError` cleared after Maps JavaScript API activation; no acceptance item remains incomplete. No credential was printed by validation commands, persisted, committed, or passed to Telegram; the temporary services and credential shells were stopped and cleared.
