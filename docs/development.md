# Development

> **Purpose:** Provide verified setup, build, test, formatting, debugging, and validation workflows.
> **Scope:** Local development and pull-request validation for server, tests, documentation, and static viewer assets.
> **Sources of truth:** [Environment setup](../.env), [build properties](../Directory.Build.props), [solution](../ThermalWatch.slnx), [test project](../tests/ThermalWatch.Tests.csproj), and [PR workflow](../.github/workflows/pr.yml).
> **Update when:** SDK requirements, commands, project layout, tests, formatting, static assets, or CI validation changes.

## Prerequisites

- .NET 10 SDK. The repository has no `global.json`; the target framework and C# version are set in [Directory.Build.props](../Directory.Build.props).
- A valid NASA FIRMS MAP_KEY and internet access to run the service against FIRMS.
- Node.js only when changing the plain JavaScript viewer and running its syntax check.
- Docker is optional and can supply the official .NET SDK when it is not installed locally.

Do not store credentials in files, shell history, plans, logs, or documentation. Export them through the local environment or deployment secret mechanism.

## Restore, build, test, and format

Run the complete validation sequence from the repository root:

```bash
dotnet restore ThermalWatch.slnx
dotnet build ThermalWatch.slnx -c Release --no-restore --nologo
dotnet test ThermalWatch.slnx -c Release --no-build --nologo
dotnet format ThermalWatch.slnx --verify-no-changes --no-restore
```

Builds are deterministic, nullable-enabled, and treat warnings as errors. `dotnet format` uses the SDK defaults because the repository has no separate formatter configuration.

The pull-request workflow runs this independently restorable command:

```bash
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

Obtain a real FIRMS key, then use the interactive setup to persist it outside the repository and apply all five prompted variables to the current shell:

```bash
source ./.env
dotnet run --project src/ThermalWatch.Api/ThermalWatch.Api.csproj
```

The tracked [`.env` setup script](../.env) must be sourced, not executed, because a child process cannot modify its parent shell. It is a shell script rather than a dotenv data file. It validates the FIRMS key, hides key/token input, supplies the documented country and channel defaults, and lets both optional keys remain empty. The service listens on `http://localhost:8080`. Telegram is disabled when its credentials are absent. See [operations](operations.md) for persistence, all options, and startup constraints.

Startup immediately calls FIRMS. Prefer the unit tests for repeatable development; there is no fake FIRMS server, API integration-test fixture, or offline application mode in the repository.

## Change-specific checks

| Change | Additional validation |
| --- | --- |
| Shell environment setup | `bash -n .env`, plus a source test using temporary `THERMALWATCH_ENV_FILE` and `THERMALWATCH_PROFILE_FILE` paths; never use real credentials in tests. |
| Viewer JavaScript | `node --check src/ThermalWatch.Api/wwwroot/app.js`, then browser-check loading, empty, error, marker-selection, and provider states as applicable. |
| Static assets or hosting | Run the static-asset publish and Kestrel smoke check below. |
| Environment parsing | Add option tests and verify missing, valid, boundary, and invalid values without printing secrets. |
| FIRMS, GIBS, or Telegram logic | Add focused unit tests with fake HTTP handlers or model fixtures; do not make tests call live services. |
| Documentation or agent workflow | Run the focused documentation test and validate any changed skill metadata. |
| Container or archive publishing | Follow the exact commands in the relevant [workflow](../.github/workflows/) and inspect publish contents before changing packaging assumptions. |

## Static-asset publish and smoke check

After the normal restore, publish the host and verify that the framework-dependent output contains the viewer entry point:

```bash
dotnet publish src/ThermalWatch.Api/ThermalWatch.Api.csproj -c Release --no-restore --nologo
test -f src/ThermalWatch.Api/bin/Release/net10.0/publish/wwwroot/index.html
```

To exercise static-file middleware, keep a valid `FIRMS_MAP_KEY` exported, start the published host, and stop it with Ctrl+C after the check:

```bash
FIRMS_COUNTRIES=UKR,RUS \
dotnet src/ThermalWatch.Api/bin/Release/net10.0/publish/ThermalWatch.Api.dll
```

From another shell:

```bash
curl --fail --silent --show-error --output /dev/null http://localhost:8080/
```

Starting the host calls live FIRMS immediately. Skip the Kestrel portion when a real key or bounded network access is unavailable; the publish-file assertion remains deterministic.

## Safe debugging workflow

1. Check `git status --short` and preserve unrelated worktree changes.
2. Read the routed component and domain documents, then inspect the source and tests they cite.
3. Keep external calls bounded and use non-production Telegram channels if delivery must be tested.
4. Remember that `GET /api/telegram/send-top` sends messages and that viewer Refresh only rereads the in-memory snapshot.
5. Run `git diff --check`, the change-specific checks, and the complete solution validation before completion.

Direct pushes to `main` run publishing workflows but do not explicitly rerun the test suite. Pull requests are the checked-in validation gate.
