# ThermalWatch agent guide

ThermalWatch is a deliberately small .NET 10 service that polls NASA FIRMS near-real-time thermal anomalies, publishes an immutable in-memory snapshot through an unauthenticated HTTP API and map viewer, and can send filtered anomaly clusters to one Telegram channel. A thermal anomaly is an observation, not proof of a wildfire or an ongoing event.

## Repository map

- `src/ThermalWatch.Api/` — sole executable host, polling service, application configuration, public anomaly/Telegram routes, and runtime composition.
- `src/ThermalWatch.Core/` — FIRMS/GIBS clients, immutable models and snapshots, geography, country boundaries, and notification candidate policy/lifecycle.
- `src/ThermalWatch.Telegram/` — Telegram validation, message construction, and delivery of prepared Core candidates.
- `src/ThermalWatch.Viewer/` — viewer routes, notification diagnostics, configuration, root-mounted static assets, and plain-JavaScript provider adapters.
- `tests/` — xUnit v3 tests for all four projects, Node viewer tests, and documentation drift checks.
- `docs/` — routed architecture, development, operations, domain, component, and decision documentation.
- `.agent/PLANS.md` — ExecPlan requirements and format.
- `.agents/skills/` — reusable repository-local Codex procedures.
- `.github/workflows/` — PR validation, publishing, container creation, and Renovate automation.
- `.env` — optional ignored, sourceable local exports for temporary, user-authorized live-service credentials; preserve it when it exists and never commit it.

## Commands

Requires the .NET 10 SDK. Running the service also requires a valid `FIRMS_MAP_KEY` already exported in the shell.

```bash
dotnet restore ThermalWatch.slnx
dotnet build ThermalWatch.slnx -c Release --no-restore --nologo
dotnet test ThermalWatch.slnx -c Release --no-build --nologo
dotnet format ThermalWatch.slnx --verify-no-changes --no-restore
```

```bash
FIRMS_COUNTRIES=UKR,RUS dotnet run --project src/ThermalWatch.Api/ThermalWatch.Api.csproj
```

Documentation-only validation:

```bash
dotnet test tests/ThermalWatch.Tests.csproj -c Release --nologo --filter FullyQualifiedName~DocumentationValidationTests
```

Viewer JavaScript validation:

```bash
node --check src/ThermalWatch.Viewer/wwwroot/map-support.js
node --check src/ThermalWatch.Viewer/wwwroot/app.js
node --test tests/viewer-map-support.test.js
```

See [development guidance](docs/development.md) for prerequisites, safe local setup, container fallback, debugging, and change-specific checks.

## Coding standards

- The repository-root `.editorconfig` is mandatory for every project.
- Fix analyzer and style errors; do not suppress them.
- Use named arguments for unclear string, numeric, Boolean, `null`, and `default` literal arguments.
- Preserve existing code style, comments, project structure, and unrelated code.
- Prefer the smallest coherent change.
- Before finishing, run the complete repository verification sequence in [development guidance](docs/development.md#restore-build-test-and-format).

## Engineering constraints

- Application-specific configuration uses exact uppercase environment names; do not add `appsettings` or commit credentials.
- When verification or tests need live-provider access, agents may source the ignored repository-root `.env`. The user supplies temporary testing credentials; never echo, log, commit, or reuse them outside the authorized task. Preserve an existing `.env` before, during, and after the task. The user rotates its values when testing ends.
- State is intentionally in memory. A restart rebuilds the FIRMS snapshot and resets Telegram deduplication, pending previews, and caches.
- `/api/anomalies` exposes all valid active FIRMS observations. Notification filters must never remove or annotate API items.
- FIRMS segments are isolated by country and source. A failed refresh retains the previous complete segment and marks it stale.
- Country ingestion is primary. Area fallback is enabled only for a verified country-feature outage and succeeds atomically across all required tiles.
- Notification land-cover unavailability fails open; an exact preview requirement fails closed after its retry window.
- The browser viewer consumes existing API contracts. Keep provider-specific map code behind its current adapter boundary and preserve common marker behavior.
- Keep dependency direction `Api -> Viewer -> Core`, `Api -> Telegram -> Core`, and `Api -> Core`; do not make Core depend on host, viewer, or Telegram concerns.
- Keep the frontend framework-free unless the repository deliberately adopts a frontend toolchain.
- Treat warnings as errors and preserve deterministic, nullable-enabled builds.

## Documentation routing

Read [docs/README.md](docs/README.md) before substantial work, then read only the documents it routes for the current task. Inspect the actual code before trusting potentially stale prose.

- Cross-component or HTTP-boundary work: [architecture](docs/architecture.md).
- Build, tests, formatting, or local debugging: [development](docs/development.md).
- Configuration, deployment, security, incidents, or recovery: [operations](docs/operations.md).
- Notification meaning and selection policy: [domain policy](docs/domain/notification-policy.md).
- FIRMS, Telegram, or viewer internals: the relevant document under `docs/components/`.
- Durable architectural choice: [ADR policy](docs/decisions/README.md).
- Complex feature, significant refactor, migration, risky change, or multi-component work: read `.agent/PLANS.md` and maintain an ExecPlan.

## Required workflow

Before changing code:

1. Read `docs/README.md` and the relevant routed documents.
2. Inspect source, tests, schemas or executable configuration that define the current behavior.
3. Check the worktree and preserve unrelated user changes.
4. Create an ExecPlan when `.agent/PLANS.md` requires one.

During the change:

1. Keep code, tests, the active ExecPlan, and affected durable documentation synchronized.
2. Use the `maintain-project-docs` skill for changes with documentation impact.
3. When code and documentation disagree, verify the behavior and correct the documentation.
4. Do not duplicate source code, generated schemas, or API definitions in prose; link to the authoritative file, type, endpoint definition, test, or generated artifact.
5. Never place secrets, tokens, credentials, personal data, transient logs, or speculative claims in documentation.

After the change:

1. Update affected documentation in the same task and update `docs/README.md` when routing changes.
2. Run the documented validation commands appropriate to the change.
3. For any web-viewer task, capture desktop and narrow screenshots of every affected provider/state, open them with image/vision inspection, and treat visible defects as failures. DOM and network assertions alone are insufficient.
4. Report which documentation was consulted and which was updated, created, removed, or intentionally left unchanged.

## Documentation boundaries

- Add an ADR only for a durable, significant choice with meaningful alternatives and consequences; never for a trivial implementation detail.
- Never rewrite a historical ADR to imply a different original decision. Supersede it with a new ADR.
- Add a repository-wide rule here only when it is broadly applicable or prevents a recurring mistake, not for one-off task details.
- Add a nested `AGENTS.md` only when a directory genuinely has different commands, conventions, or constraints.

## Definition of complete

A task is complete only when behavior and tests satisfy the request, affected documentation agrees with verified source truth, required validation passes, no secrets or unrelated changes were introduced, the ExecPlan is resolved when applicable, and the final report names documentation consulted and changed.
