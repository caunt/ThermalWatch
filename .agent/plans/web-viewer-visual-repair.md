# Web viewer visual repair and verification

## Purpose and observable outcome

Repair the live map viewer after browser screenshots exposed two regressions that deterministic request tests missed. NASA GIBS must show the latest MODIS Terra corrected-reflectance pixels wherever they exist, fill Terra no-data pixels from current Aqua and VIIRS products, and never expose opaque black orbital gaps or historical imagery. Google Satellite must retain the same usable workspace height as NASA. Web-viewer work must thereafter require desktop and narrow screenshots that are opened and visually inspected. The existing HTTP anomaly/configuration contracts and provider-neutral marker behavior remain unchanged.

The repository-root `.env` also changes from a tracked interactive prompt helper to an ignored, sourceable local credential file. The existing physical file must be preserved after the task, must not enter a diff, and must not enable Telegram delivery during viewer validation.

## Context and repository orientation

The static viewer lives in `src/ThermalWatch.Api/wwwroot/`. `app.js` owns the provider adapters, `map-support.js` contains dependency-free GIBS and Google support logic, `styles.css` owns the application grid, and `Program.cs` serves the assets. The current GIBS loader considers any HTTP-successful JPEG valid, but current corrected-reflectance JPEGs encode uncovered pixels as black. The current CSS relies on implicit placement in five grid rows, so hiding the notice shifts later children and lets the footer occupy the flexible row.

`tests/viewer-map-support.test.js` is the deterministic JavaScript suite. `docs/components/web-viewer.md`, `docs/development.md`, and `AGENTS.md` carry the durable behavior and workflow. The completed `.agent/plans/web-viewer-imagery-fallback.md` records the superseded request-level approach; this plan records the visual repair. This is a routine implementation correction rather than an architectural decision, so no ADR is required.

## Progress

- [x] 2026-07-20T11:01:21Z — Re-inspected the pushed implementation, documentation, tests, worktree, and prior screenshots; confirmed the worktree started clean.
- [x] 2026-07-20T11:01:21Z — Confirmed live GIBS capabilities expose the Terra layer as latest-dated JPEG only and representative Terra/Aqua/VIIRS responses permit cross-origin canvas reads.
- [x] 2026-07-20T11:08:53Z — Implemented GIBS pixel compositing, explicit page rows, and viewer-asset revalidation.
- [x] 2026-07-20T11:08:53Z — Preserved and ignored `.env`, removed the interactive helper from tracking, and synchronized focused tests and durable documentation.
- [x] 2026-07-20T11:59:12Z — Passed focused JavaScript, Release build, .NET/documentation tests, formatting, publish, and live static-response smoke checks.
- [x] 2026-07-20T11:59:12Z — Rendered and visually inspected NASA/Google desktop, narrow, round-trip, and selected-marker screenshots against 7,731 live observations with Telegram disabled.
- [x] 2026-07-20T12:05:03Z — Reconciled the final diff and retrospective with no tracked credentials or unrelated changes; the verified change is ready to commit and push to `main`.

## Surprises and discoveries

- GIBS returns HTTP 200 JPEG tiles with opaque black no-data pixels. Image `error` events therefore cannot identify partial or empty coverage.
- A hidden grid child stops participating in auto-placement; without named areas, the Google workspace moves into a fixed row and the footer expands into `minmax(0, 1fr)`.
- The old runtime Blue Marble strings are absent from current viewer assets. Remaining mentions are explanatory documentation/tests/history; stale browser assets and the misleading generic fallback warning explain the user's observation.
- The repository-root `.env` is currently a tracked 43-line interactive Bash helper, while the requested end state is an ignored file containing direct exports.
- Releasing 7,731 Google markers individually blocked the browser main thread during Google-to-NASA switching. Releasing the removed map and its marker graph together makes the round trip prompt while preserving marker selection state.
- A visible NASA coverage warning consumes 38 desktop pixels, so NASA's workspace is intentionally shorter than Google's by that row. Correct layout stability means both workspaces extend to the same fixed 29-pixel footer boundary; reserving an empty warning row would waste space.

## Decision log

