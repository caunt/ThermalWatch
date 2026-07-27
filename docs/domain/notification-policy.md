# Notification policy

> **Purpose:** Define the non-obvious domain rules that distinguish raw thermal observations from Telegram notification candidates.
> **Scope:** Anomaly meaning and identity, clustering, automatic selection, visibility, historical-FRP and land-cover filters, diagnostics, imagery, and manual sends.
> **Sources of truth:** [Anomaly model](../../src/ThermalWatch.Core/Anomaly.cs), [notification cluster](../../src/ThermalWatch.Core/NotificationCluster.cs), [clustering](../../src/ThermalWatch.Core/NotificationClustering.cs), [candidate engine](../../src/ThermalWatch.Core/NotificationCandidateEngine.cs), [metadata policy](../../src/ThermalWatch.Core/NotificationPolicy.cs), [historical-FRP policy](../../src/ThermalWatch.Core/NotificationHistoricalFrpPolicy.cs), [history store](../../src/ThermalWatch.Core/FirmsHistoryStore.cs), [GIBS client](../../src/ThermalWatch.Core/GibsClient.cs), [land-cover policy](../../src/ThermalWatch.Core/NotificationLandCoverPolicy.cs), and [nearby-feature client](../../src/ThermalWatch.Core/NearbyFeatureClient.cs).
> **Update when:** Observation identity, clustering, representative choice, eligibility, diagnostic explanation, filtering order, imagery or nearby-context policy, or manual-send semantics change.

## Observation meaning and API boundary

A FIRMS thermal anomaly is a satellite observation of heat. It may indicate wildfire, industrial heat, gas flaring, agricultural burning, an explosion, or another hot surface. Near-real-time acquisition is not continuous monitoring, and a recent observation does not prove the source remains active.

The HTTP API is the raw-observation boundary:

- It returns every valid FIRMS observation in the active snapshot, across MODIS and all three VIIRS feeds.
- It may apply only caller-requested query filters from [AnomalyQuery.cs](../../src/ThermalWatch.Api/AnomalyQuery.cs).
- Notification visibility, historical-FRP, land-cover, preview, mapped location context, deduplication, and clustering state never remove or annotate API anomalies.

`GET /api/history` is a separate raw-history boundary. It groups retained observations and unfiltered clusters by UTC date for baseline inspection; it does not change the active anomaly contract or classify an observation as an event.

An anomaly ID is a deterministic truncated SHA-256 hash of country, source, satellite, UTC acquisition second, latitude, and longitude. Thermal contrast is primary brightness minus secondary/background brightness only when both values exist. [AnomalyId.cs](../../src/ThermalWatch.Core/AnomalyId.cs) and [Anomaly.cs](../../src/ThermalWatch.Core/Anomaly.cs) define these contracts.

## Clustering and representative selection

Notification clustering forms connected components. Two detections are linked when both their acquisition-time separation and Haversine distance meet the configured limits. Linkage is transitive, so a cluster's greatest pairwise diameter may exceed its link radius.

Clusters can cross configured countries, FIRMS sources, and satellites. Members are sorted newest first, then by ID. The representative is selected by:

1. Highest available FRP; missing FRP ranks below every available value.
2. Newest acquisition time.
3. Lexically smallest anomaly ID.

The cluster ID is a deterministic hash of its sorted member IDs. Total cluster FRP sums every member's available finite FRP and is unavailable only when no member supplies a usable value. Members without FRP do not prevent the remaining values from contributing. Because a connected cluster can cross acquisition times, FIRMS sources, and satellites, this sum is a notification-priority heuristic rather than an instantaneous physical power measurement.

Preview sensor/date, representative filters, map links, and much of the message are based on the representative, while total FRP, multi-satellite, and detection-count facts use all members. Because adding a member changes the cluster hash, automatic delivery does not use cluster ID alone as the identity of an ongoing episode.

After an automatic message sends successfully, its members establish a delivered episode. A later cluster continues that episode when any new member is linked to a delivered member by the same radius and acquisition-time rule. Suppressed members extend the history, so continuity is transitive across snapshots: A linked to B and B linked to C remains one episode even if A is not linked directly to C. A cluster outside both limits can establish a new episode. The first successful message is not edited when later detections extend it.

