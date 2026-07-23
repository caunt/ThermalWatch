# 0001: Server-mediated viewer imagery

## Status

Accepted

## Context

The original framework-free viewer requested NASA GIBS JPEG tiles and composed corrected-reflectance coverage in browser canvas code. That made NASA a browser integration, duplicated product policy outside Core, and prevented deployments from guaranteeing that observed NASA traffic crossed the service boundary. The requested design requires NASA data and imagery to be owned by the backend while retaining one process, one image, one port, the existing optional Google provider, and provider-neutral map behavior.

The existing [dependency graph](../architecture.md) keeps provider/data logic in Core and hosting in Api. The viewer also needs a clearer code and static-asset boundary without becoming a separately deployed service.

## Decision drivers

- Browser code must never contact FIRMS or GIBS directly.
- GIBS product order, validation, compositing, cancellation, and cache behavior need one testable backend owner.
- Api must remain the only executable, listener, container entry point, and composition root.
- Existing anomaly, Telegram, Google Maps, marker, and refresh behavior must remain compatible.
- Image decoding must work in every existing Linux, Alpine, Windows, and macOS publish target without native runtime assets or a build-time license credential.

## Considered options

- Retain direct GIBS browser requests. This preserves server load but violates the required trust boundary and keeps imagery policy in JavaScript.
- Proxy individual GIBS JPEGs and continue composing in the browser. This hides the NASA host but still leaves imagery interpretation and provider policy in the client.
- Put tile retrieval/composition and static assets directly in Api. This meets the network rule but mixes provider logic and viewer ownership into the executable host.
- Add a Viewer library for routes/assets and a Core tile client, both hosted by the existing Api executable. This preserves deployment unity while enforcing project responsibilities.

## Decision

Add `ThermalWatch.Viewer` as a non-executable library referenced by `ThermalWatch.Api`, with root-mounted static assets and viewer-specific routes. Api remains the only runtime host.

Core owns a separate latest-map GIBS client. It validates, decodes, Terra-first composes, caches, and encodes tiles before the Viewer endpoint returns them with explicit coverage metadata. The browser consumes that same-origin endpoint and does not contain a FIRMS/GIBS request host.

Pinned Leaflet assets from unpkg and optional Google Maps JavaScript remain deliberate browser exceptions. Google configuration and browser-key restrictions are unchanged.

## Consequences

- NASA product and no-data policy is centralized in Core and can be validated without a browser.
- Uncached viewer navigation consumes server bandwidth, CPU, memory-cache capacity, and GIBS requests; concurrency, response bounds, early completion, and short complete-only caching constrain that cost.
- The public HTTP surface gains a read-only imagery endpoint and coverage/cache headers.
- The solution gains one library project but no service, process, port, image, configuration variable, persistence, or migration.
- Pure-managed public-domain Stb image codecs become Core dependencies; they avoid native deployment assets and the license-key build requirement observed with ImageSharp 4.
- Google and unpkg remain external browser availability/security boundaries and are not represented as same-origin.

## Validation or evidence

- [Core and endpoint tests](../../tests/GibsMapTileClientTests.cs) verify product order, validation, composition, coverage, caching, failure isolation, and cancellation.
- [Viewer endpoint tests](../../tests/ViewerImageryEndpointTests.cs) verify the PNG, coverage, cache, and coordinate-validation contract.
- [Browser support tests](../../tests/viewer-map-support.test.js) verify same-origin URLs, tile lifecycle, warning behavior, Google compatibility, and absence of direct FIRMS/GIBS request hosts.
- [Composition root](../../src/ThermalWatch.Api/Program.cs) and [solution](../../ThermalWatch.slnx) demonstrate the one-host project graph.

## Related source files and documents

- [Core map-tile client](../../src/ThermalWatch.Core/GibsMapTileClient.cs)
- [Viewer project and endpoints](../../src/ThermalWatch.Viewer/ViewerEndpoints.cs)
- [Architecture](../architecture.md)
- [Web viewer](../components/web-viewer.md)
- [Operations](../operations.md)

## Supersedes / Superseded by

- Supersedes: None.
- Superseded by: None.
