# De-jure area-fallback country boundaries

## Purpose and observable outcome

ThermalWatch currently clips FIRMS area responses with Natural Earth's default de-facto country polygons. Replace that embedded asset with Natural Earth 5.1.2's Ukraine point-of-view country polygons so area-fallback observations use that viewpoint consistently across disputed territories. In particular, a Crimean coordinate must belong to the `UKR` segment and not the `RUS` segment.

The NASA country-first acquisition and hourly recovery probe remain unchanged. A successful country request continues to use NASA's country membership, so the new attribution guarantee applies only to segment results whose ingestion mode is `areaFallback`. Public JSON shapes and environment variables do not change.

## Context and repository orientation

[CountryBoundaryCatalog.cs](../../src/ThermalWatch.Core/CountryBoundaryCatalog.cs) loads the compressed embedded GeoJSON named by `ResourceName`. [FirmsClient.cs](../../src/ThermalWatch.Core/FirmsClient.cs) derives one area envelope from the requested country's geometry and retains only CSV observations covered by that geometry. [FirmsClientTests.cs](../../tests/FirmsClientTests.cs) exercises country capability and area clipping; new focused boundary tests will lock the chosen worldview directly.

Durable behavior is described in the root [README](../../README.md), [architecture](../../docs/architecture.md), [FIRMS ingestion](../../docs/components/firms-ingestion.md), [operations](../../docs/operations.md), and ADR 0011. The repository's [development guidance](../../docs/development.md) defines the complete validation sequence.

The replacement source is `ne_10m_admin_0_countries_ukr.geojson` from Natural Earth tag `v5.1.2`, commit `f1890d9f152c896d250a77557a5751a93d494776`. Its expected uncompressed SHA-256 is `28a1a17d7327cad576083d5122b867843f957468b1bb11168ded23fd5aea342e`.

## Progress

- [x] 2026-08-09T14:13:58Z Confirmed the current country-first/fallback data flow, the embedded default worldview, the selected global Ukraine worldview, and the fallback-only scope.
- [x] 2026-08-09T14:13:58Z Created this ExecPlan and proposed ADR 0011 before implementation.
- [x] 2026-08-09T14:17:18Z Replaced and wired the verified embedded boundary asset; its decompressed content matches the pinned SHA-256.
- [x] 2026-08-09T14:17:18Z Added direct geometry and end-to-end fallback regression coverage; all 19 focused tests pass.
- [x] 2026-08-09T14:18:39Z Synchronized the README, architecture, FIRMS ingestion, operations, data provenance, ADR, and ADR registry; seven focused documentation checks pass.
- [x] 2026-08-09T14:19:32Z Completed focused and repository-wide validation, accepted ADR 0011, and recorded outcomes.

## Surprises and discoveries

- Natural Earth does not publish a worldview-neutral universal de-jure polygon set. Its ISO point-of-view excludes some disputed claim polygons rather than assigning them. The selected Ukraine point-of-view is therefore named explicitly instead of being described as universal legal truth.
- Country code is part of deterministic anomaly identity. Reassigning an area-fallback observation changes its anomaly ID and any cluster ID containing it, but the service has no persistent data or migration boundary.
- The 1:10m point-of-view asset is larger than the current 1:50m default asset. It remains small enough to embed and avoids adding runtime boundary retrieval.

## Decision log

- 2026-08-09: Use Natural Earth's Ukraine point-of-view globally for area fallback. It is a versioned, public-domain, internally consistent polygon set that assigns Crimea to Ukraine without project-owned geopolitical overlays.
- 2026-08-09: Retain the country-first capability state machine. If NASA restores the country endpoint, its assignments may differ; segment ingestion mode remains the public way to distinguish attribution sources.
- 2026-08-09: Record the durable source and scope in ADR 0011 because the choice affects public country codes, identity, filtering, viewer labels, and Telegram labels.

## Concrete implementation steps

1. Download the pinned upstream GeoJSON into a temporary directory, verify its uncompressed SHA-256, compress it deterministically, and replace the tracked default 1:50m resource with a viewpoint-explicit 1:10m resource. Update the Core project embedded-resource item, catalog resource name, and Natural Earth notice.
2. Add boundary-catalog tests for representative Ukraine-worldview assignments and FIRMS client tests proving Crimea enters `UKR` and is excluded from `RUS` during area fallback. Preserve a country-mode test proving provider membership is unchanged.
3. Update current-behavior documentation and ADR 0011. Explain the acquisition-mode boundary clearly and avoid claiming the selected worldview is universal law.
4. Run focused tests and data-integrity checks, then the complete repository sequence. Review the final diff for unrelated changes and secrets before accepting the ADR and completing this plan.

## Validation and acceptance criteria

- `gzip -dc src/ThermalWatch.Core/Data/ne_10m_admin_0_countries_ukr.geojson.gz | sha256sum` prints the pinned hash.
- Focused boundary and FIRMS tests pass and prove Crimean coordinates classify as `UKR` only in area fallback; representative global worldview coordinates remain pinned; country mode remains provider-defined.
- `dotnet test tests/ThermalWatch.Tests.csproj -c Release --nologo --filter FullyQualifiedName~DocumentationValidationTests` passes.
- `dotnet restore ThermalWatch.slnx`, the warning-free Release build, full Release test suite, and format verification all pass.
- `git diff --check` passes and the diff contains no credentials or unrelated changes.
- A live smoke is optional and only uses an existing user-authorized `.env`, with Telegram variables removed. It may confirm `areaFallback` segment publication but does not require a current Crimean detection.

## Recovery or rollback guidance

All work is repository-local and retryable. Keep the prior tracked resource recoverable through Git while replacing it, never touch `.env`, and do not reset unrelated worktree changes. If the new asset or tests fail, restore only the exact resource wiring and files changed by this plan from their Git versions, or correct the deterministic asset generation and rerun validation. There are no database migrations, external writes, or irreversible operations.

## Outcomes and retrospective

ThermalWatch now embeds the pinned Natural Earth 5.1.2 Ukraine point-of-view country geometry. Direct catalog tests pin Crimea to Ukraine and exclude it from Russia while checking representative global disputes; FIRMS client tests prove the same behavior through area clipping and preserve provider membership in country mode. The old default 1:50m asset was removed and the new 1:10m asset's decompressed SHA-256 matches its pinned source.

The focused boundary/FIRMS run passed 19 tests, documentation validation passed seven tests, the complete Release suite passed 329 tests, the Release build completed with zero warnings and errors, format verification and `git diff --check` passed, and the complete diff contains no credentials or unrelated changes. A live smoke was not necessary for deterministic acceptance and no separately authorized credential use occurred.

Durable behavior and rationale now live in the README, architecture, FIRMS ingestion, operations, data provenance notice, and accepted ADR 0011. Documentation routing, Viewer internals, Telegram internals, notification policy, development workflow, and agent guidance remain unchanged because their contracts and procedures did not change. The remaining intentional limitation is mode-dependent attribution: a restored NASA country endpoint can return membership that differs from the area-fallback worldview.
