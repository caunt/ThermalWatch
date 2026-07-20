# ThermalWatch

ThermalWatch is a small .NET 10 service that polls NASA FIRMS near-real-time thermal anomalies for configured countries. It publishes the active observations as an immutable in-memory snapshot, exposes them through a read-only HTTP API and interactive map, and can optionally send filtered anomaly clusters to one Telegram channel.

> [!CAUTION]
> A thermal anomaly is not proof of a wildfire or an ongoing event. Detections can represent industrial heat, gas flares, agricultural burning, explosions, or other hot surfaces. FIRMS is near-real-time satellite reporting, not continuous monitoring. Do not use ThermalWatch as the sole basis for emergency or safety decisions.

## Capabilities

- Polls the MODIS, Suomi-NPP VIIRS, NOAA-20 VIIRS, and NOAA-21 VIIRS FIRMS feeds for each configured country.
- Isolates failures by country and source, retains the last complete segment as stale data, and reports source diagnostics with every snapshot.
- Serves all valid active observations through an unauthenticated, CORS-enabled API and a framework-free browser viewer.
- Optionally clusters and filters observations for outbound Telegram notifications with sensor-matched NASA GIBS imagery.

All runtime state is in memory. Restarting clears the current snapshot, imagery caches, pending notifications, and Telegram deduplication state, then starts a fresh FIRMS poll.

## Quickstart

Install the .NET 10 SDK and obtain a free 32-character NASA FIRMS MAP_KEY from the [FIRMS API page](https://firms.modaps.eosdis.nasa.gov/api/map_key/). From the repository root, source the setup script so its prompts can configure the current shell and future sessions, then run the service:

```bash
source ./.env
dotnet run --project src/ThermalWatch.Api/ThermalWatch.Api.csproj
```

The tracked [environment setup script](.env) requires the FIRMS key, defaults `FIRMS_COUNTRIES` to `UKR,RUS` and `TELEGRAM_CHANNEL_ID` to `@cso_ukr`, and allows the Telegram and Google keys to remain empty. It stores the values outside the repository in a user-only file; `.env` itself is a shell script, not a dotenv data file. ThermalWatch settings use exact uppercase environment-variable names; there is no `appsettings` equivalent for them. The service listens on [http://localhost:8080](http://localhost:8080). See [operations](docs/operations.md) for persistence details, every variable, and its validation contract.

## Viewer

Open [http://localhost:8080/](http://localhost:8080/) to inspect the current snapshot, source freshness, and every mappable anomaly. NASA GIBS is the default imagery provider and needs no extra key. Setting `GOOGLE_MAPS_API_KEY` enables Google Satellite; that browser key is returned by `/api/viewer/config` and must be restricted to the Maps JavaScript API and the deployment's HTTP referrers.

The viewer reads the current APIs only. Its Refresh action does not trigger a FIRMS poll, and its map imagery is contextual rather than proof of what caused a detection.

## HTTP endpoints

All current routes are unauthenticated. Cross-origin `GET` requests are allowed.

| Endpoint | Behavior |
| --- | --- |
| `GET /` | Serves the interactive viewer. |
| `GET /api/anomalies` | Returns the current in-memory anomaly snapshot and per-source diagnostics without calling NASA. |
| `GET /api/viewer/config` | Reports optional browser map configuration and exposes the Google browser key when configured. |
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
