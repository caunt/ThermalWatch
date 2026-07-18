# ThermalWatch

ThermalWatch is a deliberately small .NET 10 service that polls NASA FIRMS near-real-time thermal anomalies for multiple countries. It keeps an immutable in-memory snapshot, exposes the raw observations through an unauthenticated endpoint, and can send anomaly zones to one Telegram channel.

A thermal anomaly is not necessarily a wildfire. Detections may represent industrial heat, gas flares, agricultural burning, explosions, or other hot surfaces. A recent detection does not prove that its heat source is still active. FIRMS is near-real-time satellite reporting, not continuous live monitoring.

## Configuration

All application configuration comes from environment variables. Names use uppercase snake case with a single underscore, such as `FIRMS_MAP_KEY`; .NET-style names such as `Firms__MapKey` are not supported.

Required variables:

| Variable | Description |
| --- | --- |
| `FIRMS_MAP_KEY` | Free 32-character NASA FIRMS MAP_KEY. Request one from the [FIRMS API page](https://firms.modaps.eosdis.nasa.gov/api/map_key/). |
| `FIRMS_COUNTRIES` | Comma-separated ISO alpha-3 country codes. Entries are trimmed, uppercased, validated, and deduplicated. Example: `UKR,RUS`. |

Optional variables:

| Variable | Default | Description |
| --- | --- | --- |
| `FIRMS_POLL_INTERVAL` | `00:05:00` | Delay between non-overlapping refresh cycles. |
| `FIRMS_ACTIVE_WINDOW` | `24:00:00` | Rolling window defining current detections; maximum 24 hours. |
| `FIRMS_REQUEST_TIMEOUT` | `00:00:45` | Overall timeout for one resilient FIRMS request. |
| `FIRMS_MAX_CONCURRENCY` | `4` | Maximum simultaneous country/source requests. |
| `TELEGRAM_BOT_TOKEN` | unset | Telegram bot token. Notifications require this and `TELEGRAM_CHANNEL_ID`. |
| `TELEGRAM_CHANNEL_ID` | unset | One numeric channel ID or `@channel_username` for every country. |
| `TELEGRAM_NOTIFY_EXISTING_ON_STARTUP` | `false` | Send detections already present on the first successful refresh. |
| `TELEGRAM_CLUSTER_RADIUS_KM` | `5` | Maximum Haversine distance within one notification zone. |
| `TELEGRAM_CLUSTER_TIME_WINDOW` | `01:30:00` | Maximum acquisition-time separation within a zone. |
| `TELEGRAM_SEEN_RETENTION` | `48:00:00` | In-memory notification deduplication retention; must be at least the FIRMS active window. |
| `TELEGRAM_PREVIEW_RETRY_WINDOW` | `01:00:00` | Time to await exact-date GIBS imagery before applying the configured preview fallback. |
| `TELEGRAM_PREVIEW_WIDTH_KM` | `30` | Geographic width of a normal Telegram preview crop. |
| `TELEGRAM_PREVIEW_HEIGHT_KM` | `20` | Geographic height of a normal Telegram preview crop. |
| `TELEGRAM_LARGE_PREVIEW_WIDTH_KM` | `45` | Geographic width of a large-cluster Telegram preview crop. |
| `TELEGRAM_LARGE_PREVIEW_HEIGHT_KM` | `30` | Geographic height of a large-cluster Telegram preview crop. |
| `TELEGRAM_PREVIEW_PIXEL_WIDTH` | `900` | Output image width in pixels for every Telegram preview. |
| `TELEGRAM_PREVIEW_PIXEL_HEIGHT` | `600` | Output image height in pixels for every Telegram preview. |
| `TELEGRAM_LARGE_CLUSTER_MIN_DETECTIONS` | `8` | Detection count that selects the large preview crop. |
| `TELEGRAM_LARGE_CLUSTER_MIN_FRP_MW` | `500` | Representative FRP in megawatts that selects the large preview crop. |
| `TELEGRAM_LARGE_CLUSTER_MIN_DIAMETER_KM` | `8` | Maximum pairwise cluster distance that selects the large preview crop. |
| `TELEGRAM_LAND_COVER_FILTER_ENABLED` | `true` | Suppress probable vegetation-fire clusters from Telegram using NASA MODIS land cover. |
| `TELEGRAM_VEGETATION_PERCENT_THRESHOLD` | `70` | Minimum percentage of cluster detections on vegetation classes for land-cover suppression. |
| `TELEGRAM_BUILT_UP_PROXIMITY_KM` | `2` | Retain a cluster when an urban/built-up land-cover pixel is within this distance of any detection. |
| `TELEGRAM_VEGETATION_MAX_FRP_MW` | `300` | Retain a cluster when representative FRP is at least this many megawatts. |
| `TELEGRAM_KEEP_MULTI_SATELLITE_CLUSTERS` | `true` | Retain clusters confirmed by more than one satellite. |
| `TELEGRAM_VISIBILITY_FILTER_ENABLED` | `true` | Require Telegram clusters to pass the likely-visible-in-imagery heuristic. |
| `TELEGRAM_MIN_FRP_MW` | `50` | Minimum representative fire radiative power in megawatts; `0` disables this requirement. |
| `TELEGRAM_MIN_THERMAL_CONTRAST_K` | `20` | Minimum representative primary-minus-secondary brightness temperature in kelvin; `0` disables this requirement. |
| `TELEGRAM_MIN_CLUSTER_DETECTIONS` | `2` | Minimum number of raw detections in a Telegram cluster; minimum configurable value is `1`. |
| `TELEGRAM_MIN_MODIS_CONFIDENCE_PERCENT` | `60` | Minimum MODIS numeric confidence percentage; `0` disables this requirement. |
| `TELEGRAM_MIN_VIIRS_CONFIDENCE` | `n` | Minimum VIIRS confidence category: `l`, `n`, or `h`. |
| `TELEGRAM_REQUIRE_DAYTIME` | `true` | Require a daytime (`D`) representative detection for Telegram notifications. |
| `TELEGRAM_REQUIRE_PREVIEW` | `true` | Suppress a Telegram candidate if an exact-date sensor-matched preview remains unavailable after the retry window. |
| `LOGGING_MINIMUM_LEVEL` | `Information` | `Verbose`, `Debug`, `Information`, `Warning`, `Error`, or `Fatal`. |

Telegram is disabled without credentials and does not affect the anomaly HTTP API. With both values configured, startup validation calls `GetMe`, resolves the channel, checks the bot's membership, and requires channel administrator permission to post messages. ThermalWatch only sends outbound messages; it uses neither polling nor webhooks. The manual Telegram endpoint returns HTTP `409` while Telegram is disabled or has not validated successfully.

## Telegram visibility filter

The visibility filter affects Telegram notification candidates only. The HTTP API continues to return every valid recent FIRMS detection. By default, a cluster must have at least two detections and its existing representative detection must be daytime, have at least 50 MW FRP, at least 20 K primary-to-background thermal contrast, and sufficient source-specific confidence. MODIS uses its numeric percentage; VIIRS confidence is ordered low (`l`), nominal (`n`), then high (`h`). An exact-date sensor-matched GIBS preview must also be available before the notification is sent.

This is only a heuristic intended to improve the chance that true-color imagery contains a visible smoke plume, burn area, or other obvious feature. It does not prove that a fire or smoke is visible. Clouds, smoke direction, the difference between image and detection timing, sensor resolution, and the physical size of the source can all limit visibility.

Set `TELEGRAM_VISIBILITY_FILTER_ENABLED=false` to restore the previous notification selection behavior. Set `TELEGRAM_REQUIRE_DAYTIME=false` to permit nighttime candidates using the existing matching nighttime GIBS layers. Set `TELEGRAM_REQUIRE_PREVIEW=false` to restore the text-only fallback when the preview retry window expires.

## Telegram land-cover filter

The land-cover filter affects Telegram notification clusters only. It does not remove or annotate FIRMS detections returned by `/api/anomalies`. It heuristically suppresses a probable vegetation fire when at least 70% of the cluster's detections are on vegetation land-cover classes, no urban/built-up pixel is within 2 km of any detection, representative FRP is below 300 MW, and the cluster is not protected as a multi-satellite event. Forests, shrublands, savannas, grassland, permanent wetland, cropland, and the cropland/natural vegetation mosaic are treated as vegetation.

Classification uses NASA GIBS [`MODIS_Combined_L3_IGBP_Land_Cover_Type_Annual`](https://gibs.earthdata.nasa.gov/wmts/epsg4326/best/1.0.0/WMTSCapabilities.xml), the annual combined Aqua/Terra MODIS IGBP land-cover product. ThermalWatch selects the most recent year GIBS advertises for every required tile and records it in debug logs. NASA currently advertises 2001–2024, so 2024 is the selected year where current data is available. Categorical classes are decoded from the rendered indexed tiles with NASA's official [`MODIS_IGBP_Land_Cover_Type` colormap](https://gibs.earthdata.nasa.gov/colormaps/v1.3/MODIS_IGBP_Land_Cover_Type.xml), rather than inferred from image appearance.

Strong events at or above the FRP threshold, events with urban land cover nearby, and multi-satellite events are retained by default. Missing FRP, unavailable NASA land-cover data, and unrecognized pixels also retain the notification candidate. Land-cover classification and these thresholds are imperfect heuristics; they do not prove whether an event is a vegetation fire or any other event type. Set `TELEGRAM_LAND_COVER_FILTER_ENABLED=false` to disable this filter and restore the existing Telegram behavior.

## Satellite feeds

Every configured country is queried through the preferred FIRMS country endpoint for all four feeds:

- `MODIS_NRT` (Terra and Aqua)
- `VIIRS_SNPP_NRT` (Suomi-NPP)
- `VIIRS_NOAA20_NRT` (NOAA-20)
- `VIIRS_NOAA21_NRT` (NOAA-21)

Satellites have different instruments, resolutions, overpass times, sensitivity, viewing geometry, and atmospheric conditions, so their observations remain separate in the API. Clustering is used only for Telegram notifications.

NASA may temporarily disable the FIRMS country API. ThermalWatch recognizes responses that specifically identify the country feature as unavailable and automatically switches to the available FIRMS area API. NASA currently returns a generic HTTP 400 for the disabled route, so ThermalWatch confirms the MAP_KEY through NASA's key-status service before treating that otherwise ambiguous response as a feature outage. Authentication, rate-limit, network, ordinary server, request-validation, and CSV failures do not enable fallback. Area results are clipped locally against embedded Natural Earth country polygons; points in a country's bounding rectangle but outside its polygon are discarded. Large or geographically dispersed countries are divided into non-overlapping, geometry-intersecting tiles so the service does not make one enormous area request.

The country API remains the primary source. While fallback is active, ThermalWatch probes it once per hour and automatically stops area requests as soon as a valid country response succeeds. Country and area responses are never merged for one country/source refresh. Every fallback tile must succeed before a segment is published; otherwise ThermalWatch retains the previous complete segment and marks it stale. Failures remain isolated by country and source.

Country boundaries come from [Natural Earth Admin 0 – Countries, 1:50m, version 5.1.2](https://www.naturalearthdata.com/downloads/50m-cultural-vectors/50m-admin-0-countries-2/). Natural Earth data is [public domain](https://www.naturalearthdata.com/about/terms-of-use/) and is embedded in the application, so fallback operation does not call a boundary service. These are generalized cartographic boundaries, not legal definitions: small coastal features may be simplified, and disputed territories or geopolitical claims may differ from other sources. A configured ISO alpha-3 code without usable embedded geometry fails startup clearly.

## Run locally

Requires the .NET 10 SDK:

```bash
FIRMS_MAP_KEY=... \
FIRMS_COUNTRIES=UKR,RUS \
dotnet run --project src/ThermalWatch.Api
```

The service listens on port `8080`.

## HTTP API

The anomaly endpoint is:

```text
GET http://localhost:8080/api/anomalies
```

It is free, read-only, unauthenticated, CORS-enabled, and always reads the current in-memory snapshot. API traffic never calls NASA.

Optional local filters are comma-separated where applicable:

```bash
curl "http://localhost:8080/api/anomalies?country=UKR,RUS"
curl "http://localhost:8080/api/anomalies?source=VIIRS_NOAA21_NRT&dayNight=D"
curl "http://localhost:8080/api/anomalies?satellite=Terra&since=2026-07-18T06:00:00Z"
```

`country` accepts ISO alpha-3 codes, `source` accepts the four feed IDs, `satellite` matches the FIRMS satellite value, `since` must be an ISO-8601 UTC timestamp inside the active window, and `dayNight` accepts `D` or `N`. Partial upstream failures remain HTTP `200` responses with stale source statuses in the body. Each source status also reports `ingestionMode` as `country`, `areaFallback`, or `none`; the last successful mode is retained while a segment is stale.

Each anomaly includes nullable `thermalContrastKelvin`, calculated as its primary brightness temperature minus its secondary or background brightness temperature when both values are available. Telegram filtering does not remove or annotate API items with notification state.

### Manual Telegram send

```text
POST http://localhost:8080/api/telegram/send-top?count=5
```

This endpoint reads the current in-memory FIRMS snapshot and sends the highest-ranked clusters that pass every configured Telegram visibility, NASA land-cover, and exact-date GIBS preview requirement. It does not refresh FIRMS. `count` defaults to `5`, must be an integer from `1` through `50`, and limits the number of anomaly posts rather than the number evaluated.

Manual sends treat all current detections as newly observed for that operation. They bypass normal seen-ID checks without changing seen IDs, pending previews, or future automatic deduplication. One manual execution can run at a time; overlapping calls return HTTP `409`. Individual anomaly-send failures do not stop later selected clusters and are reported by cluster ID in the JSON response.

The endpoint is unauthenticated and causes Telegram side effects. It is intended only for deployments where network access to the service is appropriately restricted. The existing CORS policy remains `GET`-only, but CORS is not authentication and does not replace that network restriction.

## GIBS previews

Photo notifications use NASA GIBS to compose matching base imagery and thermal-anomaly layers for the representative detection's sensor and acquisition date. Day observations use true color; night observations use the matching brightness-temperature layer. Normal alerts use a 30 × 20 km crop, while large clusters use a 45 × 30 km crop. Both produce a Telegram-friendly 900 × 600 landscape image centered on the representative detection.

A cluster uses the large crop when it has at least eight detections, its representative has at least 500 MW FRP, or its greatest pairwise Haversine distance is at least 8 km. All crop dimensions, pixel dimensions, and large-cluster thresholds can be overridden through the environment variables above.

GIBS imagery is date-based and is not claimed to represent the exact FIRMS acquisition minute. ThermalWatch verifies exact layer/date availability with GIBS `DescribeDomains`; it never accepts nearest-date imagery or substitutes another sensor, date, or day/night layer.

## Container

```bash
ContainerRepository=thermalwatch \
ContainerPort=8080 \
dotnet publish src/ThermalWatch.Api/ThermalWatch.Api.csproj \
  -c Release \
  /t:PublishContainer \
  /p:ContainerImageTags=local

docker run --rm -p 8080:8080 \
  -e FIRMS_MAP_KEY=... \
  -e FIRMS_COUNTRIES=UKR,RUS \
  -e TELEGRAM_BOT_TOKEN=... \
  -e TELEGRAM_CHANNEL_ID=@example_channel \
  thermalwatch:local
```

The .NET SDK publishes the image directly through `PublishContainer`; the repository does not use a Dockerfile. The final image uses the official .NET 10 ASP.NET runtime, listens on port 8080, runs as the image's built-in non-root `app` user, and writes no persistent application data.

## GHCR

Pull requests run the Release test suite. Pushes to `main` publish self-contained application archives for Windows, macOS, glibc Linux, and musl Linux, and publish a multi-platform `latest` container manifest for Linux, Alpine, and Windows. The image is named:

```text
ghcr.io/owner/thermalwatch:latest
```

Publishing uses the repository `GITHUB_TOKEN` with `packages: write` permission.
