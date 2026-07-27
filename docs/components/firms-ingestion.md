# FIRMS ingestion

> **Purpose:** Explain how ThermalWatch obtains, validates, falls back, and publishes FIRMS observations.
> **Scope:** Poll scheduling, current and historical country/source segments, country capability, area fallback, CSV parsing, boundaries, snapshot/history publication, and staleness.
> **Sources of truth:** [Poller](../../src/ThermalWatch.Api/FirmsPollingService.cs), [history backfill](../../src/ThermalWatch.Api/FirmsHistoryBackfill.cs), [FIRMS client](../../src/ThermalWatch.Core/FirmsClient.cs), [boundary catalog](../../src/ThermalWatch.Core/CountryBoundaryCatalog.cs), [snapshot store](../../src/ThermalWatch.Core/AnomalySnapshotStore.cs), and [history store](../../src/ThermalWatch.Core/FirmsHistoryStore.cs).
> **Update when:** Polling, backfill, concurrency, FIRMS routes, fallback detection, boundary envelopes, CSV parsing, segment publication, history retention, or history readiness changes.

## Segment model and polling

Each configured country is crossed with the four source IDs in `FirmsSources.All`: MODIS NRT and Suomi-NPP, NOAA-20, and NOAA-21 VIIRS NRT. Sources remain separate through ingestion and the API.

The background poller refreshes the active range once immediately and publishes it before attempting history backfill. After the current refresh and backfill attempt complete, it waits at least one configured interval plus up to 10 percent positive jitter, so cycles neither overlap nor shorten the post-cycle pause. A current cycle where no segment succeeds doubles the next base delay for each consecutive total failure, capped at the greater of one hour or the configured interval; any current segment success resets that backoff. Historical failures do not contribute to this active-cycle backoff.

The first segment is refreshed before the remaining parallel work so the process-wide country-API capability is established before concurrent requests. At most two remaining segments refresh concurrently. `FIRMS_MAX_CONCURRENCY` independently bounds admitted FIRMS HTTP operations across those segments.

Every current result is a complete segment success or failure. Current results update their covered history dates first, and the snapshot store atomically publishes one immutable active snapshot only after all current segments finish. Backfill follows as a separate bounded batch.

## Country-first acquisition

The preferred request is FIRMS country CSV for the configured country and source. FIRMS day ranges are UTC-calendar-based, so current country and area requests derive their range as the configured active window rounded up to whole days, plus the current calendar day. The default 24-hour window therefore fetches two calendar days, while a 72-hour window fetches four. Snapshot construction still applies the exact configured rolling window locally.

The same client supports explicit dated ranges of one through five inclusive UTC dates. Startup history uses six consecutive five-day requests for each configured country/source, covering the 30 completed dates before today. Dated requests retain the same country-first capability checks, verified area fallback, parsing, clipping, timeout, and complete-segment semantics as current requests.

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

Malformed data rows are skipped and logged safely. A nonempty response where every row is unusable fails the segment; an empty valid dataset succeeds with no anomalies. Duplicate anomaly IDs inside a response are removed.

## Snapshot publication and staleness

A successful result replaces its segment anomalies, timestamps, error state, and ingestion mode. A failed result:

- Retains the previous complete anomalies.
- Retains the last successful ingestion mode.
- Updates the attempt time, marks the segment stale, and records a safe error.

Snapshot construction removes observations outside `now - active window` through `now`, deduplicates by ID across all segments, and sorts newest first then by ID. `IsReady` becomes true after any segment succeeds. Once ready, any stale configured segment makes the snapshot partially stale.

The current snapshot is swapped atomically. A bounded one-item, drop-oldest channel notifies the single Telegram consumer; HTTP reads do not consume that channel.

## Daily history, backfill, and API

The in-memory history retains 31 UTC dates: the preceding 30 completed dates plus today's live bucket. Each date has one slice for every configured country crossed with the four existing NRT sources. A successful current or dated response replaces every covered date/source/country slice, including with an empty anomaly array; a failure retains the last complete slice where one exists, updates its attempt status, and marks it stale. Successful range responses are split by each anomaly's UTC acquisition date before publication.

After every current snapshot publication, [FirmsHistoryBackfill.cs](../../src/ThermalWatch.Api/FirmsHistoryBackfill.cs) considers six five-day windows for every country/source and requests only windows containing a missing or stale completed-date slice. It admits at most two backfill requests concurrently and commits the completed batch to the history store in one publication. A later polling cycle retries incomplete windows. Active refreshes continue updating all UTC dates covered by their latest request, including today, so history remains live after startup.

Each history day combines and ID-deduplicates raw anomalies across configured countries and all existing NRT sources, including every satellite returned by those feeds. It creates raw connected clusters with the configured notification radius and time window but does not apply notification eligibility filters. History and current eligible-cluster summaries share [NotificationClusterSummary.cs](../../src/ThermalWatch.Core/NotificationClusterSummary.cs), including cluster/representative properties, aggregate FRP, detection count, diameter, and member IDs. The separately exposed anomaly array remains the source for complete member measurements.

History `IsReady` requires a successful, non-stale slice for every configured country/source on all 30 completed dates. Today's bucket does not participate in baseline readiness. At UTC rollover the store discards the expired oldest date, adds the new today bucket, and reevaluates the completed baseline. History state is process-local and is rebuilt after restart.

`GET /api/history` reads this immutable state without calling NASA. It defaults to all retained dates in chronological order and accepts optional inclusive `from` and `to` dates in exact `YYYY-MM-DD` form; dates must be within retention and the range cannot exceed 31 dates. Incomplete backfill remains HTTP `200`, with top-level readiness/staleness and per-day completeness, staleness, and segment diagnostics. Reads never initiate or retry acquisition.

## Failure boundaries

- Invalid startup configuration or unusable requested boundary data terminates the process.
- A segment failure does not block successful countries or sources.
- Unexpected client exceptions become a generic safe segment error.
- API clients receive the retained snapshot and source diagnostics with HTTP `200`; they do not trigger recovery.
- API clients also receive partial retained history with HTTP `200`; incomplete history causes the enabled notification baseline criterion to fail closed but does not remove current anomalies.
- Later polling cycles retry failed current and historical FIRMS work automatically; only a current cycle with zero successful segments activates exponential cycle backoff.

Focused FIRMS client and scheduler tests use fake `HttpMessageHandler` responses, embedded boundary fixtures, and fake time. There is no direct live-service integration test; provider checks must remain bounded and cannot replace deterministic tests.
