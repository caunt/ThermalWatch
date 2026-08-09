# Architecture

> **Purpose:** Explain ThermalWatch's system boundaries, end-to-end data flow, invariants, and failure isolation.
> **Scope:** Server projects, browser viewer, in-memory state, HTTP boundaries, and external integrations.
> **Sources of truth:** [Composition root](../src/ThermalWatch.Api/Program.cs), [solution](../ThermalWatch.slnx), [snapshot store](../src/ThermalWatch.Core/AnomalySnapshotStore.cs), [history store](../src/ThermalWatch.Core/FirmsHistoryStore.cs), [candidate engine](../src/ThermalWatch.Core/NotificationCandidateEngine.cs), and [viewer endpoints](../src/ThermalWatch.Viewer/ViewerEndpoints.cs).
> **Update when:** A component boundary, dependency direction, endpoint, state model, integration, or cross-component invariant changes.

## Boundaries and dependencies

| Boundary | Responsibility | Depends on |
| --- | --- | --- |
| `ThermalWatch.Core` | FIRMS ingestion, GIBS and Overpass access, immutable current snapshots and daily history, geography, country boundaries, anomaly identity, mapped location context, and the complete anomaly-to-notification-candidate policy and lifecycle. | External data libraries and abstractions only. |
| `ThermalWatch.Telegram` | Telegram transport validation, automatic-forward correlation, message construction, and delivery of prepared Core candidates. | Core. |
| `ThermalWatch.Viewer` | Viewer configuration and routes, eligible-cluster summaries, notification diagnostics, same-origin imagery delivery, root-mounted static assets, and provider-neutral browser presentation. | Core. |
| `ThermalWatch.Api` | Sole executable, process startup, environment configuration, resilient HTTP clients, current polling and historical backfill, public anomaly/history/Telegram routes, and static-file hosting. | Viewer, Core, and Telegram. |
| Browser viewer | Reads same-origin configuration, anomaly, eligible-cluster, notification-diagnostic, and NASA imagery APIs; renders provider-neutral markers through Leaflet or optional Google Maps; offers representative search plus selected-coordinate navigation to Google and Yandex Maps. | ThermalWatch API plus the approved unpkg and Google browser services; external map sites only after user navigation. |
| `ThermalWatch.Tests` | .NET, JavaScript, and documentation validation. | API and its transitive project references. |

Preserve the dependency directions `Api -> Viewer -> Core`, `Api -> Telegram -> Core`, and `Api -> Core`. `ThermalWatch.Api` remains the only executable and listener; Viewer is a library included in the same publish output and container. Core must not acquire host, browser, or Telegram concerns.

## Runtime data flow

```text
NASA FIRMS -> FirmsClient -> FirmsPollingService -> AnomalySnapshotStore -> anomaly API ----------------> browser viewer
                                      |                  |
                                      |                  +-> Core candidate engine -> Telegram formatter/sender <-> Telegram API
                                      |                  +-> Core eligible/diagnostic evaluators ----------------> browser viewer
                                      +-> FirmsHistoryStore -> history API
                                                |
                                                +-> historical-FRP criterion -> Core candidate engine/evaluators
NASA GIBS -> Core map-tile client -> viewer imagery API -----------------------> browser viewer
OpenStreetMap Overpass -> Core mapped-context client -> candidate/diagnostic ---> Telegram or browser viewer
Google Maps ------------------------------------------------------------------> browser viewer
```

