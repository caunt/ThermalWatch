# Architecture

> **Purpose:** Explain ThermalWatch's system boundaries, end-to-end data flow, invariants, and failure isolation.
> **Scope:** Server projects, browser viewer, in-memory state, HTTP boundaries, and external integrations.
> **Sources of truth:** [Composition root](../src/ThermalWatch.Api/Program.cs), [solution](../ThermalWatch.slnx), [snapshot store](../src/ThermalWatch.Core/AnomalySnapshotStore.cs), [candidate engine](../src/ThermalWatch.Core/NotificationCandidateEngine.cs), and [viewer endpoints](../src/ThermalWatch.Viewer/ViewerEndpoints.cs).
> **Update when:** A component boundary, dependency direction, endpoint, state model, integration, or cross-component invariant changes.

## Boundaries and dependencies

| Boundary | Responsibility | Depends on |
| --- | --- | --- |
| `ThermalWatch.Core` | FIRMS ingestion, GIBS and Overpass access, immutable models and snapshots, geography, country boundaries, anomaly identity, nearby mapped context, and the complete anomaly-to-notification-candidate policy and lifecycle. | External data libraries and abstractions only. |
| `ThermalWatch.Telegram` | Telegram transport validation, message construction, and delivery of prepared Core candidates. | Core. |
| `ThermalWatch.Viewer` | Viewer configuration and routes, notification diagnostics, same-origin imagery delivery, root-mounted static assets, and provider-neutral browser presentation. | Core. |
| `ThermalWatch.Api` | Sole executable, process startup, environment configuration, resilient HTTP clients, polling, public anomaly/Telegram routes, and static-file hosting. | Viewer, Core, and Telegram. |
| Browser viewer | Reads same-origin configuration, anomaly, notification-diagnostic, and NASA imagery APIs; renders provider-neutral markers through Leaflet or optional Google Maps; offers selected-coordinate navigation to Google and Yandex Maps. | ThermalWatch API plus the approved unpkg and Google browser services; external map sites only after user navigation. |
| `ThermalWatch.Tests` | .NET, JavaScript, and documentation validation. | API and its transitive project references. |

Preserve the dependency directions `Api -> Viewer -> Core`, `Api -> Telegram -> Core`, and `Api -> Core`. `ThermalWatch.Api` remains the only executable and listener; Viewer is a library included in the same publish output and container. Core must not acquire host, browser, or Telegram concerns.

## Runtime data flow

```text
NASA FIRMS -> FirmsClient -> FirmsPollingService -> AnomalySnapshotStore
                                                   |             |
                                                   |             +-> Core candidate engine -> Telegram formatter/sender -> Telegram API
                                                   |             +-> Core diagnostic evaluator ---------------------------> browser viewer
                                                   +-> anomaly API -----------------------------------------------> browser viewer
NASA GIBS -> Core map-tile client -> viewer imagery API -----------------------> browser viewer
OpenStreetMap Overpass -> Core nearby-feature client -> candidate/diagnostic ----> Telegram or browser viewer
Google Maps ------------------------------------------------------------------> browser viewer
```

1. Startup parses application-specific environment variables and loads embedded Natural Earth boundaries for every configured country. Invalid application configuration or unusable requested boundaries are fatal.
2. The poller refreshes immediately, then waits a jittered configured interval after each completed non-overlapping cycle. Consecutive cycles with zero successful segments back off; each country/source combination remains an independent segment.
3. Successful segments replace their prior data. Failed segments retain their last complete data and become stale.
4. The store atomically publishes an immutable, active-window snapshot and offers a single-consumer update stream. The Telegram hosted service passes each update to the Core candidate engine and supplies only the message-delivery callback. Core clusters and evaluates all active observations from each consumed snapshot, suppresses startup-baseline and delivered episodes, and looks up nearby mapped context only after an automatic candidate is ready to deliver or a manual candidate has been ranked and selected.
5. Anomaly API requests read the current snapshot only and never trigger NASA requests. Viewer imagery API requests may retrieve and compose GIBS tiles in Core; complete results use the bounded in-memory cache.
6. The framework-free viewer consumes same-origin configuration, anomaly, notification-diagnostic, and NASA imagery contracts. Selecting an anomaly asks Core to cluster the current snapshot, exhaustively explain the same policy rules, and look up nearby mapped context around that selected observation; this read-only evaluation does not mutate automatic notification state.

