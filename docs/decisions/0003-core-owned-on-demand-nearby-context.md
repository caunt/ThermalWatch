# 0003: Core-owned on-demand nearby context

## Status

Accepted

## Context

Telegram recipients and Viewer users need nearby mapped context for a thermal anomaly without treating proximity as evidence of cause. Both surfaces consume Core notification candidates or diagnostics, while `/api/anomalies` must remain the complete raw FIRMS observation boundary. The chosen public Overpass endpoint is capacity-limited and must not receive one request for every active observation on every polling cycle.

## Decision drivers

- Telegram and Viewer need the same bounded, distance-ordered result contract.
- Nearby context must not affect notification eligibility, ranking, deduplication, or raw observations.
- Calls to a free external provider must be demand-driven, serialized, cached, and isolated from core service availability.
- Viewer-selected observations and cluster representatives are distinct locations and must retain their existing meanings.
- A nearby mapped feature is not evidence that it caused heat or fire.

## Considered options

- Enrich every snapshot observation during FIRMS polling. This would fan out provider traffic, delay publication, and annotate the raw API boundary.
- Query Overpass directly from Telegram and the browser. This would duplicate selection, validation, distance, and failure behavior while exposing another browser dependency.
- Put one on-demand client and neutral result contract in Core. Telegram can consume prepared representative context and Viewer diagnostics can consume selected-observation context without either surface owning provider policy.

## Decision

Core owns nearby-feature retrieval and its immutable result model. It queries named OpenStreetMap nodes, ways, and relations through `overpass-api.de` only when an automatic candidate is ready for delivery, after manual candidates are ranked and selected, or when a Viewer diagnostic evaluates a selected anomaly.

Automatic and manual notifications query the cluster representative; Viewer diagnostics query the selected observation. Core calculates and orders representative-point distances, limits output to five, and caches lookup outcomes. Results are informational only and never enter candidate policy or `/api/anomalies`.

Provider requests are globally serialized and bounded. HTTP, transport, timeout, size, and response failures produce an empty result plus a structured Warning; caller cancellation still propagates. User-facing surfaces omit an empty section and describe nonempty results as possible nearby sources with an explicit non-causality warning.

## Consequences

- Core gains another outbound provider client and the Viewer diagnostic contract gains `nearbyFeatures`.
- A selected diagnostic or ready notification can wait for one bounded Overpass request, but provider failure cannot fail the diagnostic or delivery.
- Successful and failed lookups use short-lived in-memory caching; restart clears them with other runtime caches.
- Ways and relations use Overpass-provided centers, so displayed distance is not distance to the nearest geometry edge.
- Direct `/api/anomalies` requests and FIRMS polling never call Overpass.

## Validation or evidence

- [Overpass client tests](../../tests/NearbyFeatureClientTests.cs) cover querying, validation, ordering, limits, caching, concurrency, cancellation, and warning-only failures.
- [Candidate engine tests](../../tests/NotificationCandidateEngineTests.cs) verify selected-observation and representative lookup points plus post-ranking manual enrichment.
- [Viewer endpoint tests](../../tests/ViewerNotificationDiagnosticEndpointTests.cs) verify the public diagnostic result.
- [Telegram formatter tests](../../tests/TelegramMessageFormatterTests.cs) verify bounded conditional presentation and causal wording.

## Related source files and documents

- [Core nearby client](../../src/ThermalWatch.Core/NearbyFeatureClient.cs)
- [Core candidate engine](../../src/ThermalWatch.Core/NotificationCandidateEngine.cs)
- [Architecture](../architecture.md)
- [Notification policy](../domain/notification-policy.md)
- [Telegram notifier](../components/telegram-notifier.md)
- [Web viewer](../components/web-viewer.md)

## Supersedes / Superseded by

- Supersedes: None.
- Superseded by: None.
