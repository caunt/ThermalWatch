# Web viewer

> **Purpose:** Explain the Viewer project boundary, framework-free interface, notification diagnostics, same-origin NASA imagery, provider adapters, and browser-specific failure behavior.
> **Scope:** Root-mounted static assets, viewer routes and state, Core notification diagnostics and GIBS map tiles, Google and Yandex Maps, markers, responsive layout, and validation.
> **Sources of truth:** [Viewer project](../../src/ThermalWatch.Viewer/ThermalWatch.Viewer.csproj), [viewer endpoints](../../src/ThermalWatch.Viewer/ViewerEndpoints.cs), [controller](../../src/ThermalWatch.Viewer/wwwroot/app.js), [map support](../../src/ThermalWatch.Viewer/wwwroot/map-support.js), [Core tile client](../../src/ThermalWatch.Core/GibsMapTileClient.cs), and [composition root](../../src/ThermalWatch.Api/Program.cs).
> **Update when:** Viewer inputs, project/static-asset boundaries, imagery composition, provider contracts, browser dependencies, marker behavior, diagnostics, layout, or validation changes.

## Project, hosting, and inputs

`ThermalWatch.Viewer` is a Razor/static-web-assets library, not a service. It depends on Core and is referenced by the sole `ThermalWatch.Api` executable. Its assets publish into that host's root `wwwroot`, so the same process, image, listener, and port `8080` serve `/`, the public APIs, polling, and Telegram. Local static responses require revalidation so obsolete controllers do not survive deployment.

The UI remains plain HTML, CSS, and JavaScript with no package manifest, bundler, transpiler, generated client, or frontend framework. It loads pinned Leaflet CSS/JavaScript from unpkg. On initial load and Refresh, the controller reads these same-origin JSON resources concurrently:

- `/api/viewer/config` for optional Google Maps availability and its browser key.
- `/api/anomalies` for the complete current snapshot and source diagnostics.

Selecting an anomaly additionally requests `/api/viewer/notification-diagnostics/{anomalyId}`. The response identifies the active-snapshot cluster and exhaustively explains the current Core notification criteria. The NASA adapter requests visible same-origin PNGs from `/api/viewer/imagery/gibs/{z}/{x}/{y}.png`. No browser asset contains a FIRMS or GIBS request host. Refresh rereads APIs and never starts a FIRMS poll.

## Provider contract and shared behavior

The controller owns configuration, snapshot, validated points, malformed-coordinate count, selected anomaly, notification diagnostic, provider, notices, cancellation, and asynchronous version guards. Each provider implements `mount`, `renderAnomalies`, `setSelection`, `fitToAnomalies`, `resize`, and `destroy`; providers neither fetch anomaly records nor render the inspector. Window resize events are animation-frame throttled before the active adapter refreshes its map dimensions, so crossing the responsive breakpoint does not leave stale tiles or map bounds.

NASA and Google receive the same validated points, point keys, marker colors, selected/cluster state, tooltips, click callback, and fit rules. The selected anomaly is gold, other members of its current notification cluster are cyan, and unrelated observations remain red. Empty data uses a world view, one point uses bounded zoom 8, and multiple points use padded bounds capped at zoom 10. Selection and cluster highlighting survive provider switches. A selection survives a successful refresh while the observation exists and triggers a fresh diagnostic; it clears when the observation disappears and does not toggle off on repeated marker or map-background clicks.

The browser defensively accepts only finite in-range coordinates. Malformed observations are omitted with a visible count rather than failing the snapshot. The inspector renders notification eligibility, cluster membership, every criterion's observed and required values, every anomaly property, snapshot readiness/staleness metadata, matching country/source status, and raw JSON. Dynamic values use text nodes. A valid HTTP(S) Google URL is paired with a Yandex Maps action derived from the validated coordinates; both are explicit new-tab navigations rather than data inputs.

## Notification diagnostics through Core

The Viewer endpoint passes the current snapshot and selected anomaly ID to Core's candidate engine. Core uses the configured notification radius/time window, deterministic representative selection, metadata thresholds, land-cover policy, preview sizing, and exact-date preview checks. It returns `404` if the selected anomaly is no longer active.