1. Startup parses application-specific environment variables and loads embedded Natural Earth 5.1.2 Ukraine point-of-view boundaries for every configured country. Invalid application configuration or unusable requested boundaries are fatal.
2. The poller refreshes immediately, publishing the active result before attempting history backfill, then waits a jittered configured interval after each completed non-overlapping cycle. Consecutive active cycles with zero successful segments back off; each country/source combination remains an independent segment, and backfill failures do not affect active-cycle backoff.
3. Active results update the matching UTC-date history slices before the immutable active-window snapshot is published. The poller then fills incomplete slices for the preceding 30 completed dates in six five-day requests per country/source. Successful current or historical segments replace prior data; failures retain complete data where available and mark the affected slices stale.
4. The snapshot store offers a single-consumer update stream. The Telegram hosted service also owns the configured bot's message-update stream so it can correlate channel posts with their automatic forwards in the linked discussion. It passes each snapshot update to the Core candidate engine and supplies only the message-delivery callback. Core clusters and evaluates all active observations from each consumed snapshot. The enabled historical-FRP criterion fails closed until every completed-date history slice is ready, without consuming first-ready startup-suppression state. When startup delivery is disabled, Core then records only first-snapshot incidents that pass the complete content policy; initially ineligible incidents remain retryable. Later processing suppresses continuations of those startup incidents and successfully delivered episodes, and looks up mapped location context only after an automatic candidate is ready to deliver or a manual candidate has been ranked and selected.
5. Anomaly and history API requests read immutable in-memory state only and never trigger NASA requests. Viewer imagery API requests may retrieve and compose GIBS tiles in Core; complete results use the bounded in-memory cache.
6. The framework-free viewer consumes same-origin configuration, anomaly, eligible-cluster, notification-diagnostic, and NASA imagery contracts; it does not consume or visualize `/api/history`. Its list query asks Core for notification-priority-ordered clusters passing all enabled content criteria, including the in-memory historical baseline; selecting a list row searches the representative coordinates. Selecting an anomaly asks Core to exhaustively explain the same policy rules and look up nearby mapped context around that observation. Both evaluations are read-only and do not inspect or mutate automatic startup-incident or delivered-episode state.

## HTTP surface

The route definitions and status mappings in [Program.cs](../src/ThermalWatch.Api/Program.cs) and [FirmsHistoryEndpoints.cs](../src/ThermalWatch.Api/FirmsHistoryEndpoints.cs) are authoritative. Public current and historical properties come from [AnomalySnapshot.cs](../src/ThermalWatch.Core/AnomalySnapshot.cs), [FirmsHistory.cs](../src/ThermalWatch.Core/FirmsHistory.cs), [FirmsHistoryDay.cs](../src/ThermalWatch.Core/FirmsHistoryDay.cs), and [Anomaly.cs](../src/ThermalWatch.Core/Anomaly.cs); query parsing comes from [AnomalyQuery.cs](../src/ThermalWatch.Api/AnomalyQuery.cs) and [FirmsHistoryQuery.cs](../src/ThermalWatch.Api/FirmsHistoryQuery.cs). There is no generated OpenAPI artifact.

- `GET /` serves the interactive viewer.
- `GET /api/viewer/config` exposes optional browser map configuration, including the browser-visible Google key when configured.
- `GET /api/viewer/imagery/gibs/{z}/{x}/{y}.png` validates Web Mercator coordinates and returns a composed PNG plus coverage and cache headers.
- `GET /api/viewer/eligible-notification-clusters` captures the current snapshot and returns total-FRP-priority-ordered cluster summaries with representative navigation fields and member IDs for clusters passing every enabled content criterion. Core evaluates at most two clusters concurrently; enabled land-cover and required exact-preview checks can cause bounded, cached GIBS requests. The historical check reads only the in-memory history. The query neither looks up nearby features nor applies delivery lifecycle suppression.
- `GET /api/viewer/notification-diagnostics/{anomalyId}` returns the selected anomaly's active-snapshot cluster, total FRP, all current criterion outcomes including historical location FRP, and up to 25 nearby named OSM features, or `404` when the anomaly is no longer present. Enabled land-cover and exact-preview criteria can cause bounded, cached GIBS requests; every valid selection can cause a bounded, cached Overpass request.
- `GET /api/anomalies` returns the current snapshot with optional local filters. Its contract names the anomaly collection/count and country/source segment statuses explicitly; partial upstream failures remain successful responses with segment-level stale diagnostics.
- `GET /api/history` returns the current UTC date plus the preceding 30 completed dates by default. Optional inclusive `from` and `to` dates select at most the retained 31 dates. Each day contains raw observations, shared raw-cluster summaries, and country/source completeness diagnostics; partial history remains a successful response with readiness and staleness flags.
- `GET /api/telegram/send-top` is an unauthenticated, side-effecting manual Telegram operation. See [operations](operations.md) before exposing it beyond a trusted network boundary.

