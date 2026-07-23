# Backend-mediated viewer imagery and solution redesign

## Purpose and observable outcome

Move every browser-originated NASA data and imagery request behind ThermalWatch while preserving the existing FIRMS snapshot, Telegram, Google Maps, marker, refresh, selection, and failure behavior. Add a dedicated `ThermalWatch.Viewer` library that owns the framework-free UI, viewer configuration, and viewer endpoints while `ThermalWatch.Api` remains the only executable, container entry point, HTTP listener, and composition root on port `8080`. Redesign the viewer as a refined dark, map-first operations surface that remains usable at desktop and narrow widths.

The browser may still load pinned Leaflet assets from unpkg and, when configured, Google Maps JavaScript directly. Those are explicit user-approved exceptions. It must never contact FIRMS or GIBS/NASA directly; NASA map tiles must be fetched, validated, composed, cached, and encoded by Core and returned through the same-origin API.

## Context and repository orientation

The current executable and static viewer live in `src/ThermalWatch.Api/`. `Program.cs` owns all routes and HTTP-client registration; `wwwroot/app.js` contains both provider adapters; `wwwroot/map-support.js` performs browser-side GIBS compositing and Google script loading. `src/ThermalWatch.Core/GibsClient.cs` already owns Telegram preview and land-cover access but not the viewer's latest global mosaic. The viewer currently calls `/api/viewer/config` and `/api/anomalies`, loads Leaflet from unpkg, requests GIBS tiles directly, and optionally loads Google Maps JavaScript.

The governing durable documents are `docs/architecture.md`, `docs/components/web-viewer.md`, `docs/development.md`, `docs/operations.md`, and `README.md`. The project-boundary and external-data ownership decision qualifies for ADR `0001`. Repository-root `.env` exists, is ignored, is mode `0600`, and must remain preserved; live viewer validation must remove Telegram credentials from the server child process.

## Progress

- [x] 2026-07-23T13:14:07Z — Read repository guidance, documentation routing, architecture/viewer/operations/development docs, current source/tests, ADR policy, and prior viewer ExecPlans; confirmed a clean worktree and preserved ignored `.env`.
- [x] 2026-07-23T13:52:25Z — Created the Viewer library and verified its root-mounted assets in the sole Api publish output and container.
- [x] 2026-07-23T13:52:25Z — Implemented and tested Core-owned GIBS map-tile retrieval, validation, compositing, caching, cancellation, and API delivery.
- [x] 2026-07-23T13:52:25Z — Moved and redesigned the viewer, replaced direct GIBS access with same-origin tile fetches, and preserved Google/provider behavior.
- [x] 2026-07-23T13:52:25Z — Synchronized solution, CI, ADR, repository guidance, and routed durable documentation.
- [x] 2026-07-23T13:52:25Z — Passed deterministic, publish, final container, live network, screenshot, and visual validation; reconciled outcomes.

## Surprises and discoveries

- The existing Core GIBS code contains purpose-built PNG parsing for Telegram previews and land cover, but viewer source tiles are JPEG. Cross-platform backend composition therefore requires a raster codec.
- ImageSharp 4.0.0 fails its build target without a Six Labors license key/file. The repository has no such credential and must not gain one, so the implementation uses the public-domain, pure-managed StbImageSharp/StbImageWriteSharp ports instead.
- Live provider switching proved browser aborts reached Core, but uncaught request cancellation made ASP.NET/Serilog label disconnected tile requests as HTTP 500. The Viewer endpoint now consumes only request-aborted cancellation after Core has stopped its upstream work.
- The Razor/static-web-assets library publishes its assets at root as intended. Running the published DLL from outside its publish directory still gives ASP.NET an unrelated content root; live validation and containers correctly run with the publish directory as `/app`.
- Referenced-project static assets are discovered automatically by `dotnet run` only in Development unless the host explicitly enables its generated static-web-assets manifest. The Api host now enables that manifest so the documented Production-mode local run serves the Viewer library at root as well as publish/container runs do.
- The repository already contains completed viewer ExecPlans. This plan is distinct and active because it changes the project graph and moves the NASA trust boundary.

## Decision log

- 2026-07-23 — Add `ThermalWatch.Viewer` as a library referenced by the existing executable rather than a second service. This gives viewer code a clear boundary without adding a process, image, listener, or deployment unit.
- 2026-07-23 — Keep `ThermalWatch.Api` as the only composition root and retain direct references to Core and Telegram. Viewer depends only on Core; Core remains independent of host/browser concerns.
- 2026-07-23 — Add a separate Core `GibsMapTileClient` instead of extending Telegram's exact-date `GibsClient`. The two imagery products have different time, coverage, and failure semantics.
- 2026-07-23 — Preserve the current Terra-first pixel policy exactly and expose `complete`, `partial`, or `none` coverage in a response header so the browser can preserve its warning behavior without interpreting imagery.
- 2026-07-23 — Cache only complete composed tiles for five minutes in the existing bounded memory cache. Partial and unavailable tiles remain immediately retryable.
- 2026-07-23 — Retain direct Google Maps and unpkg browser requests as explicit exceptions selected by the user; only NASA/FIRMS data traffic is required to be same-origin.
- 2026-07-23 — Use StbImageSharp 2.30.15 and StbImageWriteSharp 1.16.7 for JPEG decode and PNG encode. They need no native runtime asset or build credential, which preserves Linux/Alpine/Windows/macOS publish portability.
- 2026-07-23 — Catch `OperationCanceledException` only when `HttpContext.RequestAborted` is cancelled and return an empty result. This avoids false server errors after a disconnected browser while retaining cancellation propagation through Core and the GIBS HTTP request.
- 2026-07-23 — Emit successful viewer-tile request summaries at Debug while retaining normal Information/Error levels for other routes. Map navigation creates many tile requests and must not flood default production logs.
- 2026-07-23 — Call `UseStaticWebAssets` in the Api composition root. Viewer remains a referenced library, while local Production-mode runs reliably load the same generated asset manifest used by publish.

