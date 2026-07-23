# Notification policy

> **Purpose:** Define the non-obvious domain rules that distinguish raw thermal observations from Telegram notification candidates.
> **Scope:** Anomaly meaning and identity, clustering, automatic selection, visibility and land-cover filters, imagery, and manual sends.
> **Sources of truth:** [Anomaly model](../../src/ThermalWatch.Core/Anomaly.cs), [notification cluster](../../src/ThermalWatch.Core/NotificationCluster.cs), [generic clustering](../../src/ThermalWatch.Core/NotificationClustering.cs), [notification service](../../src/ThermalWatch.Telegram/TelegramNotificationService.cs), [automatic state](../../src/ThermalWatch.Telegram/TelegramAutomaticNotificationState.cs), [visibility filter](../../src/ThermalWatch.Telegram/TelegramVisibilityFilter.cs), and [land-cover filter](../../src/ThermalWatch.Telegram/TelegramLandCoverFilter.cs).
> **Update when:** Observation identity, clustering, representative choice, eligibility, filtering order, imagery policy, or manual-send semantics change.

## Observation meaning and API boundary

A FIRMS thermal anomaly is a satellite observation of heat. It may indicate wildfire, industrial heat, gas flaring, agricultural burning, an explosion, or another hot surface. Near-real-time acquisition is not continuous monitoring, and a recent observation does not prove the source remains active.

The HTTP API is the raw-observation boundary:

- It returns every valid FIRMS observation in the active snapshot, across MODIS and all three VIIRS feeds.
- It may apply only caller-requested query filters from [AnomalyQuery.cs](../../src/ThermalWatch.Api/AnomalyQuery.cs).
- Telegram visibility, land-cover, preview, deduplication, and clustering state never remove or annotate API items.

An anomaly ID is a deterministic truncated SHA-256 hash of country, source, satellite, UTC acquisition second, latitude, and longitude. Thermal contrast is primary brightness minus secondary/background brightness only when both values exist. [AnomalyId.cs](../../src/ThermalWatch.Core/AnomalyId.cs) and [Anomaly.cs](../../src/ThermalWatch.Core/Anomaly.cs) define these contracts.

## Clustering and representative selection

Notification clustering forms connected components. Two detections are linked when both their acquisition-time separation and Haversine distance meet the configured limits. Linkage is transitive, so a cluster's greatest pairwise diameter may exceed its link radius.

Clusters can cross configured countries, FIRMS sources, and satellites. Members are sorted newest first, then by ID. The representative is selected by:

1. Highest available FRP; missing FRP ranks below every available value.
2. Newest acquisition time.
3. Lexically smallest anomaly ID.

The cluster ID is a deterministic hash of its sorted member IDs. Preview sensor/date, filter metadata, map link, and much of the message are based on the representative, while multi-satellite and detection-count facts use all members. Because adding a member changes that hash, automatic delivery does not use cluster ID alone as the identity of an ongoing episode.

After an automatic message sends successfully, its members establish a delivered episode. A later cluster continues that episode when any new member is linked to a delivered member by the same radius and acquisition-time rule. Suppressed members extend the history, so continuity is transitive across snapshots: A linked to B and B linked to C remains one episode even if A is not linked directly to C. A cluster outside both limits can establish a new episode. The first successful message is not edited when later detections extend it.

## Automatic notification lifecycle

On each ready snapshot:

1. Expire old seen IDs and delivered-episode history. The first ready snapshot is marked seen without notification unless `TELEGRAM_NOTIFY_EXISTING_ON_STARTUP` is enabled.
2. Identify observations not already seen and mark their IDs seen before filtering or sending. A rejected or failed candidate is therefore not treated as new again merely because it remains active.
3. Build clusters containing at least one new observation. When the visibility filter requires multiple detections, related active observations from prior snapshots can provide clustering context and can remain the representative.
4. Merge a cluster with any related pending candidate, preserving the earliest preview retry start. If the merged cluster continues an already delivered episode, suppress it and extend that episode without rerunning filters or imagery work.
5. Apply representative metadata visibility rules to the current merged cluster.
6. If enabled, evaluate NASA land cover for every merged cluster member and its configured built-up proximity.
7. Queue accepted clusters pending an exact-date preview.
8. Send when imagery is available, or apply the configured preview-timeout policy. Only successful automatic delivery establishes a delivered episode; rejection, preview timeout, and send failure do not.

