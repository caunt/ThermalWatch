# Operations

> **Purpose:** Define ThermalWatch runtime configuration, deployment, security, observability, failure, and recovery behavior.
> **Scope:** Process startup, environment variables, external services, SDK publishing, CI artifacts, containers, and operational limitations.
> **Sources of truth:** [Application configuration](../src/ThermalWatch.Api/EnvironmentConfiguration.cs), [Telegram options](../src/ThermalWatch.Telegram/TelegramOptions.cs), [composition root](../src/ThermalWatch.Api/Program.cs), and [publish workflows](../.github/workflows/).
> **Update when:** A variable, startup rule, external dependency, security boundary, log, deployment workflow, failure mode, or recovery procedure changes.

## Runtime model

The process binds plain HTTP to `0.0.0.0:8080`, starts one immediate FIRMS refresh, then runs non-overlapping polling cycles. Application options are parsed once at startup and are not reloaded.

All application state is in memory: source segments, the published snapshot, GIBS cache entries, Telegram seen IDs, and pending preview notifications. The service has no database, durable queue, migration, or required persistent volume. Restart clears this state and starts a fresh FIRMS poll.

The application-specific options below use exact uppercase environment names. Framework hosting still uses ASP.NET Core's normal host configuration, but .NET-style nested names such as `Firms__MapKey` do not configure ThermalWatch options.

## Environment variables

Do not place real values in documentation, tracked files, images, plans, or logs. The parsing code linked in the header is authoritative for normalization and validation.

### FIRMS, viewer, and logging

| Variable | Default | Contract |
| --- | --- | --- |
| `FIRMS_MAP_KEY` | required | Exactly 32 ASCII alphanumeric characters. |
| `FIRMS_COUNTRIES` | required | Nonempty comma-separated ISO alpha-3 codes; trimmed, uppercased, deduplicated, and required to have usable embedded boundary geometry. |
| `FIRMS_POLL_INTERVAL` | `00:05:00` | Duration from 10 seconds through 1 day. |
| `FIRMS_ACTIVE_WINDOW` | `24:00:00` | Duration from 1 minute through 24 hours. |
| `FIRMS_REQUEST_TIMEOUT` | `00:00:45` | Duration from 5 seconds through 5 minutes. |
| `FIRMS_MAX_CONCURRENCY` | `4` | Integer from 1 through 32. |
| `GOOGLE_MAPS_API_KEY` | unset | Optional trimmed browser key. When present, it is returned by `/api/viewer/config`; protect it with Google API and HTTP-referrer restrictions. |
| `LOGGING_MINIMUM_LEVEL` | `Information` | `Verbose`, `Debug`, `Information`, `Warning`, `Error`, or `Fatal`, case-insensitive. |

### Telegram lifecycle and preview

| Variable | Default | Contract |
| --- | --- | --- |
| `TELEGRAM_BOT_TOKEN` | unset | Telegram bot credential. Notifications require this and `TELEGRAM_CHANNEL_ID`. |
| `TELEGRAM_CHANNEL_ID` | unset | Numeric channel ID or a value beginning with `@`; notifications require this and the bot token. |
| `TELEGRAM_NOTIFY_EXISTING_ON_STARTUP` | `false` | Boolean controlling whether the first ready snapshot is eligible for automatic notification. |
| `TELEGRAM_CLUSTER_RADIUS_KM` | `5` | Finite number from `0.01` through `100`. |
| `TELEGRAM_CLUSTER_TIME_WINDOW` | `01:30:00` | Duration from 1 minute through 1 day. |
| `TELEGRAM_SEEN_RETENTION` | `48:00:00` | Duration from 1 minute through 30 days and at least `FIRMS_ACTIVE_WINDOW`. |
| `TELEGRAM_PREVIEW_RETRY_WINDOW` | `01:00:00` | Duration from zero through 1 day. |
| `TELEGRAM_PREVIEW_WIDTH_KM` | `30` | Positive finite number. |
| `TELEGRAM_PREVIEW_HEIGHT_KM` | `20` | Positive finite number. |
| `TELEGRAM_LARGE_PREVIEW_WIDTH_KM` | `45` | Positive finite number. |
| `TELEGRAM_LARGE_PREVIEW_HEIGHT_KM` | `30` | Positive finite number. |
| `TELEGRAM_PREVIEW_PIXEL_WIDTH` | `900` | Integer greater than or equal to 1. |
| `TELEGRAM_PREVIEW_PIXEL_HEIGHT` | `600` | Integer greater than or equal to 1. |
| `TELEGRAM_LARGE_CLUSTER_MIN_DETECTIONS` | `8` | Integer greater than or equal to 1. |
| `TELEGRAM_LARGE_CLUSTER_MIN_FRP_MW` | `500` | Non-negative finite number. |
| `TELEGRAM_LARGE_CLUSTER_MIN_DIAMETER_KM` | `8` | Non-negative finite number. |