## HTTP surface

The route definitions and status mappings in [Program.cs](../src/ThermalWatch.Api/Program.cs) are authoritative. Public snapshot and anomaly properties come from [AnomalySnapshot.cs](../src/ThermalWatch.Core/AnomalySnapshot.cs) and [Anomaly.cs](../src/ThermalWatch.Core/Anomaly.cs); query parsing comes from [AnomalyQuery.cs](../src/ThermalWatch.Api/AnomalyQuery.cs). There is no generated OpenAPI artifact.

- `GET /` serves the interactive viewer.
- `GET /api/viewer/config` exposes optional browser map configuration, including the browser-visible Google key when configured.
- `GET /api/viewer/imagery/gibs/{z}/{x}/{y}.png` validates Web Mercator coordinates and returns a composed PNG plus coverage and cache headers.
- `GET /api/viewer/notification-diagnostics/{anomalyId}` returns the selected anomaly's active-snapshot cluster, current criterion outcomes, and up to five nearby named OSM features, or `404` when the anomaly is no longer present. Enabled land-cover and exact-preview criteria can cause bounded, cached GIBS requests; every valid selection can cause a bounded, cached Overpass request.
- `GET /api/anomalies` returns the current snapshot with optional local filters. Partial upstream failures remain successful responses with source-level stale diagnostics.
- `GET /api/telegram/send-top` is an unauthenticated, side-effecting manual Telegram operation. See [operations](operations.md) before exposing it beyond a trusted network boundary.

All API routes use camel-case JSON. The host currently permits cross-origin `GET` requests and binds plain HTTP on port `8080`.

## State and invariants

- Anomaly segments, current snapshot, GIBS preview/land-cover/viewer-tile cache entries, Overpass lookup cache entries, the notification startup baseline, and delivered-episode history exist only in process memory. Unsent notification candidates are not retained. Restart is the only persistence boundary.
- The anomaly API exposes every valid active FIRMS observation. Notification filters do not delete or annotate API items.
- MODIS and the three VIIRS feeds remain distinct observations because their sensors and acquisition characteristics differ.
- Anomaly and cluster IDs are deterministic hashes of stable observation inputs; see [AnomalyId.cs](../src/ThermalWatch.Core/AnomalyId.cs).
- Snapshot items are bounded from `now - active window` through `now`, deduplicated by anomaly ID, and sorted deterministically.
- `IsReady` means at least one configured segment has succeeded. Once ready, any stale segment makes the snapshot partially stale.

## External and failure boundaries

- NASA FIRMS supplies country/area CSV data and MAP_KEY status. Failures are isolated per segment; only a verified country-feature outage enables area fallback.
- NASA GIBS supplies exact-date notification imagery, land-cover tiles, and backend-retrieved viewer map tiles. Missing required notification imagery rejects the cluster for the current snapshot and is reevaluated after later publications; GIBS failure leaves missing viewer pixels transparent and does not stop FIRMS ingestion or the anomaly API.
- OpenStreetMap's public Overpass endpoint supplies named nodes, ways, and relations within 2 km on demand. Requests are serialized and cached; failure logs a Warning and produces no nearby results without affecting diagnostics, Telegram delivery, FIRMS polling, or the anomaly API.
- Telegram is outbound only. Missing credentials, validation failure, or notifier disablement does not stop polling or HTTP service.
- Natural Earth boundary data is embedded in Core, so fallback does not depend on a runtime boundary service.
- Browser-only external dependencies are pinned Leaflet assets from unpkg and optional Google Maps JavaScript. Selected-anomaly links can navigate to Google or Yandex Maps, and nearby-result links can navigate to Google Maps at the feature coordinates; none is a server-side data dependency. NASA/FIRMS/Overpass data is never requested directly by viewer code. Browser or viewer-tile failure affects the viewer, not FIRMS ingestion.
- Serilog writes structured events to the console. The repository defines no database, durable queue, health endpoint, metrics, tracing, or production deployment target.

Read the focused [FIRMS](components/firms-ingestion.md), [Telegram](components/telegram-notifier.md), or [viewer](components/web-viewer.md) document before changing those failure semantics.
