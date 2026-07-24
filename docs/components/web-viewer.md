# Web viewer

> **Purpose:** Explain the Viewer project boundary, framework-free interface, eligible notification clusters, notification diagnostics, nearby mapped context, same-origin NASA imagery, provider adapters, and browser-specific failure behavior.
> **Scope:** Root-mounted static assets, viewer routes and state, Core eligible-cluster evaluation and notification diagnostics, nearby features and GIBS map tiles, Google, OpenStreetMap, and Yandex links, markers, responsive layout, and validation.
> **Sources of truth:** [Viewer project](../../src/ThermalWatch.Viewer/ThermalWatch.Viewer.csproj), [viewer endpoints](../../src/ThermalWatch.Viewer/ViewerEndpoints.cs), [controller](../../src/ThermalWatch.Viewer/wwwroot/app.js), [map support](../../src/ThermalWatch.Viewer/wwwroot/map-support.js), [Core candidate engine](../../src/ThermalWatch.Core/NotificationCandidateEngine.cs), [Core tile client](../../src/ThermalWatch.Core/GibsMapTileClient.cs), [Core nearby client](../../src/ThermalWatch.Core/NearbyFeatureClient.cs), and [composition root](../../src/ThermalWatch.Api/Program.cs).
> **Update when:** Viewer inputs, project/static-asset boundaries, imagery composition, provider contracts, browser dependencies, marker behavior, diagnostics, layout, or validation changes.

## Project, hosting, and inputs

`ThermalWatch.Viewer` is a Razor/static-web-assets library, not a service. It depends on Core and is referenced by the sole `ThermalWatch.Api` executable. Its assets publish into that host's root `wwwroot`, so the same process, image, listener, and port `8080` serve `/`, the public APIs, polling, and Telegram. Local static responses require revalidation so obsolete controllers do not survive deployment.

The UI remains plain HTML, CSS, and JavaScript with no package manifest, bundler, transpiler, generated client, or frontend framework. It loads pinned Leaflet CSS/JavaScript from unpkg. On initial load and Refresh, the controller reads these same-origin JSON resources concurrently:

- `/api/viewer/config` for optional Google Maps availability and its browser key.
- `/api/anomalies` for the complete current snapshot and source diagnostics.
- `/api/viewer/eligible-notification-clusters` for notification-priority-ordered summaries of every active cluster passing all enabled content criteria.

The eligible-cluster request has independent cancellation, loading, empty, and error states so its potentially slower NASA-backed evaluation does not block the anomaly map. Selecting an anomaly additionally requests `/api/viewer/notification-diagnostics/{anomalyId}`. The response identifies the active-snapshot cluster, exhaustively explains the current Core notification criteria, and includes up to five named OpenStreetMap features within 2 km of that selected observation. The NASA adapter requests visible same-origin PNGs from `/api/viewer/imagery/gibs/{z}/{x}/{y}.png`. No browser asset contains a FIRMS, GIBS, or Overpass request host. Refresh rereads APIs and never starts a FIRMS poll.

Coordinate search is entirely local to the static viewer. It does not call a geocoder or add an HTTP endpoint. Successful searches replace the current browser URL with canonical six-decimal `lat` and `lon` query values while preserving unrelated query values and fragments.

## Provider contract and shared behavior

The controller owns configuration, snapshot, validated points, malformed-coordinate count, eligible-cluster list state, coordinate-search target, selected anomaly, notification diagnostic, provider, notices, cancellation, and asynchronous version guards. Each provider implements `mount`, `renderAnomalies`, `setSelection`, `setSearchLocation`, `focusCoordinate`, `fitToAnomalies`, `resize`, and `destroy`; providers neither fetch anomaly records nor render the right rail. Window resize events are animation-frame throttled before the active adapter refreshes its map dimensions, so crossing the responsive breakpoint does not leave stale tiles or map bounds.

NASA and Google receive the same validated points, point keys, marker colors, selected/cluster state, tooltips, click callback, and fit rules. The selected anomaly is gold, other members of its current notification cluster are cyan, and unrelated observations remain red. Empty data uses a world view, one point uses bounded zoom 8, and multiple points use padded bounds capped at zoom 10. Selection and cluster highlighting survive provider switches. A selection survives a successful refresh while the observation exists and triggers a fresh diagnostic; it clears when the observation disappears and does not toggle off on repeated marker or map-background clicks.

NASA batches its circle markers through Leaflet's canvas renderer. Google represents all anomaly points in one Maps Data layer rather than creating one browser marker object per observation; selection and diagnostic changes restyle only points whose selected or cluster role changed. This provider-specific batching preserves individual point visibility and selection while keeping pan and zoom responsive for large snapshots. The single coordinate-search marker remains independent of the anomaly layer.

