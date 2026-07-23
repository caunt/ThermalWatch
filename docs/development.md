# Development

> **Purpose:** Provide verified setup, build, test, formatting, debugging, and validation workflows.
> **Scope:** Local development and pull-request validation for server, tests, documentation, and static viewer assets.
> **Sources of truth:** [Build properties](../Directory.Build.props), [solution](../ThermalWatch.slnx), [test project](../tests/ThermalWatch.Tests.csproj), and [PR workflow](../.github/workflows/pr.yml).
> **Update when:** SDK requirements, commands, project layout, tests, formatting, static assets, or CI validation changes.

## Prerequisites

- .NET 10 SDK. The repository has no `global.json`; the target framework and C# version are set in [Directory.Build.props](../Directory.Build.props).
- A valid NASA FIRMS MAP_KEY and internet access to run the service against FIRMS.
- Node.js 24 only when changing the plain JavaScript viewer and running its syntax checks and dependency-free unit tests.
- Docker is optional and can supply the official .NET SDK when it is not installed locally.

Do not store credentials in shell history, tracked files, plans, logs, or documentation. A user-authorized, ignored repository-root `.env` is the only local file exception for live development credentials.

## Restore, build, test, and format

Run the complete validation sequence from the repository root:

```bash
dotnet restore ThermalWatch.slnx
dotnet build ThermalWatch.slnx -c Release --no-restore --nologo
dotnet test ThermalWatch.slnx -c Release --no-build --nologo
dotnet format ThermalWatch.slnx --verify-no-changes --no-restore
```

Builds are deterministic, nullable-enabled, and treat warnings as errors. `dotnet format` uses the SDK defaults because the repository has no separate formatter configuration.

The pull-request workflow configures Node.js 24, validates the Viewer project's static assets, and then runs the independently restorable .NET test command:

```bash
node --check src/ThermalWatch.Viewer/wwwroot/map-support.js
node --check src/ThermalWatch.Viewer/wwwroot/app.js
node --test tests/viewer-map-support.test.js
dotnet test ThermalWatch.slnx -c Release --nologo
```

When the host has Docker but no SDK, a disposable Linux SDK container can run the same command:

```bash
docker run --rm \
  --volume "$PWD:/workspace" \
  --workdir /workspace \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet test ThermalWatch.slnx -c Release --nologo
```

This writes ignored `bin/` and `obj/` output into the working tree and may create files owned by the container user.

## Documentation validation

The focused drift check is:

```bash
dotnet test tests/ThermalWatch.Tests.csproj -c Release --nologo --filter FullyQualifiedName~DocumentationValidationTests
```

It validates local documentation links and routing, ADR structure and identifiers, documented command targets, the root agent-guide size, and obvious unfinished markers. The full solution test runs it automatically, including in pull requests.

The check deliberately does not validate external URLs, anchors, or semantic agreement between prose and code. Review those manually against the linked source of truth.

## Run locally

Export a real key or source an existing ignored local `.env`, then start the host:

```bash
source ./.env
env -u TELEGRAM_BOT_TOKEN -u TELEGRAM_CHANNEL_ID \
  dotnet run --project src/ThermalWatch.Api/ThermalWatch.Api.csproj
```

The service listens on `http://localhost:8080`. Telegram is disabled when its credentials are absent. See [operations](operations.md) for all options and startup constraints.

Startup immediately calls FIRMS. Prefer the unit tests for repeatable development; there is no fake FIRMS server, API integration-test fixture, or offline application mode in the repository.

### Temporary credentials for live validation

Whenever verification requires live-provider access, an agent may source an existing ignored repository-root `.env` in the same Bash session that runs the check:

```bash
source ./.env
```

The file contains quoted Bash `export` assignments supplied or authorized by the user. It must stay ignored, use owner-only permissions, and never be printed, logged, included in a diff, or committed. Preserve the file whenever it exists; do not delete it during cleanup. The user rotates its values after live testing. Do not use it for deployment or long-lived credentials.

Sourcing all variables can enable Telegram's hosted service. Viewer and read-only provider checks must remove `TELEGRAM_BOT_TOKEN` and `TELEGRAM_CHANNEL_ID` from the server child process as shown above. Never call `/api/telegram/send-top` during viewer validation.

