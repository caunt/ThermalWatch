# Repository-wide coding standards enforcement

## Purpose and observable outcome

Establish one enforced C# formatting, naming, analyzer, and modern-syntax policy across every project in `ThermalWatch.slnx`. The repository-root `.editorconfig`, `Directory.Build.props`, and `Directory.Packages.props` now provide the shared policy and private analyzer dependency; current source complies without broad suppressions or public-behavior changes; contributor documentation describes the durable workflow; and the complete repository validation sequence passes.

The work is limited to standards enforcement and the smallest code edits required by diagnostics. It does not change HTTP contracts, domain behavior, project boundaries, viewer JavaScript behavior, or public member names.

## Context and repository orientation

The solution contains the executable `src/ThermalWatch.Api`, three class-library projects under `src/`, and `tests/ThermalWatch.Tests.csproj`. `Directory.Build.props` already centralized .NET 10, C# 14, nullable analysis, implicit usings, warnings-as-errors, and deterministic compilation. `Directory.Packages.props` already enabled Central Package Management. No root `.editorconfig`, `global.json`, repository build script, or formatter wrapper existed before this work. Pull requests run the viewer checks and `dotnet test ThermalWatch.slnx -c Release --nologo` in `.github/workflows/pr.yml`.

The governing documentation is `AGENTS.md`, `docs/README.md`, `docs/development.md`, and `.agent/PLANS.md`. Source and tests under `src/` and `tests/` remain authoritative for behavior. Validation used SDK 10.0.110 with MSBuild 18.0.11 and C# 14 configured centrally.

## Progress

- [x] 2026-07-23T14:04:01Z Read repository guidance, documentation routing, development workflow, the documentation-maintenance skill, and the ExecPlan standard.
- [x] 2026-07-23T14:04:01Z Confirmed a clean starting worktree and inventoried the solution, projects, central build/package files, workflows, scripts, SDK, and C# source set.
- [x] 2026-07-23T14:08:39Z Established a clean baseline and checked every requested option against the installed SDK and Meziantou analyzer configuration.
- [x] 2026-07-23T14:08:39Z Merged the root editor, build, package, agent, and development-documentation policy.
- [x] 2026-07-23T14:42:00Z Applied safe automated fixes and remediated all remaining diagnostics without suppressions or behavior changes.
- [x] 2026-07-23T15:01:02Z Passed focused and complete validation, reviewed the full diff, reconciled durable documentation, and closed the plan for commit and push to `main`.

## Surprises and discoveries

- The repository had no `.editorconfig`; formatting previously used SDK defaults.
- Existing central configuration already satisfied nullable, warnings-as-errors, and deterministic-build requirements, so those properties remained single definitions.
- Central Package Management allowed the analyzer version to live in `Directory.Packages.props` while one private repository-wide `PackageReference` lives in `Directory.Build.props`.
- The requested `dotnet_code_quality_unused_parameters = all:error` form is unsupported. SDK 10.0.110 accepts `all` for scope and IDE0060 for severity, so the equivalent valid configuration is `dotnet_code_quality_unused_parameters = all` plus `dotnet_diagnostic.IDE0060.severity = error`.
- All other requested option names and non-Boolean values are exposed by the installed Roslyn/dotnet-format assemblies. Meziantou 3.0.125 recognizes the requested MA0003 option names and expression-kind values.
- Enabling recommended analysis and Meziantou defaults required type-per-file splits, source-generated logging calls, bounded regex timeouts, small method extractions, and disposal of owned semaphore instances. These are mechanical compliance changes; public contracts and runtime decisions remain unchanged.
- Sequential automated fixes could introduce ordinal string-comparison calls after MA0003 had run, leaving poor framework argument labels and malformed continuation indentation. Literal receivers or named constants preserve ordinal semantics without ambiguous names, and the final formatter verification reports zero changes.

## Decision log

