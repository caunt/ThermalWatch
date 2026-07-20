# Web viewer

> **Purpose:** Explain the framework-free browser viewer, provider boundary, API consumption, and browser-specific failure behavior.
> **Scope:** Static assets at `/`, viewer state, anomaly details, NASA GIBS and Google adapters, and change validation.
> **Sources of truth:** [HTML entry point](../../src/ThermalWatch.Api/wwwroot/index.html), [viewer controller](../../src/ThermalWatch.Api/wwwroot/app.js), [map support](../../src/ThermalWatch.Api/wwwroot/map-support.js), [styles](../../src/ThermalWatch.Api/wwwroot/styles.css), and [host routes](../../src/ThermalWatch.Api/Program.cs).
> **Update when:** Viewer inputs, state, map-provider contract, marker behavior, diagnostics, browser dependencies, layout, or static hosting changes.

## Hosting and inputs

ASP.NET Core default-file and static-file middleware serve the plain HTML, CSS, and JavaScript viewer at `/`. There is no frontend framework, package manifest, bundler, transpiler, or generated client. The map-support script exposes a small browser global before the controller loads and exports the same functions to dependency-free Node tests.

On load and refresh, the controller fetches these same-origin resources concurrently:

- `/api/viewer/config` to decide whether Google Maps is available and obtain its browser key.
- `/api/anomalies` to obtain the complete current snapshot and source diagnostics.

The viewer does not duplicate ingestion or notification selection logic. A Refresh action rereads those APIs and never triggers a FIRMS poll.

## Shared state and provider contract

The controller owns configuration, snapshot, validated point models, malformed-coordinate count, selected anomaly, current provider, notices, and asynchronous version guards. Map providers do not fetch anomaly data or render the details panel.

Each provider adapter implements the same lifecycle used by `activateProvider`:

1. `mount(container, context)` initializes provider resources and callbacks.
2. `renderAnomalies(points)` renders every validated point with the shared marker semantics.
3. `setSelected(key)` updates selected-marker appearance.
4. `fitToAnomalies(points)` shows a world view, a bounded single-point zoom, or padded multi-point bounds.
5. `destroy()` removes provider listeners, markers, and map resources.

To add a provider, register one definition, implement that lifecycle, reuse the controller's point key and selection callback, report provider failures through the supplied context, and preserve the same anomaly set and details behavior. Provider-specific code must not move anomaly interpretation into the adapter.

## Provider behavior

### NASA GIBS

Leaflet 1.9.4 is loaded from unpkg with pinned URLs and Subresource Integrity hashes. Each displayed tile requests the product's latest GIBS default date, independently of FIRMS acquisition dates. The adapter tries MODIS Terra first, then MODIS Aqua, VIIRS NOAA-21, VIIRS NOAA-20, and VIIRS Suomi-NPP until that tile loads.

Fallback is per tile because daily orbital coverage differs by location and product. A successful image is retained even when it contains clouds or limited visible detail. If every current corrected-reflectance product fails to load, the area remains blank and a deduplicated warning is shown; the viewer never substitutes Blue Marble or another historical basemap. Because products can have different current defaults and multiple products can appear at once, the caption does not claim one exact imagery date or satellite for the complete map.

This map imagery is contextual and differs from Telegram's representative-sensor, day/night-matched preview composition.

### Google Satellite

Google Maps JavaScript loads only when selected and only when `GOOGLE_MAPS_API_KEY` is present. The option remains visible but disabled when unavailable. Script loading has a 15-second timeout; download and callback failures are cleaned up so a later provider selection can retry. Google authentication failures are surfaced to the controller. The key is browser-visible by design and must be restricted outside the application.

Both adapters use the same marker colors, selected state, click callback, fit rules, and source point collection.

## Data validation and details

The API owns valid server-side observations, but the browser defensively requires numeric, finite coordinates inside latitude and longitude ranges before mapping. Malformed observations are omitted from providers and counted in a visible warning. They do not cause the entire snapshot to fail.

Selecting a marker renders every anomaly property returned by the API, snapshot readiness/staleness metadata, the matching country/source status, and raw anomaly JSON. Dynamic values are inserted through DOM text nodes; HTTP(S) is required before rendering the supplied Google URL as a link.

Only one anomaly is selected at a time. Its stable point key preserves selection across provider switches and successful refreshes while that observation remains present; selection clears when it disappears. Clicking the selected marker again or the map background does not toggle it off.

## Loading and failure states

- Initial data and provider loading use a map overlay and disabled controls.
- Missing Google configuration leaves NASA available and explains why Google is disabled.
- Viewer-configuration failure degrades to NASA-only operation.
- Initial anomaly API failure shows an unavailable state and retry action.
- A failed refresh preserves the last usable snapshot and markers.
- Empty results show the world map and an empty details state.
- Provider/script/authentication failure leaves the selector available so another provider can be chosen.
- Partial FIRMS staleness and GIBS fallback or exhausted tile gaps are visible warnings rather than fatal viewer errors.

## Validation

Run the static syntax checks and dependency-free viewer unit tests for every JavaScript change:

```bash
node --check src/ThermalWatch.Api/wwwroot/map-support.js
node --check src/ThermalWatch.Api/wwwroot/app.js
node --test tests/viewer-map-support.test.js
```

The Node suite covers current GIBS product order, URL construction, per-tile success/fallback/exhaustion, warning state, and Google loader success/failure/retry/authentication behavior. Then run the .NET tests and publish/smoke checks described in [development](../development.md). Browser-check desktop and narrow layouts, loading, empty, refresh failure, malformed coordinates, marker selection, provider switching, missing Google key, and the changed provider's failure path. There is no automated browser-test harness in the repository.