Seen IDs, delivered-episode detections, and pending candidates are process memory only. Seen and delivered history use the configured seen retention and are each capped at 100,000 entries.

## Visibility policy

When enabled, [TelegramVisibilityFilter.cs](../../src/ThermalWatch.Telegram/TelegramVisibilityFilter.cs) evaluates in this order:

1. Required daytime pass.
2. Minimum cluster member count.
3. Source-specific representative confidence: MODIS numeric percentage or ordered VIIRS low/nominal/high category.
4. Minimum representative FRP when the configured threshold is greater than zero.
5. Minimum representative thermal contrast when the configured threshold is greater than zero.

A required value that is absent rejects the candidate. Exact defaults and ranges live in [operations](../operations.md); tests should express policy edge cases rather than prose duplicating implementation branches.

## Land-cover policy

The land-cover filter uses NASA's annual combined MODIS IGBP product. The GIBS client selects the newest year common to every required tile, samples the detection pixels plus pixels intersecting the configured proximity, and decodes the official indexed colors into classes.

- IGBP classes 1–12 and 14 count as vegetation.
- Class 13 means urban/built-up and retains an otherwise vegetation-dominated cluster when present within proximity.
- Vegetation at or above the configured percentage is suppressed when no nearby class 13 exists, unless an explicitly enabled high-FRP or multi-satellite vegetation exception applies.
- Missing FRP does not bypass vegetation suppression.
- Unavailable, inconsistent, or invalid NASA land-cover data fails open: retain the candidate and report the unavailable reason.

These rules are heuristics, not event classification. Land cover, confidence, FRP, and imagery cannot prove whether a wildfire or visible smoke exists.

## Preview policy

[GibsClient.cs](../../src/ThermalWatch.Core/GibsClient.cs) maps the representative source, satellite, and day/night pass to a representative thermal-anomaly overlay and pass-matched contextual base layers. Daytime uses true color; nighttime uses the corresponding brightness-temperature products.

The overlay and selected base must advertise the exact acquisition date. The client probes the requested crop rather than treating global date availability as proof that spatial pixels are ready. It prefers the representative satellite's base, then tries other supported satellites in the same sensor family, followed by the other family. A fallback changes only the contextual base: the representative thermal overlay remains authoritative, and the Telegram caption names both sources. It never chooses the nearest date or a different pass. Imagery represents a date, not the exact acquisition minute.

Black, transparent, malformed, or mostly no-data base crops are unavailable and are not cached, so a later snapshot can retry transient GIBS ingestion gaps. Automatic candidates wait until a preview becomes available or the retry window expires. With the visibility filter and preview requirement enabled, expiry discards the candidate. Otherwise it may send as text. Crop selection uses the large dimensions when detection count, representative FRP, or cluster diameter meets its configured large-cluster threshold.

## Manual send differences

`GET /api/telegram/send-top` is an operator action, not a replay of the automatic queue:

- It evaluates the entire current snapshot as newly eligible input and does not refresh FIRMS.
- It bypasses seen-ID and delivered-episode checks without modifying seen IDs, delivered episodes, pending previews, or future automatic deduplication.
- It obtains each preview once; a required missing preview skips the candidate without an automatic retry window.
- It ranks eligible clusters by available/highest representative FRP, member count, diameter, acquisition time, and ID, then selects the requested count.
- It serializes manual operations, sends an introductory status message, and continues after individual candidate-send failures.

The endpoint is unauthenticated and side-effecting. Its status contract is authoritative in [Program.cs](../../src/ThermalWatch.Api/Program.cs) and the result types at the end of [TelegramNotificationService.cs](../../src/ThermalWatch.Telegram/TelegramNotificationService.cs).