## Change-specific checks

| Change | Additional validation |
| --- | --- |
| Ignored temporary environment file | `bash -n .env`, mode/ignore checks, and silent assertions that required variables are nonempty; never print supplied credentials. |
| Viewer JavaScript | Run both `node --check` commands and `node --test tests/viewer-map-support.test.js`, then complete the screenshot and vision workflow below. |
| Static assets or hosting | Run the static-asset publish and Kestrel smoke check below. |
| Environment parsing | Add option tests and verify missing, valid, boundary, and invalid values without printing secrets. |
| FIRMS, GIBS, or Telegram logic | Add focused unit tests with fake HTTP handlers or model fixtures; do not make tests call live services. |
| Documentation or agent workflow | Run the focused documentation test and validate any changed skill metadata. |
| Container or archive publishing | Follow the exact commands in the relevant [workflow](../.github/workflows/) and inspect publish contents before changing packaging assumptions. |

## Static-asset publish and smoke check

After the normal restore, publish the sole executable host and verify that the referenced Viewer project's root-mounted assets are in the same output:

```bash
dotnet publish src/ThermalWatch.Api/ThermalWatch.Api.csproj -c Release --no-restore --nologo
test -f src/ThermalWatch.Api/bin/Release/net10.0/publish/wwwroot/index.html
```

To exercise static-file middleware, keep a valid `FIRMS_MAP_KEY` exported, start the published host, and stop it with Ctrl+C after the check:

```bash
(
  cd src/ThermalWatch.Api/bin/Release/net10.0/publish
  FIRMS_COUNTRIES=UKR,RUS dotnet ThermalWatch.Api.dll
)
```

From another shell:

```bash
curl --fail --silent --show-error --output /dev/null http://localhost:8080/
```

Starting the host calls live FIRMS immediately. Skip the Kestrel portion when a real key or bounded network access is unavailable; the publish-file assertion remains deterministic.

## Live viewer screenshot verification

Every task that changes or diagnoses the web viewer requires browser screenshots and image/vision inspection. Successful HTTP requests, map tiles, console checks, DOM assertions, or marker counts do not prove that the rendered viewer is usable.

Use a clean disposable browser profile against the real local application. For provider work, wait until the live FIRMS snapshot is ready and capture at least:

- NASA and Google at a 1440×900 desktop viewport.
- NASA and Google at a 390×844 narrow viewport.
- The initial provider again after switching to the other provider and back.

Open every captured image rather than merely checking that the file exists. Verify current imagery has no opaque no-data swaths, controls and markers are visible, marker selection works, the desktop map and inspector remain usable within the footer boundary, and the stacked narrow layout has no horizontal overflow. Inspect any task-specific loading, empty, warning, or failure states too. For imagery-boundary work, observe requests without saving a key-bearing archive and verify that GIBS/FIRMS hosts never appear in browser traffic; NASA tiles must use `/api/viewer/imagery/gibs/...`. Save transient screenshots outside the repository, report their paths and visual findings, and do not save HAR files or browser logs that expose a Google key.

For a viewer-only run, source `.env` but suppress Telegram in the application process:

```bash
source ./.env
(
  cd src/ThermalWatch.Api/bin/Release/net10.0/publish
  env -u TELEGRAM_BOT_TOKEN -u TELEGRAM_CHANNEL_ID dotnet ThermalWatch.Api.dll
)
```

Stop the browser and server normally after inspection. Preserve `.env`.

## Safe debugging workflow

1. Check `git status --short` and preserve unrelated worktree changes.
2. Read the routed component and domain documents, then inspect the source and tests they cite.
3. Keep external calls bounded. Viewer checks must disable Telegram regardless of the configured channel.
4. Remember that `GET /api/telegram/send-top` sends messages and that viewer Refresh only rereads the in-memory snapshot.
5. Run `git diff --check`, the change-specific checks, and the complete solution validation before completion.

Direct pushes to `main` run publishing workflows but do not explicitly rerun the test suite. Pull requests are the checked-in validation gate.