The diagnostic evaluates all criteria even when one already fails and never reads or mutates automatic seen, pending, or delivered state. Enabled land-cover and preview checks may make cached server-side GIBS requests, so selection has its own loading state and request cancellation. A stale response cannot replace a newer selection. Failure retains the selected anomaly, removes cluster highlighting, and shows a diagnostic error without disrupting the map or raw observation inspector.

## NASA GIBS through Core

The Core map-tile client is intentionally separate from the exact-date notification-preview path. Viewer tiles represent each product's latest GIBS default corrected-reflectance date, independently of FIRMS acquisition dates. Core requests 256×256 Web Mercator JPEGs in Terra, Aqua, NOAA-21, NOAA-20, and Suomi-NPP order. Valid Terra pixels remain authoritative; later products fill only transparent or near-black pixels whose RGB channels are all at most 12.

Each upstream response is size, media-type, and dimension checked before decode. A failed request or malformed product is isolated and composition continues. Fully resolved tiles stop early. Remaining holes are encoded transparent, never replaced with a historical basemap, and classified as partial or unavailable. The API returns that classification in `X-ThermalWatch-Imagery-Coverage`; the browser shows one deduplicated warning for any degraded tile. Complete PNGs use five-minute server and HTTP caching; degraded tiles are immediately retryable. Caller cancellation propagates when Leaflet unloads a tile.

The caption avoids claiming one exact date or satellite for the complete mosaic. This contextual map differs from representative-sensor, day/night-matched exact-date notification previews.

## Google, Yandex, and the external-browser boundary

Google Maps JavaScript remains an explicit exception to the same-origin data rule. It loads only when selected and only when `GOOGLE_MAPS_API_KEY` is available from viewer configuration. The option stays visible but disabled otherwise. Loading has a 15-second timeout; download/callback failures clean up for retry, and authentication failures are reported without breaking NASA or server processing. The key remains browser-visible by design and requires Google API and HTTP-referrer restrictions.

Consequently, automatic browser external requests are limited by design to pinned unpkg assets and optional Google Maps. Selected-anomaly actions can navigate the user to Google or Yandex Maps with the observation coordinates. NASA/FIRMS data and imagery always cross the ThermalWatch HTTP boundary first.

## Interface and failure states

The refined dark interface is map-first: header controls, compact snapshot-status cards, dominant map, and a complete anomaly/notification inspector. Desktop uses a bounded map/inspector workspace. At narrow widths, controls and status cards reflow, the map receives a bounded viewport, and the inspector stacks below it without horizontal overflow. Focus indicators, touch targets, live regions, semantic details, and reduced-motion preferences remain usable.

- Initial API/provider work uses a blocking map overlay and disabled controls.
- Configuration failure degrades to NASA-only operation; a missing key explains the disabled Google option.
- Initial anomaly failure shows an unavailable state; refresh failure retains the last usable snapshot and markers.
- Empty results retain the world map and an explanatory inspector state.
- Diagnostic loading is local to the inspector; a diagnostic failure preserves the selected anomaly and provider recovery controls.
- Provider/script/authentication failure leaves provider selection available for recovery.
- Partial FIRMS staleness, malformed coordinates, and unresolved GIBS coverage are warnings, not fatal map failures.

## Validation

Run the dependency-free browser checks after every JavaScript change:

```bash
node --check src/ThermalWatch.Viewer/wwwroot/map-support.js
node --check src/ThermalWatch.Viewer/wwwroot/app.js
node --test tests/viewer-map-support.test.js
```

The Node suite covers provider-neutral cluster marker styles/member mapping, same-origin tile URLs, Yandex coordinate-link construction, coverage propagation, warning deduplication, failed loads, cancellation/object-URL cleanup, absence of direct NASA request hosts, and Google success/failure/retry/authentication behavior. The .NET suite owns diagnostic policy/endpoint behavior plus GIBS product order, response validation, pixel composition, coverage, cache, cancellation, and endpoint headers. Then run the complete build, test, format, documentation, and publish checks from [development](../development.md).

Every viewer task also requires the [live screenshot workflow](../development.md#live-viewer-screenshot-verification). Capture and open NASA and Google desktop/narrow states plus a provider round trip. For notification-diagnostic work, include a selected cluster and inspect eligibility/criterion readability and consistent cluster highlighting. Treat imagery defects, missing markers/controls, unreadable details, unstable desktop bounds, or narrow overflow as failures. For imagery-boundary work, observe that browser NASA tiles use only the same-origin imagery API; do not save archives or logs containing a Google key.