The browser defensively accepts only finite in-range coordinates. Malformed observations are omitted with a visible count rather than failing the snapshot. The inspector renders notification eligibility, cluster membership, every criterion's observed and required values, every anomaly property, snapshot readiness/staleness metadata, matching country/source status, and raw JSON. When nearby features exist, it renders their names and distances nearest first under “Possible nearby sources” and links each name to Google Maps at the feature's validated coordinates. Dynamic values use text nodes. A valid HTTP(S) Google URL is paired with a Yandex Maps action whose target is derived from the validated coordinates and defaults to satellite view; all map actions are explicit new-tab navigations rather than data inputs.

Coordinate search accepts latitude/longitude in decimal degrees, labeled or cardinal order, degrees and decimal minutes, degrees/minutes/seconds, and colon-delimited angles. It also accepts `geo:` URIs, WKT Points, GeoJSON Points, and embedded coordinates from full Google Maps or Google Earth, OpenStreetMap, Bing, and Yandex links. Common punctuation, wrappers, ASCII or Unicode angle marks, and unambiguous decimal commas are normalized. Ambiguous bare pairs are latitude first unless a label, cardinal direction, recognized wire format, provider URL, or an out-of-latitude-range first value establishes longitude first. Invalid ranges, contradictory labels, malformed angles, unsupported links, and inputs with extra ambiguous numbers fail visibly without changing the current map state.

A successful search places the same violet search ring in both providers, centers the map at zoom 7, and uses great-circle distance to select the nearest current anomaly even when it is outside the searched viewport. The feedback reports that distance; no-data searches retain the location ring without selecting an anomaly. Choosing an eligible-cluster row writes its representative coordinates into the same input and executes this complete search flow, including canonical URL replacement. The target survives data refreshes and provider switches, and refresh recalculates the nearest anomaly against the new snapshot.

On initial load, one valid `lat` and one valid `lon` query value restore the canonical search input and follow that same marker, zoom, and nearest-selection flow. Missing, duplicate, nonnumeric, or out-of-range URL coordinates fail visibly but do not prevent the snapshot or default map from loading. Each successful search replaces the current browser-history entry instead of adding a navigation step. Manual pan and zoom, provider choice, and independently selected anomalies remain transient. Full coordinate-bearing links are parsed without navigation or network access. Short Google redirect links, projected grid systems, location codes, place names, and external geocoding are intentionally outside this local parser.

## Notification eligibility and diagnostics through Core

The eligible-cluster endpoint captures one current snapshot, builds its connected components, and applies the same metadata, land-cover, preview sizing, and required exact-date preview behavior as notification preparation. Unavailable land cover remains non-blocking; an unavailable required preview excludes the cluster for that evaluation. Results contain representative navigation/display fields rather than previews or criterion details and use the manual-send priority: available/highest representative FRP, member count, diameter, acquisition time, and cluster ID. The operation performs no nearby lookup and neither reads nor mutates startup-incident or delivered-episode state.

The list request runs automatically on initial load and Refresh. Superseded requests are aborted, stale rows are removed while reevaluating, and list failure remains local to its panel. Enabled land-cover and required preview checks can make bounded, cached server-side GIBS calls, while disabled or optional preview criteria cause no preview request for this list.

The Viewer endpoint passes the current snapshot and selected anomaly ID to Core's candidate engine. Core uses the configured notification radius/time window, deterministic representative selection, metadata thresholds, land-cover policy, preview sizing, and exact-date preview checks. It separately queries nearby context around the selected observation, even when another cluster member is the representative. Nearby results never affect eligibility. Core returns `404` if the selected anomaly is no longer active.

The diagnostic evaluates all criteria even when one already fails and never reads or mutates automatic startup-incident or delivered-episode history. Enabled land-cover and preview checks may make cached server-side GIBS requests, and every valid selection may make a cached server-side Overpass request, so selection has its own loading state and request cancellation. Overpass failure returns an empty nearby array and logs server-side rather than failing the diagnostic. A stale response cannot replace a newer selection. Other diagnostic failure retains the selected anomaly, removes cluster highlighting, and shows a diagnostic error without disrupting the map or raw observation inspector.

## NASA GIBS through Core

The Core map-tile client is intentionally separate from the exact-date notification-preview path. Viewer tiles represent each product's latest GIBS default corrected-reflectance date, independently of FIRMS acquisition dates. Core requests 256×256 Web Mercator JPEGs in Terra, Aqua, NOAA-21, NOAA-20, and Suomi-NPP order. Valid Terra pixels remain authoritative; later products fill only transparent or near-black pixels whose RGB channels are all at most 12.