## Concrete implementation steps

1. Add a Razor/static-web-assets `src/ThermalWatch.Viewer` project with root-mounted assets. Move Viewer options, viewer route mapping, and `wwwroot` into it. Reference Viewer from Api and keep Api as the sole executable and container target.
2. Add the public-domain Stb image codecs centrally and implement a singleton Core map-tile client. Validate zoom/x/y, fetch bounded exact-size GIBS JPEGs sequentially in Terra/Aqua/NOAA-21/NOAA-20/Suomi-NPP order, preserve earlier valid pixels, fill RGB-at-most-12 holes, encode transparent PNG output, isolate source failures, bound compositions to eight, propagate cancellation, and cache complete output for five minutes.
3. Map `GET /api/viewer/imagery/gibs/{z}/{x}/{y}.png` from Viewer. Return PNG and coverage/cache headers for valid coordinates and camel-case `400` errors for invalid integer ranges. Preserve every existing route and JSON contract.
4. Remove GIBS hosts/products/composition from browser support code. Fetch same-origin PNG tiles with abort/unload and object-URL cleanup, read the coverage header, and retain one deduplicated unresolved-coverage warning. Keep Google loading and provider-neutral markers unchanged.
5. Redesign HTML/CSS/controller presentation as a refined dark map-first operations workspace. Preserve every field, raw JSON, source diagnostics, status, loading/empty/error state, selection lifecycle, fit rule, refresh rule, and provider behavior. Provide a non-overflowing stacked narrow layout and accessible focus/touch/live-region behavior.
6. Move/update JS tests, add Core fake-handler image tests and TestServer endpoint tests, and update solution/CI/publish paths. Create ADR `0001`, update routed documentation and repository guidance, and leave unrelated FIRMS/Telegram domain documentation untouched unless verified drift is found.

## Validation and acceptance criteria

Run the Viewer-path Node syntax checks and unit suite, `dotnet restore`, zero-warning Release build, full Release tests, format verification, focused documentation validation, `git diff --check`, and publish-file assertions. Verify the Api publish output contains root viewer assets and that container workflows still publish only `ThermalWatch.Api`.

Run the published host from the ignored `.env` with Telegram variables removed. Exercise `/`, `/api/anomalies`, `/api/viewer/config`, valid/invalid GIBS tile routes, static cache headers, and one-port behavior. In a clean browser profile, observe that NASA/GIBS traffic is same-origin while the approved unpkg/Google traffic remains external. Capture and open NASA and Google screenshots at 1440x900 and 390x844, plus NASA after switching to Google and back. Inspect loading, selection, missing-Google, stale/partial imagery, and failed-refresh states as applicable. Reject opaque no-data swaths, missing controls/markers, overflow, unreadable details, broken footer/layout, or behavior drift.

## Recovery or rollback guidance

All tracked work is source, project, test, workflow, and documentation text plus normal package metadata. Apply changes in coherent project/Core/viewer/docs checkpoints and revert only affected files if necessary; never reset unrelated work. Static assets must exist in exactly one project before build validation. Stop Kestrel and browser processes normally. Never print `.env`, enable Telegram during viewer checks, call the manual Telegram endpoint, or remove the ignored credential file.

## Outcomes and retrospective

ThermalWatch now has four projects but one runtime: Api remains the only executable and container entry point, Viewer owns routes/assets, Core owns all NASA map retrieval/composition, and Telegram remains behaviorally unchanged. The browser requested 84 observed live NASA tiles only through the same-origin API; no FIRMS/GIBS host appeared in browser traffic. Google and pinned unpkg remained the approved external exceptions.

Fourteen Node tests and 81 .NET tests, including seven documentation checks, pass. Restore, zero-warning Release build, formatting, publish assertions, diff checks, valid/invalid live endpoint smoke checks, cancellation rechecks, and a final SDK-built container all pass. The documented Production-mode `dotnet run` path also serves `/`, `/app.js`, and `/api/viewer/config`. The final image exposes only `8080/tcp`, starts `ThermalWatch.Api.dll`, serves root Viewer assets, and returns a real backend-composed GIBS PNG.

Desktop and narrow NASA/Google screenshots, provider round-trip, selected inspector, loading, empty, initial API failure, missing Google, partial coverage, and retained-data refresh failure were opened and visually inspected. Controls, markers, imagery, status hierarchy, details, selection persistence, footer bounds, and narrow overflow are sound. Screenshots remain transient under `/tmp/thermalwatch-viewer-repair.FjqQRM/backend-mediated/`; `.env` stayed ignored, owner-only, unmodified, and Telegram was disabled throughout live checks.