## Automatic notification lifecycle

On each ready snapshot:

1. Expire startup-incident and delivered-episode histories, then build connected clusters from every observation in the current active snapshot.
2. When the historical-FRP filter is enabled, require the complete preceding-30-day baseline before consuming first-ready startup state or evaluating delivery. An incomplete baseline rejects the current evaluation as unavailable and remains retryable after a later snapshot publication.
3. On the first baseline-ready snapshot with `NOTIFICATION_SEND_EXISTING_ON_STARTUP` disabled, apply the complete metadata, historical-FRP, land-cover, and required-preview policy to every cluster. Record each eligible cluster as a startup incident without sending it. Leave every ineligible cluster unrecorded so later snapshots can reevaluate it.
4. On later snapshots, suppress and extend clusters continuing a recorded startup incident without rerunning filters or imagery work.
5. If a cluster continues an already delivered episode, suppress it and extend that episode without rerunning filters or imagery work.
6. For every remaining cluster, apply metadata visibility rules, then the historical-FRP criterion, then NASA land cover for every cluster member when enabled.
7. Attempt the current exact-date preview once. A missing required preview rejects the cluster for this snapshot; when previews are optional, continue with a text candidate immediately.
8. Look up mapped location context around the representative and send. Only successful automatic delivery establishes a delivered episode; rejection, mapped-context failure, and send failure do not.

Every later snapshot repeats eligibility evaluation from its complete current data for incidents that have not been startup-suppressed or delivered. A cluster rejected at startup or later because imagery is unavailable can therefore qualify after a later publication without retaining an unsent candidate. A transient send failure likewise records no delivered episode and remains retryable. Startup incidents and delivered episodes use the same configured radius, time window, episode retention, transitive extension, and 100,000-anomaly per-history cap.

## Visibility policy

When enabled, [NotificationPolicy.cs](../../src/ThermalWatch.Core/NotificationPolicy.cs) evaluates in this order:

1. Required daytime pass.
2. Minimum cluster member count.
3. Source-specific representative confidence: MODIS numeric percentage or ordered VIIRS low/nominal/high category.
4. Minimum representative FRP when the configured threshold is greater than zero.
5. Minimum total cluster FRP when the configured threshold is greater than zero.
6. Minimum representative thermal contrast when the configured threshold is greater than zero.

A daytime pass is not required by default. Set `NOTIFICATION_REQUIRE_DAYTIME=true` to reject clusters whose representative is not from a daytime pass; disabling the criterion still permits nighttime preview selection and pass-matched imagery.

Of the two FRP criteria, only total cluster FRP is enabled by default. Representative FRP defaults to zero, which disables that criterion; set `NOTIFICATION_MIN_FRP_MW` to a positive threshold to opt in.

A required value that is absent rejects the candidate. For total cluster FRP, absence means every member lacks a usable FRP value; partially available clusters use the sum of their known values. Exact defaults and ranges live in [operations](../operations.md); tests should express policy edge cases rather than prose duplicating implementation branches.

## Historical location FRP policy

The historical-FRP criterion is enabled by default and is shared by automatic delivery, manual preparation, Viewer eligible-cluster listing, and Viewer diagnostics. It asks whether a current location's total measured FRP exceeds the 95th percentile of its comparable cluster totals from the preceding 30 complete UTC days; it does not infer cause, persistence, or event type.

For each completed history day, Core removes every anomaly whose deterministic ID is already a member of the current active cluster, then rebuilds that day's raw clusters. This avoids comparing a current-window observation with itself when the active window crosses a UTC-date boundary and allows the remaining historical members to split into their correct connected components. A rebuilt historical cluster is spatially comparable when any of its members lies within the configured cluster radius of any current member. The configured time window still governs clustering within a day, but no cross-day time proximity is required for this location match.

The current cluster must have an available total FRP and that total must be strictly greater than the 95th percentile of all spatially matching rebuilt historical-cluster totals. Core sorts the comparable totals and uses the inclusive, linearly interpolated position `(sample count - 1) * 0.95`; one sample therefore supplies its own threshold. Equality fails. A historical cluster with no available member FRP is ignored; if no matching cluster has comparable FRP, the criterion passes. A current cluster with no available total FRP fails.

