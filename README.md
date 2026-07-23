# ThermalWatch

ThermalWatch is a small .NET 10 service that polls NASA FIRMS near-real-time thermal anomalies for configured countries. It publishes the active observations as an immutable in-memory snapshot, exposes them through a read-only HTTP API and interactive map, and can optionally send filtered anomaly clusters to one Telegram channel.

> [!CAUTION]
> A thermal anomaly is not proof of a wildfire or an ongoing event. Detections can represent industrial heat, gas flares, agricultural burning, explosions, or other hot surfaces. FIRMS is near-real-time satellite reporting, not continuous monitoring. Do not use ThermalWatch as the sole basis for emergency or safety decisions.

## Capabilities

- Polls the MODIS, Suomi-NPP VIIRS, NOAA-20 VIIRS, and NOAA-21 VIIRS FIRMS feeds for each configured country.
- Isolates failures by country and source, retains the last complete segment as stale data, and reports source diagnostics with every snapshot.
- Serves all valid active observations and backend-composed NASA map imagery through an unauthenticated, CORS-enabled API and a framework-free browser viewer.
- Explains the shared notification policy for a selected anomaly and highlights its complete active-snapshot cluster in the viewer.
- Optionally clusters and filters observations for outbound Telegram notifications with sensor-matched NASA GIBS imagery.

All runtime state is in memory. Restarting clears the current snapshot, imagery caches, pending notifications, and notification deduplication state, then starts a fresh FIRMS poll.

## Quickstart

Install the .NET 10 SDK and obtain a free 32-character NASA FIRMS MAP_KEY from the [FIRMS API page](https://firms.modaps.eosdis.nasa.gov/api/map_key/). Replace the key placeholder, then run from the repository root:

```bash
FIRMS_MAP_KEY='<32-character MAP_KEY>' \
FIRMS_COUNTRIES='UKR,RUS' \
dotnet run --project src/ThermalWatch.Api/ThermalWatch.Api.csproj
```

ThermalWatch settings use exact uppercase environment-variable names; there is no `appsettings` equivalent for them. The service listens on [http://localhost:8080](http://localhost:8080). Telegram remains disabled when its credentials are absent. See [operations](docs/operations.md) for every variable and its validation contract.

## Viewer

Open [http://localhost:8080/](http://localhost:8080/) to inspect the current snapshot, source freshness, and every mappable anomaly. Selecting a marker highlights its complete notification cluster and evaluates the same Core criteria used to prepare outbound notifications. NASA GIBS is the default imagery provider and needs no extra key. Core retrieves and composes its tiles, so the browser receives NASA imagery only from ThermalWatch. Setting `GOOGLE_MAPS_API_KEY` enables Google Satellite; that browser key is returned by `/api/viewer/config` and must be restricted to the Maps JavaScript API and the deployment's HTTP referrers.

The coordinate search accepts common decimal, labeled, degrees/minutes, and degrees/minutes/seconds forms, plus coordinate-bearing Google Maps and other major map links. A successful search marks and centers the exact location and selects the nearest current anomaly for inspection.

The viewer reads the current APIs only. Its Refresh action does not trigger a FIRMS poll, and its map imagery is contextual rather than proof of what caused a detection.

## HTTP endpoints

All current routes are unauthenticated. Cross-origin `GET` requests are allowed.

| Endpoint | Behavior |
| --- | --- |
| `GET /` | Serves the interactive viewer. |
| `GET /api/anomalies` | Returns the current in-memory anomaly snapshot and per-source diagnostics without calling NASA. |
| `GET /api/viewer/config` | Reports optional browser map configuration and exposes the Google browser key when configured. |
| `GET /api/viewer/imagery/gibs/{z}/{x}/{y}.png` | Returns a backend-composed latest NASA GIBS map tile and coverage metadata. |
| `GET /api/viewer/notification-diagnostics/{anomalyId}` | Builds the selected anomaly's active-snapshot cluster and explains every current notification criterion. |
| `GET /api/telegram/send-top?count=5` | Sends selected current clusters to Telegram. This is a side-effecting operator endpoint and must be protected by the deployment's network boundary. |

`/api/anomalies` accepts `country`, `source`, and `satellite` comma-separated filters, plus `dayNight=D|N` and `since`. The `since` value must be an ISO-8601 UTC timestamp and must not be older than the current active-window cutoff. The current parser also accepts future UTC values, which can produce an empty result.

```bash
curl "http://localhost:8080/api/anomalies?country=UKR,RUS&dayNight=D"
```

Partial upstream failures remain HTTP `200` responses; inspect `isPartiallyStale` and the `sources` collection in the response. The complete contracts and failure boundaries are routed from the documentation index rather than duplicated here.

## Documentation

Start with the [documentation index](docs/README.md), which explains what each document contains, when to read it, its authoritative sources, and when it must be updated.

- [Architecture](docs/architecture.md) — system boundaries, data flow, state, dependencies, and HTTP surface.
- [Development](docs/development.md) — prerequisites, exact build/test/format commands, debugging, and validation.
- [Operations](docs/operations.md) — environment variables, deployment, security, observability, failure recovery, and packaging.
- [Notification policy](docs/domain/notification-policy.md) — anomaly meaning, clustering, filters, previews, and manual-send semantics.
- [Component guides](docs/README.md#project-documents) — focused FIRMS ingestion, Telegram notifier, and web viewer documentation.
- [Agent guide](AGENTS.md) — repository-wide workflow and documentation-maintenance rules for Codex sessions.
