# Architecture

> **Purpose:** Explain ThermalWatch's system boundaries, end-to-end data flow, invariants, and failure isolation.
> **Scope:** Server projects, browser viewer, in-memory state, HTTP boundaries, and external integrations.
> **Sources of truth:** [Composition root](../src/ThermalWatch.Api/Program.cs), [solution](../ThermalWatch.slnx), [models](../src/ThermalWatch.Core/Models.cs), and [snapshot store](../src/ThermalWatch.Core/AnomalySnapshotStore.cs).
> **Update when:** A component boundary, dependency direction, endpoint, state model, integration, or cross-component invariant changes.

## Boundaries and dependencies

| Boundary | Responsibility | Depends on |
| --- | --- | --- |
| `ThermalWatch.Core` | FIRMS ingestion, GIBS access, immutable models and snapshots, geography, country boundaries, anomaly identity, and generic clustering. | External data libraries and abstractions only. |
| `ThermalWatch.Telegram` | Notification-specific clustering context, filtering, formatting, validation, and delivery. | Core. |
| `ThermalWatch.Api` | Process startup, environment configuration, resilient HTTP clients, polling, HTTP routes, and static-file hosting. | Core and Telegram. |
| Browser viewer | Reads same-origin configuration and anomaly APIs; renders provider-neutral markers through NASA GIBS or Google Maps. | API responses plus browser-side map services. |
| `ThermalWatch.Tests` | Unit and documentation validation. | API and its transitive project references. |

Preserve the dependency direction `Api -> Telegram -> Core` and `Api -> Core`. Core must not acquire host, browser, or Telegram concerns.

## Runtime data flow

```text
NASA FIRMS -> FirmsClient -> FirmsPollingService -> AnomalySnapshotStore
                                                   |             |
                                                   |             +-> Telegram notifier -> NASA GIBS -> Telegram API
                                                   +-> HTTP API -> browser viewer -> NASA GIBS / Google Maps
```

1. Startup parses application-specific environment variables and loads embedded Natural Earth boundaries for every configured country. Invalid application configuration or unusable requested boundaries are fatal.
2. The poller refreshes immediately, then starts non-overlapping cycles after the configured interval. Each country/source combination is an independent segment.
3. Successful segments replace their prior data. Failed segments retain their last complete data and become stale.
4. The store atomically publishes an immutable, active-window snapshot and offers a single-consumer update stream to the Telegram notifier.
5. HTTP requests read the current snapshot only. They never trigger NASA requests.
6. The viewer is static client code and consumes the same API contract. Telegram applies a separate notification policy without changing API observations.

## HTTP surface

The route definitions and status mappings in [Program.cs](../src/ThermalWatch.Api/Program.cs) are authoritative. Public snapshot and anomaly properties come from [Models.cs](../src/ThermalWatch.Core/Models.cs); query parsing comes from [AnomalyQuery.cs](../src/ThermalWatch.Api/AnomalyQuery.cs). There is no generated OpenAPI artifact.

- `GET /` serves the interactive viewer.
- `GET /api/viewer/config` exposes optional browser map configuration, including the browser-visible Google key when configured.
- `GET /api/anomalies` returns the current snapshot with optional local filters. Partial upstream failures remain successful responses with source-level stale diagnostics.
- `GET /api/telegram/send-top` is an unauthenticated, side-effecting manual Telegram operation. See [operations](operations.md) before exposing it beyond a trusted network boundary.

All API routes use camel-case JSON. The host currently permits cross-origin `GET` requests and binds plain HTTP on port `8080`.

## State and invariants

- Anomaly segments, current snapshot, GIBS cache, Telegram seen IDs, delivered-episode history, and pending previews exist only in process memory. Restart is the only persistence boundary.
- The anomaly API exposes every valid active FIRMS observation. Notification filters do not delete or annotate API items.
- MODIS and the three VIIRS feeds remain distinct observations because their sensors and acquisition characteristics differ.
- Anomaly and cluster IDs are deterministic hashes of stable observation inputs; see [AnomalyId.cs](../src/ThermalWatch.Core/AnomalyId.cs).
- Snapshot items are bounded from `now - active window` through `now`, deduplicated by anomaly ID, and sorted deterministically.
- `IsReady` means at least one configured segment has succeeded. Once ready, any stale segment makes the snapshot partially stale.

## External and failure boundaries

- NASA FIRMS supplies country/area CSV data and MAP_KEY status. Failures are isolated per segment; only a verified country-feature outage enables area fallback.
- NASA GIBS supplies Telegram imagery, land-cover tiles, and browser map tiles. GIBS failure does not stop FIRMS ingestion or the anomaly API.
- Telegram is outbound only. Missing credentials, validation failure, or notifier disablement does not stop polling or HTTP service.
- Natural Earth boundary data is embedded in Core, so fallback does not depend on a runtime boundary service.
- Browser-only dependencies are Leaflet from unpkg, NASA GIBS tiles, and optional Google Maps JavaScript. Their failure affects the viewer, not server-side ingestion.
- Serilog writes structured events to the console. The repository defines no database, durable queue, health endpoint, metrics, tracing, or production deployment target.

Read the focused [FIRMS](components/firms-ingestion.md), [Telegram](components/telegram-notifier.md), or [viewer](components/web-viewer.md) document before changing those failure semantics.