- Decision: Use an ExecPlan. Reason: analyzer enforcement affects all five projects and requires cross-component mechanical remediation. Date: 2026-07-23. Consequence: progress, discoveries, validation, and recovery are recorded here.
- Decision: Do not add an ADR. Reason: this is repository tooling and contributor policy, not a runtime architectural choice with meaningful product alternatives. Date: 2026-07-23. Consequence: durable workflow facts live in `AGENTS.md` and `docs/development.md`.
- Decision: Split unused-parameter scope from diagnostic severity. Reason: `dotnet_code_quality_unused_parameters` accepts `all` or `non_public`, not a `value:severity` suffix; IDE0060 carries severity. Date: 2026-07-23. Consequence: all parameters are analyzed at error severity without an ignored option value.
- Decision: Keep Meziantou centralized and private. Reason: the repository already uses Central Package Management and one build-props reference reaches every project. Date: 2026-07-23. Consequence: no per-project package duplication or annotations dependency was added.

## Concrete implementation steps

1. Inspect repository configuration, source conventions, SDK support, and the clean baseline.
2. Add the mandatory root `.editorconfig` and merge shared build/package enforcement without duplicating existing central properties.
3. Update `AGENTS.md`, `docs/development.md`, and documentation routing/source links with durable contributor workflow facts.
4. Restore dependencies, run `dotnet format`, and remediate diagnostics with named arguments, explicit types/accessibility, immutable state, source-generated logging, type-per-file splits, and narrow method extractions.
5. Preserve behavior while reviewing public contracts, async/disposal changes, argument names, generated logging templates, and documentation links.
6. Run complete .NET, documentation, Viewer JavaScript, formatting, and diff validation; commit and push the reviewed result to `main`.

## Validation and acceptance criteria

Executed from the repository root:

```bash
dotnet restore ThermalWatch.slnx
dotnet format ThermalWatch.slnx --no-restore
dotnet build ThermalWatch.slnx -c Release --no-restore --nologo
dotnet test ThermalWatch.slnx -c Release --no-build --nologo
dotnet format ThermalWatch.slnx --verify-no-changes --no-restore
dotnet test tests/ThermalWatch.Tests.csproj -c Release --no-build --nologo --filter FullyQualifiedName~DocumentationValidationTests
node --check src/ThermalWatch.Viewer/wwwroot/map-support.js
node --check src/ThermalWatch.Viewer/wwwroot/app.js
node --test tests/viewer-map-support.test.js
git diff --check
```

Observed results: restore was current; Release build succeeded with zero warnings and zero errors; all 81 .NET tests passed; all 7 focused documentation tests passed; formatter verification required zero changes across 94 files; both Node syntax checks passed; all 14 Viewer tests passed; and `git diff --check` reported no errors. No live services, credentials, or screenshots were needed because runtime and rendered Viewer behavior did not change.

## Recovery or rollback guidance

All changes are tracked text files and package restore output remains in ignored `obj/` directories. Automated formatter output was reviewed before manual remediation. Re-run the validation commands idempotently after any edit. Reverse only specifically identified task edits with `apply_patch`; never reset unrelated work. No secrets or live-provider calls are involved, `.env` remains untouched, and there are no irreversible operations.

## Outcomes and retrospective

Every project now shares deterministic formatting, naming, named-literal arguments, unused-code detection, explicit accessibility, immutability, controlled `var`, multiline braces, static noncapturing functions, folder-matched file-scoped namespaces, and the requested modern C# preferences. Recommended SDK analysis and Meziantou.Analyzer 3.0.125 are enforced centrally with warnings and style diagnostics treated as errors.

Current code, tests, and durable contributor documentation comply without diagnostic suppressions, broad `NoWarn`, generated-code edits, narrow exclusions, annotations, public API renames, or remaining validation issues. The principal lesson is to run analyzer and code-style fixers to a fixed point, then inspect named-argument trivia and framework parameter names manually before accepting the diff.