### Telegram land-cover and visibility policy

| Variable | Default | Contract |
| --- | --- | --- |
| `TELEGRAM_LAND_COVER_FILTER_ENABLED` | `true` | Boolean. |
| `TELEGRAM_VEGETATION_PERCENT_THRESHOLD` | `50` | Finite percentage from 0 through 100. |
| `TELEGRAM_BUILT_UP_PROXIMITY_KM` | `2` | Finite number from 0 through 100. |
| `TELEGRAM_VEGETATION_MAX_FRP_MW` | `300` | Non-negative finite number used only when the high-FRP exception is enabled. |
| `TELEGRAM_KEEP_HIGH_FRP_VEGETATION` | `false` | Boolean enabling the configured high-FRP vegetation exception. |
| `TELEGRAM_KEEP_MULTI_SATELLITE_VEGETATION` | `false` | Boolean enabling the configured multi-satellite vegetation exception. |
| `TELEGRAM_VISIBILITY_FILTER_ENABLED` | `true` | Boolean. |
| `TELEGRAM_MIN_FRP_MW` | `50` | Non-negative finite number; zero disables this requirement. |
| `TELEGRAM_MIN_THERMAL_CONTRAST_K` | `20` | Non-negative finite number; zero disables this requirement. |
| `TELEGRAM_MIN_CLUSTER_DETECTIONS` | `2` | Integer greater than or equal to 1. |
| `TELEGRAM_MIN_MODIS_CONFIDENCE_PERCENT` | `60` | Finite percentage from 0 through 100; zero disables the MODIS requirement. |
| `TELEGRAM_MIN_VIIRS_CONFIDENCE` | `n` | `l`, `n`, or `h`, case-insensitive. |
| `TELEGRAM_REQUIRE_DAYTIME` | `true` | Boolean. |
| `TELEGRAM_REQUIRE_PREVIEW` | `true` | Boolean controlling whether automatic candidates without exact imagery are discarded after the retry window. |

Every Telegram option is parsed even when Telegram credentials are absent. An invalid optional value can therefore stop startup while Telegram would otherwise be disabled.

No credentials disables Telegram without affecting the API. Supplying only one credential logs a warning and leaves it disabled. With both values, startup calls Telegram to validate the bot, channel type, membership, and permission to post. Validation failure disables notifications until process restart; it does not terminate the API.

## External services and resilience

| Service | Server-side use | Failure boundary |
| --- | --- | --- |
| NASA FIRMS | Country and area CSV plus MAP_KEY status. | Isolated by country/source segment; stale data is retained where available. |
| NASA GIBS | Exact-date previews, availability domains, annual land-cover domains, and tiles. | Preview/land-cover results become unavailable; FIRMS and HTTP API continue. |
| Telegram Bot API | Startup validation and outbound channel messages. | Notifier can disable or defer; polling and HTTP API continue. |
| Natural Earth | Embedded boundary data loaded from Core. | Missing or unusable geometry for a configured country is a fatal startup error. |
| unpkg, GIBS tiles, Google Maps | Browser-only viewer resources. | Viewer provider can fail while server APIs and polling continue. |

Configured HTTP clients use bounded total and attempt timeouts, exponential retry delay with jitter, and `Retry-After` handling. FIRMS uses its configured request timeout; GIBS and Telegram use fixed 30-second total policies. See [Program.cs](../src/ThermalWatch.Api/Program.cs) for retry counts and attempt-timeout calculation.

Live provider availability, data ranges, and error bodies change independently of this repository. Durable behavior is the client's verified handling logic, not a particular response observed on one date.

