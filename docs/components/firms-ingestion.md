# FIRMS ingestion

> **Purpose:** Explain how ThermalWatch obtains, validates, falls back, and publishes FIRMS observations.
> **Scope:** Poll scheduling, country/source segments, country capability, area fallback, CSV parsing, boundaries, and snapshot staleness.
> **Sources of truth:** [Poller](../../src/ThermalWatch.Api/FirmsPollingService.cs), [FIRMS client](../../src/ThermalWatch.Core/FirmsClient.cs), [boundary catalog](../../src/ThermalWatch.Core/CountryBoundaryCatalog.cs), and [snapshot store](../../src/ThermalWatch.Core/AnomalySnapshotStore.cs).
> **Update when:** Polling, concurrency, FIRMS routes, fallback detection, boundary envelopes, CSV parsing, or segment publication changes.

## Segment model and polling

Each configured country is crossed with the four source IDs in `FirmsSources.All`: MODIS NRT and Suomi-NPP, NOAA-20, and NOAA-21 VIIRS NRT. Sources remain separate through ingestion and the API.

The background poller refreshes once immediately. After every completed cycle it waits at least one configured interval plus up to 10 percent positive jitter, so cycles neither overlap nor shorten the post-cycle pause. A cycle where no segment succeeds doubles the next base delay for each consecutive total failure, capped at the greater of one hour or the configured interval; any successful segment resets that backoff.

The first segment is refreshed before the remaining parallel work so the process-wide country-API capability is established before concurrent requests. At most two remaining segments refresh concurrently. `FIRMS_MAX_CONCURRENCY` independently bounds admitted FIRMS HTTP operations across those segments.

Every result is a complete segment success or failure. The store atomically publishes one immutable snapshot only after the cycle finishes.

## Country-first acquisition

The preferred request is FIRMS country CSV for the configured country, source, and one-day range. The active window cannot exceed that one-day upstream request.

Country-API capability is process-wide:

- Unknown capability is serialized behind a gate while one request determines it.
- A successful country request marks the capability available.
- An explicit country-feature outage response enables area fallback.
- The client's recognized ambiguous HTTP `400` outage response enables fallback only when the FIRMS MAP_KEY status endpoint confirms a usable, non-exhausted key.
- Authentication, rate limit, network, ordinary server, request-validation, and dataset failures do not independently enable fallback.
- While fallback is active, the client probes the country API after one hour. A successful probe restores country mode; an ordinary failed probe schedules the next probe and continues fallback.

The matching and key-status rules in [FirmsClient.cs](../../src/ThermalWatch.Core/FirmsClient.cs) are authoritative. External error wording is not a durable contract.

## Area fallback and boundaries

[CountryBoundaryCatalog.cs](../../src/ThermalWatch.Core/CountryBoundaryCatalog.cs) loads only requested countries from the embedded compressed Natural Earth Admin 0 data. It joins multiple parts, repairs invalid geometry where possible, prepares it for point tests, derives the complete WGS84 geometry envelope, and fails startup when a requested country has no usable polygon or multipolygon.

FIRMS area acquisition supports bounds up to the entire world, so fallback sends exactly one complete envelope request for each country/source segment. An antimeridian-spanning country can therefore use a world-width numeric envelope. The enclosing rectangle may return observations outside the country, but the client applies its existing response-size limit and checks every parsed observation against the prepared country geometry.

The complete response is clipped locally with polygon coverage checks and deduplicated by anomaly ID. A failed or invalid envelope response fails the segment atomically; no partial rectangle result is published. Country and area responses are never merged for one segment refresh. Natural Earth geometry is generalized cartographic data; see its [embedded license](../../src/ThermalWatch.Core/Data/NaturalEarth.LICENSE.txt).

## Response validation and parsing

The configured request timeout starts only after admission through the global request gate and remains active through response-body consumption. It bounds one area operation or the complete country capability operation, including a MAP_KEY status check needed to verify an ambiguous country-endpoint response. The HTTP resilience pipeline permits one retry, gives each transport attempt 40 percent of the total timeout without a fixed ceiling, and honors `Retry-After`; a body-read timeout fails the segment and is retried by a later polling cycle rather than immediately repeating the download.

The client bounds response size, rejects incompatible content types and upstream status categories, and requires common plus source-specific CSV headers. It parses values with invariant culture and validates:

- Finite latitude and longitude inside geographic ranges.
- Nonempty satellite, instrument, and required fields.
- `D` or `N` pass classification.
- UTC acquisition date and four-digit time.
- Finite numeric optional values when present.
- MODIS numeric confidence versus normalized VIIRS confidence category.

Malformed data rows are skipped and logged safely. A nonempty response where every row is unusable fails the segment; an empty valid dataset succeeds with no detections. Duplicate IDs inside a response are removed.

## Snapshot publication and staleness

A successful result replaces its segment detections, timestamps, error state, and ingestion mode. A failed result:

- Retains the previous complete detections.
- Retains the last successful ingestion mode.
- Updates the attempt time, marks the segment stale, and records a safe error.

Snapshot construction removes observations outside `now - active window` through `now`, deduplicates by ID across all segments, and sorts newest first then by ID. `IsReady` becomes true after any segment succeeds. Once ready, any stale configured segment makes the snapshot partially stale.

The current snapshot is swapped atomically. A bounded one-item, drop-oldest channel notifies the single Telegram consumer; HTTP reads do not consume that channel.

## Failure boundaries

- Invalid startup configuration or unusable requested boundary data terminates the process.
- A segment failure does not block successful countries or sources.
- Unexpected client exceptions become a generic safe segment error.
- API clients receive the retained snapshot and source diagnostics with HTTP `200`; they do not trigger recovery.
- Later polling cycles retry failed FIRMS work automatically; only a cycle with zero successful segments activates exponential cycle backoff.

Focused FIRMS client and scheduler tests use fake `HttpMessageHandler` responses, embedded boundary fixtures, and fake time. There is no direct live-service integration test; provider checks must remain bounded and cannot replace deterministic tests.