Each upstream response is size, media-type, and dimension checked before decode. A failed request or malformed product is isolated and composition continues. Fully resolved tiles stop early. Remaining holes are encoded transparent, never replaced with a historical basemap, and classified as partial or unavailable. The API returns that classification in `X-ThermalWatch-Imagery-Coverage`; the browser shows one deduplicated warning for any degraded tile. Complete PNGs use five-minute server and HTTP caching; degraded tiles are immediately retryable. Caller cancellation propagates when Leaflet unloads a tile.

The caption avoids claiming one exact date or satellite for the complete mosaic. This contextual map differs from representative-sensor, day/night-matched exact-date notification previews.

## Google, OpenStreetMap, Yandex, and the external-browser boundary

Google Maps JavaScript remains an explicit exception to the same-origin data rule. It loads only when selected and only when `GOOGLE_MAPS_API_KEY` is available from viewer configuration. The option stays visible but disabled otherwise. Loading has a 15-second timeout; download/callback failures clean up for retry, and authentication failures are reported without breaking NASA or server processing. The key remains browser-visible by design and requires Google API and HTTP-referrer restrictions.

Consequently, automatic browser external requests are limited by design to pinned unpkg assets and optional Google Maps. Selected-anomaly actions can navigate the user to Google or Yandex Maps with the observation coordinates, and nonempty nearby results can navigate to Google Maps with the feature coordinates. The browser never calls Overpass. Pasting a map URL into coordinate search only parses its text and never navigates to or resolves it. NASA/FIRMS data, imagery, and Overpass data always cross the ThermalWatch HTTP boundary first.

## Interface and failure states

The refined dark interface is map-first: header controls, compact snapshot-status cards, dominant map, and a right rail with the eligible-cluster list above the complete anomaly/notification inspector. The list and inspector scroll independently within the bounded desktop workspace. At narrow widths, controls and status cards reflow, the map receives a bounded viewport, and the list and inspector stack below it without horizontal overflow. Focus indicators, touch targets, live regions, semantic buttons/details, and reduced-motion preferences remain usable.

- Initial API/provider work uses a blocking map overlay and disabled controls.
- Configuration failure degrades to NASA-only operation; a missing key explains the disabled Google option.
- Initial anomaly failure shows an unavailable state; refresh failure retains the last usable snapshot and markers.
- Empty results retain the world map and an explanatory inspector state.
- Eligible-cluster loading and failure remain local to the list panel; each Refresh clears stale rows and retries evaluation without delaying the map.
- Diagnostic loading is local to the inspector; a diagnostic failure preserves the selected anomaly and provider recovery controls.
- Empty or unavailable nearby context omits its section without showing an end-user provider error; the server Warning is the operational signal.
- Provider/script/authentication failure leaves provider selection available for recovery.
- Partial FIRMS staleness, malformed coordinates, and unresolved GIBS coverage are warnings, not fatal map failures.

## Validation

Run the dependency-free browser checks after every JavaScript change:

```bash
node --check src/ThermalWatch.Viewer/wwwroot/map-support.js
node --check src/ThermalWatch.Viewer/wwwroot/app.js
node --test tests/viewer-map-support.test.js
```

The Node suite covers coordinate formats, viewer-URL restoration and serialization, eligible representative-coordinate validation, map-link extraction and rejection, nearby-feature validation, exact-coordinate Google and Yandex links, nearest-point selection, provider-neutral marker styles/member mapping, 9,000-point Google batching and lifecycle, differential marker-role changes, same-origin tile URLs, coverage propagation, warning deduplication, failed loads, cancellation/object-URL cleanup, absence of direct NASA request hosts, and Google success/failure/retry/authentication behavior. The .NET suite owns eligible-cluster policy/ranking and endpoint behavior, diagnostic and nearby-feature endpoint behavior, plus GIBS product order, response validation, pixel composition, coverage, cache, cancellation, and endpoint headers. Then run the complete build, test, format, documentation, and publish checks from [development](../development.md).

Every viewer task also requires the [live screenshot workflow](../development.md#live-viewer-screenshot-verification). Capture and open NASA and Google desktop/narrow states plus a provider round trip. For notification eligibility/diagnostic work, inspect list loading/populated/empty/error behavior as applicable, representative search and canonical URL updates, criterion readability, nearby-feature cards and links when present, and consistent cluster highlighting. Treat imagery defects, missing markers/controls, unreadable or unscrollable rail content, unstable desktop bounds, or narrow overflow as failures. For imagery-boundary work, observe that browser NASA tiles use only the same-origin imagery API; do not save archives or logs containing a Google key.
