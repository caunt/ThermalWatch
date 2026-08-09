# 0011: Use the Ukraine worldview for area fallback

## Status

Accepted

## Context

ThermalWatch prefers NASA FIRMS country CSV responses but uses locally clipped area responses during a verified country-feature outage. The clipping geometry embedded by [Core](../../src/ThermalWatch.Core/CountryBoundaryCatalog.cs) previously came from Natural Earth's default Admin 0 countries theme, whose editorial policy represents de-facto control. That geometry assigns Crimea to Russia, so area-fallback `countryCode` values, deterministic anomaly IDs, API filtering, viewer labels, clusters, and Telegram country labels inherit that attribution.

There is no universally neutral de-jure polygon dataset. Natural Earth supplies named point-of-view variants; its ISO variant leaves some disputed claim areas unassigned. The project needs one deterministic, versioned polygon set that assigns every covered fallback coordinate to the intended configured country without runtime boundary retrieval.

NASA country membership is a separate upstream contract. This decision does not reinterpret a successful country response or remove the existing country-capability probe.

## Decision drivers

- Assign Crimea and other disputed territories according to a legal-country worldview during area fallback.
- Keep fallback clipping deterministic, local, and independent of a runtime boundary service.
- Name the selected political viewpoint honestly rather than presenting it as universal law.
- Preserve the current FIRMS country-capability and segment-failure behavior.
- Use a redistributable, versioned source with sufficient geometry detail for point clipping.

## Considered options

- Retain Natural Earth's default de-facto countries. This preserves current behavior but continues assigning Crimea to Russia during area fallback.
- Use Natural Earth's ISO point-of-view. This has an institutional name but leaves some disputed claim areas, including Crimea, outside every country polygon.
- Combine UN OCHA or geoBoundaries sources. Coverage and source authority vary by country, licensing requires additional attribution, and independently sourced polygons can overlap or leave gaps.
- Maintain a project-owned mapping of disputed polygons based on UN resolutions. This can express a bespoke policy but creates an ongoing geopolitical research and geometry-maintenance burden.
- Use Natural Earth's Ukraine point-of-view countries. This provides one public-domain, versioned global worldview that assigns Crimea to Ukraine and can directly replace the current embedded source.

## Decision

Embed Natural Earth 5.1.2's 1:10m Ukraine point-of-view Admin 0 countries geometry and use it for all area-fallback envelopes and point clipping. Pin its upstream version and source hash in the repository's data provenance.

Keep NASA country-first acquisition, capability probing, and restoration behavior unchanged. The Ukraine-worldview assignment is guaranteed only for `areaFallback`; `country` mode continues to trust NASA's membership. No compatibility alias or new configuration switch is introduced.

## Consequences

- Crimean area-fallback observations belong to `UKR` and not `RUS`. Other disputes follow Natural Earth's Ukraine worldview.
- Public schemas remain unchanged, but fallback country values, country-filter results, country labels, anomaly IDs, and derived cluster IDs can change.
- A later successful NASA country probe can restore provider-defined assignments that differ from area fallback. Consumers can distinguish the paths through segment ingestion mode.
- The embedded assembly grows because Natural Earth publishes point-of-view country polygons at 1:10m rather than 1:50m.
- The dataset remains a generalized cartographic source and must be deliberately reviewed before any future version or worldview update.

## Validation or evidence

[Country boundary tests](../../tests/CountryBoundaryCatalogTests.cs) pin representative worldview coordinates. [FIRMS client tests](../../tests/FirmsClientTests.cs) prove end-to-end fallback clipping and unchanged country-mode behavior. The embedded notice records source provenance and the repository verification suite validates the resulting build and public documentation.

## Related source files and documents

- [Country boundary catalog](../../src/ThermalWatch.Core/CountryBoundaryCatalog.cs)
- [FIRMS client](../../src/ThermalWatch.Core/FirmsClient.cs)
- [FIRMS ingestion](../components/firms-ingestion.md)
- [Architecture](../architecture.md)

## Supersedes / Superseded by

- Supersedes: None.
- Superseded by: None.
