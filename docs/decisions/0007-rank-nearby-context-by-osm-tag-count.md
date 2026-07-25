# 0007: Rank nearby context by OSM tag count

## Status

Accepted

## Context

Core supplies one bounded nearby-feature result set to prepared Telegram notifications and Viewer diagnostics. The previous decision ranked every valid result by distance, so a minimally described feature could displace a more fully tagged feature from Telegram's five-result subset solely because it was closer. Both surfaces still need the same result order, and nearby mapped context must remain informational rather than becoming a notification criterion or claim of causality.

## Decision drivers

- Prefer more fully described OpenStreetMap features in both user-facing surfaces.
- Keep Telegram and Viewer ordering and result limits consistent.
- Retain distance as useful context without making it the primary rank.
- Keep provider parsing, validation, limiting, and ranking in Core rather than duplicating policy in presentation adapters.
- Do not expand the public nearby-feature model with raw OSM tags or a new ranking field.

## Considered options

- Retain nearest-first ranking. This preserves the old behavior but lets sparsely tagged features outrank richer mapped context.
- Return unsorted results and rank independently in Telegram and Viewer. This duplicates policy and cannot recover features that Core already removed at a caller limit.
- Rank once in Core by OSM tag count, then use distance and stable identity fields for ties. This keeps selection and order identical across both surfaces without changing their data contract.

## Decision

Core continues to own on-demand nearby-feature retrieval, validation, failure isolation, caching, and limiting. After invalid and blacklisted elements are removed, it ranks features by the descending number of properties in each element's OSM `tags` object. Equal tag counts rank by ascending Haversine distance, then ordinal OSM type and numeric OSM ID. Core applies its 25-result cache bound after this ordering and then applies the caller's requested limit, so Telegram receives the five highest-ranked results and Viewer diagnostics can receive up to 25.

Distance remains part of every result and both surfaces continue to display it. Tag count is an ordering heuristic only: it is not returned in the nearby-feature contract, does not affect notification eligibility or cluster ranking, and does not make a mapped feature more likely to have caused an anomaly.

## Consequences

- A farther, more fully tagged feature can appear before a closer, sparsely tagged feature and can displace it from Telegram's five-result subset.
- Telegram and Viewer preserve one shared deterministic order without adding presentation-layer sorting.
- Ranking can change when OpenStreetMap contributors add or remove tags and the cached lookup is refreshed.
- The existing nearby-feature JSON shape, displayed distances, lookup radius, result limits, and failure behavior remain unchanged.

## Validation or evidence

- [Overpass client tests](../../tests/NearbyFeatureClientTests.cs) verify tag-count-first ordering, distance ties, filtering, and caller limits.
- [Telegram formatter tests](../../tests/TelegramMessageFormatterTests.cs) verify that prepared nearby results retain their supplied order and distance display.
- [Viewer endpoint tests](../../tests/ViewerNotificationDiagnosticEndpointTests.cs) verify that Core's nearby-feature sequence crosses the diagnostic HTTP boundary.

## Related source files and documents

- [Core nearby client](../../src/ThermalWatch.Core/NearbyFeatureClient.cs)
- [Notification policy](../domain/notification-policy.md)
- [Telegram notifier](../components/telegram-notifier.md)
- [Web viewer](../components/web-viewer.md)

## Supersedes / Superseded by

- Supersedes: [0003](0003-core-owned-on-demand-nearby-context.md)
- Superseded by: None.