All API routes use camel-case JSON. The host currently permits cross-origin `GET` requests and binds plain HTTP on port `8080`.

## State and invariants

- Anomaly segments, current snapshot, 31-date FIRMS history, GIBS preview/land-cover/viewer-tile cache entries, Overpass mapped-context cache entries, bounded Telegram automatic-forward correlations, startup-incident history, and delivered-episode history exist only in process memory. Unsent notification candidates are not retained. Restart is the only persistence boundary.
- The anomaly API exposes every valid active FIRMS observation. Notification filters do not delete or annotate API anomalies.
- MODIS and the three VIIRS feeds remain distinct observations because their sensors and acquisition characteristics differ.
- Anomaly and cluster IDs are deterministic hashes of stable observation inputs; country code participates in anomaly identity, so changing a fallback coordinate's worldview assignment also changes its anomaly and derived cluster IDs. See [AnomalyId.cs](../src/ThermalWatch.Core/AnomalyId.cs).
- Snapshot anomalies are bounded from `now - active window` through `now`, deduplicated by anomaly ID, and sorted deterministically.
- Active-snapshot `IsReady` means at least one configured segment has succeeded. Once ready, any stale segment makes that snapshot partially stale.
- History `IsReady` means every configured country/source slice is complete and fresh for all 30 preceding UTC dates. Today's live bucket is retained and exposed but excluded from baseline readiness. UTC rollover drops the expired oldest date, adds a new live date, and reevaluates readiness.

## External and failure boundaries

- NASA FIRMS supplies latest and dated country/area CSV data plus MAP_KEY status. Successful country responses retain NASA's requested-country membership. Failures are isolated per segment; only a verified country-feature outage enables area fallback, whose local clipping uses the embedded Ukraine worldview. Incomplete history keeps the enabled historical-FRP criterion fail-closed and is retried after later active cycles without stopping current snapshot publication or its HTTP API.
- NASA GIBS supplies exact-date notification imagery, land-cover tiles, and backend-retrieved viewer map tiles. Missing required notification imagery rejects the cluster for the current snapshot and is reevaluated after later publications; GIBS failure leaves missing viewer pixels transparent and does not stop FIRMS ingestion or the anomaly API.
- OpenStreetMap's public Overpass endpoint supplies named nodes, ways, and relations within 2 km plus exact containing city, town, or village areas on demand. Requests are serialized and cached; failure logs a Warning and produces no mapped context without affecting diagnostics, Telegram delivery, FIRMS polling, or the anomaly API.
- Telegram is outbound for notifications and inbound only through dedicated Bot API message polling used to identify automatic forwards in the linked discussion. An existing webhook, a competing update consumer, insufficient discussion visibility, missing credentials, validation failure, or notifier disablement does not stop FIRMS polling or the HTTP service.
- Natural Earth's versioned Ukraine point-of-view boundary data is embedded in Core, so fallback does not depend on a runtime boundary service. This named political viewpoint is the area's country-attribution source only in `areaFallback`; an eventual successful country probe restores NASA-defined membership.
- Browser-only external dependencies are pinned Leaflet assets from unpkg and optional Google Maps JavaScript. Selected-anomaly links can navigate to Google or Yandex Maps, and nearby-result links can navigate to Google Maps at the feature coordinates; none is a server-side data dependency. NASA/FIRMS/Overpass data is never requested directly by viewer code. Browser or viewer-tile failure affects the viewer, not FIRMS ingestion.
- Serilog writes structured events to the console. The repository defines no database, durable queue, health endpoint, metrics, tracing, or production deployment target.

Read the focused [FIRMS](components/firms-ingestion.md), [Telegram](components/telegram-notifier.md), or [viewer](components/web-viewer.md) document before changing those failure semantics.