- 2026-07-20 — Compose each 256-pixel tile in a transparent canvas. A source pixel is no-data when transparent or when all RGB channels are at most 12. Fill only transparent destination pixels in Terra, Aqua, NOAA-21, NOAA-20, and Suomi-NPP order. This retains valid Terra data while repairing successful JPEG gaps.
- 2026-07-20 — Normal Aqua/VIIRS supplementation is not a warning. Emit one warning only if pixels remain unresolved after all products, and leave them transparent over the neutral map background.
- 2026-07-20 — Use named CSS grid areas so notice visibility cannot change semantic row assignment.
- 2026-07-20 — Revalidate local static assets on every page load instead of adding a bundler or hand-maintained asset versions.
- 2026-07-20 — Keep `.env` on disk, ignore and untrack it, use quoted `export` assignments, and launch viewer checks with Telegram variables removed from only the server child process.
- 2026-07-20 — On Google provider destruction, clear map-level listeners and release the map/marker object graph instead of synchronously detaching every marker. The map container is replaced immediately, and a later provider creates a new map.

## Concrete implementation steps

1. In `map-support.js`, use official `.jpeg` URLs, add pure no-data/merge helpers, and replace serial image selection with a cancellable canvas composer. Read cross-origin images through a scratch canvas, progressively fill only missing pixels, stop when complete, and return coverage/product metadata.
2. In `app.js`, return canvas tiles from the Leaflet grid layer, cancel work on tile unload, report only unresolved coverage, and update the NASA caption. Preserve the provider lifecycle and all marker behavior.
3. In `styles.css`, name and assign toolbar, notice, snapshot, workspace, and footer areas. In `Program.cs`, serve local static files with `Cache-Control: no-cache, must-revalidate`.
4. Extend the Node suite with exact pixel fixtures for Terra authority, black/transparent no-data, supplementation, exhaustion, early stop, cancellation, URLs, and warning behavior; retain the Google loader cases.
5. Add `/.env` to `.gitignore`, remove `.env` only from Git's index, replace the preserved working file with the five supplied temporary exports without printing them, and set mode `0600`.
6. Update `AGENTS.md`, `docs/development.md`, and `docs/components/web-viewer.md` with the ignored-file policy, Telegram-safe launch, compositor behavior, stable layout, cache behavior, and mandatory screenshot/vision gate. Update the documentation index only if routing changes.

## Validation and acceptance criteria

Run `bash -n .env` and a silent isolated source assertion, both Node syntax checks, `node --test tests/viewer-map-support.test.js`, restore/build/test/format, the focused documentation test, publish-file assertions, `git diff --check`, and a Kestrel smoke check. Assert local viewer responses contain a revalidation cache header and runtime assets contain no historical-basemap warning.

For live acceptance, source `.env` and start the published host for `UKR,RUS` with `TELEGRAM_BOT_TOKEN` and `TELEGRAM_CHANNEL_ID` unset only for that child. Use a clean Chromium profile. Capture NASA and Google at 1440x900 and 390x844 plus NASA after a Google-to-NASA round trip. Open each screenshot with visual inspection and confirm current imagery has no opaque black swath gaps, markers render and select, Google shows satellite imagery without provider errors, both workspaces end at the same content-sized footer with any height difference explained only by a visible notice row, and narrow layouts have no horizontal overflow. Network success alone is insufficient. Keep screenshots in a temporary artifact directory and report their paths without saving key-bearing network archives.

## Recovery or rollback guidance

All tracked changes are text and can be reverted file-by-file without resetting unrelated work. `git rm --cached .env` must never be replaced with a command that deletes the working file. Stop Kestrel and Chromium normally; never call Telegram endpoints. If GIBS canvas reads fail, verify the response CORS header and image `crossOrigin` ordering before changing the product policy. Do not commit or push until deterministic and visual acceptance both pass.

## Outcomes and retrospective

The viewer now composites latest-dated Terra, Aqua, and VIIRS pixels instead of treating an HTTP-successful black JPEG as usable. Live canvas inspection found no opaque near-black no-data pixels, while genuinely unresolved coverage remained transparent and truthfully warned. Google and NASA both reach the same content-sized footer, narrow layouts do not overflow, markers remain selectable across providers, and releasing thousands of Google markers no longer blocks the round trip.

Seventeen focused Node tests, 36 .NET tests including seven documentation checks, a zero-warning Release build, formatting verification, publish assertions, static cache-header smoke checks, and credential-safe live Chromium inspection passed. The ignored owner-only `.env` remains on disk and Telegram was never enabled or called. Durable behavior and the screenshot/vision requirement now live in `AGENTS.md`, `docs/development.md`, and `docs/components/web-viewer.md`; no ADR or operations change was warranted.

Current daily orbital imagery can still contain clouds, visible joins between acquisition swaths, and areas uncovered by every configured satellite. Those are source-data limits rather than historical substitution; the viewer shows neutral background for the remaining gaps.