## Observability and security

Serilog writes structured console events. `LOGGING_MINIMUM_LEVEL` controls the application minimum while the host suppresses noisy ASP.NET, HTTP-client, and resilience categories. Logs report safe error summaries, segment refreshes, snapshot publication, notification filtering, preview state, and sends; they must never include credential values.

There is no health/readiness route, metrics endpoint, tracing, external log sink, alert, or dashboard. `/api/anomalies` source statuses are the only built-in structured operational diagnostics. A viewer refresh rereads the snapshot and does not force an upstream poll.

All current HTTP endpoints are unauthenticated. Cross-origin `GET` is allowed:

- `/api/anomalies` and `/api/viewer/config` are read-only.
- `/api/viewer/config` intentionally exposes a browser API key when configured.
- `/api/telegram/send-top` sends Telegram messages and must sit behind an appropriate network access boundary. Its use is not safe as a health check.

## Local container publishing

The repository uses the .NET SDK's `PublishContainer` target and has no Dockerfile:

```bash
ContainerRepository=thermalwatch \
ContainerPort=8080 \
dotnet publish src/ThermalWatch.Api/ThermalWatch.Api.csproj \
  -c Release \
  /t:PublishContainer \
  /p:ContainerImageTags=local
```

```bash
docker run --rm -p 8080:8080 \
  -e FIRMS_MAP_KEY="$FIRMS_MAP_KEY" \
  -e FIRMS_COUNTRIES=UKR,RUS \
  thermalwatch:local
```

The service writes no required persistent application data. Configuration changes require a new process.

## CI artifacts and images

- Pull requests run the Release test suite in [pr.yml](../.github/workflows/pr.yml).
- Pushes to `main` call publishing workflows but do not explicitly run tests.
- [publish-builds.yml](../.github/workflows/publish-builds.yml) creates self-contained, single-file outputs for Windows, macOS, glibc Linux, and musl Linux, then compresses per-runtime archives.
- [publish-container.yml](../.github/workflows/publish-container.yml) publishes Linux, Alpine, and Windows images and assembles `ghcr.io/<repository-owner>/thermalwatch:latest`.
- Renovate runs hourly and may automerge configured dependency and action updates.

Known packaging limitation: a single-file publish produces the executable alongside the static-web-assets manifest and `wwwroot` files, but the current compression step archives only the executable. Do not assume the compressed application archives include a working web viewer. The container publishing path uses the full publish output and is the repository's suitable packaging path for the viewer.

The repository contains no production deployment manifests, immutable release tags, environment rollout, rollback automation, secret-store configuration, branch-protection definition, or artifact-retention policy. Do not infer those from publishing workflows.

## Failure and recovery

| Condition | Runtime behavior | Recovery |
| --- | --- | --- |
| Invalid application option or requested country geometry | Process prints a safe startup error and exits with code 1. | Correct environment configuration or embedded data, then restart. |
| FIRMS segment failure | Retain the previous complete segment, mark it stale, and continue other segments. | Later polls retry automatically. Inspect source statuses and logs. |
| Verified country-feature outage | Switch globally to polygon-clipped area fallback and probe the country API after one hour. | Automatic when the country API succeeds again. |
| GIBS preview unavailable | Keep automatic candidate pending until retry expiry; then discard if preview is required or send text-only if allowed. | Later snapshots retry pending work until expiry. |
| GIBS land cover unavailable or invalid | Retain the notification candidate and record fail-open diagnostics. | Later candidates retry after cache expiry. |
| Telegram startup validation failure | Disable notifier for the process lifetime. | Correct credentials/channel permissions and restart. |
| Telegram automatic send returns `400`, `401`, or `403` | Disable automatic notifier processing for the process lifetime. | Correct the permanent condition and restart. |
| Telegram transient send failure | Keep the pending item and return to the snapshot loop. | A later snapshot update retries it. |
| Process restart | Lose snapshots, deduplication, pending notifications, and caches; run an immediate poll. | Expected stateless recovery; monitor startup and first ready snapshot. |
| Browser map dependency failure | Show provider/UI error; server polling and APIs remain available. | Restore browser network/key/provider access and refresh. |