The enabled criterion fails closed with an unavailable outcome until all configured country/source slices are complete and fresh for all 30 prior dates. Today's live bucket is excluded from the baseline even though it is retained by `/api/history`. Disabling `NOTIFICATION_HISTORICAL_FRP_FILTER_ENABLED` reports the criterion as disabled and removes the readiness requirement. See [ADR 0009](../decisions/0009-use-95th-percentile-historical-frp-threshold.md) for the current threshold decision.

## Land-cover policy

The [land-cover policy](../../src/ThermalWatch.Core/NotificationLandCoverPolicy.cs) uses NASA's annual combined MODIS IGBP product. The GIBS client selects the newest year common to every required tile, samples the detection pixels plus pixels intersecting the configured proximity, and decodes the official indexed colors into classes.

- IGBP classes 1–12 and 14 count as vegetation.
- Class 13 means urban/built-up and retains an otherwise vegetation-dominated cluster when present within proximity.
- Vegetation at or above the configured percentage is suppressed when no nearby class 13 exists, unless an explicitly enabled high-FRP or multi-satellite vegetation exception applies.
- Missing FRP does not bypass vegetation suppression.
- Unavailable, inconsistent, or invalid NASA land-cover data fails open: retain the candidate and report the unavailable reason.

These rules are heuristics, not event classification. Land cover, confidence, FRP, and imagery cannot prove whether a wildfire or visible smoke exists.

## Mapped location context

The [mapped-context client](../../src/ThermalWatch.Core/NearbyFeatureClient.cs) uses `overpass-api.de` to perform one combined coordinate lookup. It queries named OpenStreetMap nodes, ways, and relations within 2 km, excluding elements with a `highway` or `railway` tag or `type=public_transport`. Elements carrying a blacklisted tag are discarded before ordering and limiting: any `waterway` tag is excluded, and `route=bus` excludes bus routes while retaining other route types. Node coordinates and Overpass-provided way/relation centers are validated and measured from the lookup observation with Haversine distance. Results are ranked by descending property count in the OSM `tags` object; equal counts use nearest distance, then OSM type and ID for deterministic ties. The shared coordinate cache retains up to 25 ranked results; Viewer diagnostics can return that complete set, while prepared automatic and manual notification candidates take only the highest-ranked five.

The same request asks Overpass which mapped areas contain the coordinate and accepts only named `city`, `town`, or `village` areas. It prefers an English name when present and otherwise uses the mapped name. When overlapping accepted areas exist, the most local numeric administrative level wins, followed by the more specific settlement kind and stable identity. Prepared Telegram candidates attach this single settlement for the representative coordinate; Viewer diagnostic contracts do not expose it. A point outside an accepted mapped settlement remains unnamed rather than using the nearest place.

Mapped location results are presentation context, not a notification criterion or event classification. They never change eligibility, ranking, delivery deduplication, or `/api/anomalies`. The automatic and manual paths query only the cluster representative and include both settlement and nearby-feature data. Prepared Telegram candidates preserve ranked order while skipping later features whose names case-insensitively match an earlier result, then take the first five unique names. The Viewer path queries the specifically selected observation even when another member is the cluster representative and consumes the complete ranked nearby-feature set without this name deduplication.

Retrieval is on demand, serialized, bounded, and cached. Provider, transport, timeout, oversized, or malformed-response failure returns no mapped context and logs a Warning; it does not block the diagnostic or Telegram delivery. Surfaces omit unavailable context, and nearby-feature surfaces warn that mapped proximity does not establish cause. For ways and relations, distance is to the supplied center rather than the nearest geometry edge.

## Viewer eligibility and diagnostics

The Viewer eligible-cluster query evaluates every connected component in one captured active snapshot. It applies the same metadata, historical-FRP, and land-cover rules as candidate preparation and, when configured, requires the same exact-date preview. Incomplete history and unavailable required previews fail closed; unavailable land cover fails open. It returns only passing clusters, ordered by the manual-send priority, and performs no mapped-context lookup.

This list is criteria-only, not a promise that automatic delivery will send a cluster. It neither applies nor mutates startup-incident or delivered-episode suppression. Repeated Viewer evaluation can therefore continue to list a startup-suppressed or already delivered episode, and it cannot consume or extend automatic lifecycle state.

Selecting an anomaly in the viewer asks the Core candidate engine to cluster every observation in the current active snapshot and find the connected component containing that anomaly. The diagnostic uses the same radius, time window, representative selection, metadata rules, historical baseline, land-cover policy, preview sizing, and exact-date preview client as automatic and manual candidate preparation. It also attaches nearby mapped context for the selected observation; that context remains outside eligibility criteria.

The diagnostic is deliberately exhaustive: it reports daytime, detection-count, source-specific confidence, representative FRP, total cluster FRP, thermal-contrast, historical location FRP, land-cover, and exact-preview outcomes even when an earlier criterion already blocks the candidate. Disabled criteria are identified explicitly. Incomplete history and an unavailable required preview block the current result; unavailable land cover remains non-blocking because that policy fails open.

This is a fresh, read-only evaluation. It neither reads nor changes startup incidents or delivered episodes. Refreshing diagnostics can therefore observe newly available GIBS data without changing later automatic or manual behavior.

## Preview policy

[GibsClient.cs](../../src/ThermalWatch.Core/GibsClient.cs) maps the representative source, satellite, and day/night pass to a representative thermal-anomaly overlay and pass-matched contextual base layers. Daytime uses true color; nighttime uses the corresponding brightness-temperature products.

The overlay and selected base must advertise the exact acquisition date. The client probes the requested crop rather than treating global date availability as proof that spatial pixels are ready. It prefers the representative satellite's base, then tries other supported satellites in the same sensor family, followed by the other family. A fallback changes only the contextual base: the representative thermal overlay remains authoritative, and the Telegram caption names both sources. The composed overlay uses GIBS's 25-pixel thermal-anomaly marker style so its red anomaly dots remain legible when Telegram scales the image. It never chooses the nearest date or a different pass. Imagery represents a date, not the exact acquisition minute.

Black, transparent, malformed, or mostly no-data base crops are unavailable and are not cached, so a later snapshot can retry transient GIBS ingestion gaps. Each automatic evaluation attempts the preview once. With the visibility filter and preview requirement enabled, an unavailable preview rejects the cluster for the current snapshot; otherwise it sends as text immediately. Crop selection uses the large dimensions when detection count, representative FRP, or cluster diameter meets its configured large-cluster threshold.

Every successfully composed notification preview is losslessly re-encoded as a PNG with a kilometre ruler before Core caches it for automatic or manual delivery. The inset bottom and left axes derive their scale from the complete configured image coverage, then label only the physical distance covered by each visible line; their image-border gaps are padding that represents omitted image distance. The ruler places one-kilometre ticks, labels ten-kilometre intervals and exact endpoints, shows the origin once, and identifies both axes in kilometres. Its anti-aliased sans-serif text has a dark outline and uses three quarters of the ruler geometry's reference size; the padding, text, and geometry scale with the shorter pixel dimension. When an unusually dense scale cannot fit every intermediate label, it retains the ticks, origin, units, and exact endpoints and omits only colliding intermediate labels.

## Manual send differences

`GET /api/telegram/send-top` is an operator action, not a replay of automatic snapshot processing:

- It evaluates the entire current snapshot and does not refresh FIRMS.
- It bypasses startup-incident and delivered-episode checks without modifying automatic state or future deduplication.
- It applies the same historical-FRP criterion and fails closed when the enabled baseline is incomplete; it neither waits for nor initiates history acquisition.
- It obtains each preview once; a required missing preview skips the candidate.
- It ranks eligible clusters by available/highest total FRP, then representative FRP, member count, diameter, acquisition time, and ID before selecting the requested count.
- It looks up mapped location context only for those selected representatives, after ranking, so unselected eligible clusters create no Overpass traffic.
- It serializes manual operations, sends an introductory status message, and continues after individual candidate-send failures.

The endpoint is unauthenticated and side-effecting. Its status contract is authoritative in [Program.cs](../../src/ThermalWatch.Api/Program.cs) and the result types at the end of [TelegramNotificationService.cs](../../src/ThermalWatch.Telegram/TelegramNotificationService.cs).
