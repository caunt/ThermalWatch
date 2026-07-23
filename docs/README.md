# ThermalWatch documentation index

> **Purpose:** Route contributors and agents to the minimum documentation needed for a task.
> **Scope:** Durable project documentation, decision records, plans, and repository-local agent guidance.
> **Sources of truth:** [Solution structure](../ThermalWatch.slnx), [source projects](../src/), [tests](../tests/), and [workflows](../.github/workflows/).
> **Update when:** A document is added, removed, renamed, changes purpose, or gains a different authoritative source.

Start with this index, select only the relevant documents, and then inspect their linked source-of-truth files. Documentation summarizes intent and boundaries; code, tests, and executable configuration decide actual behavior.

## Project documents

| Document | Contains and read when | Authoritative sources | Update when |
| --- | --- | --- | --- |
| [Root README](../README.md) | Product overview, safety warning, quickstart, public entry points, and links. Read for initial orientation. | [API host](../src/ThermalWatch.Api/Program.cs), [models](../src/ThermalWatch.Core/Models.cs), and [configuration parsers](../src/ThermalWatch.Api/EnvironmentConfiguration.cs) | User-facing capabilities or entry points change. |
| [Architecture](architecture.md) | System boundaries, dependencies, data flow, state, HTTP surface, invariants, and failure isolation. Read for cross-component or integration work. | [Solution](../ThermalWatch.slnx), [composition root](../src/ThermalWatch.Api/Program.cs), [viewer endpoints](../src/ThermalWatch.Viewer/ViewerEndpoints.cs), and [models](../src/ThermalWatch.Core/Models.cs) | A project boundary, endpoint, external integration, state model, or cross-component flow changes. |
| [Development](development.md) | Prerequisites, commands, tests, formatting, debugging, CI, and safe local workflows. Read before building or validating changes. | [Build properties](../Directory.Build.props), [test project](../tests/ThermalWatch.Tests.csproj), and [PR workflow](../.github/workflows/pr.yml) | Toolchain, commands, test layout, formatting, or CI validation changes. |
| [Operations](operations.md) | Environment variables, startup, deployment, security boundaries, observability, recovery, and known operational limits. Read for configuration, deployment, or incident work. | [Application configuration](../src/ThermalWatch.Api/EnvironmentConfiguration.cs), [Telegram options](../src/ThermalWatch.Telegram/TelegramOptions.cs), and [publish workflows](../.github/workflows/) | Configuration, deployment, logging, security, or recovery behavior changes. |
| [Notification policy](domain/notification-policy.md) | Domain meaning, clustering, API-versus-notification rules, filters, previews, and manual-send differences. Read for anomaly interpretation or notification-selection changes. | [Models](../src/ThermalWatch.Core/Models.cs), [clustering](../src/ThermalWatch.Core/NotificationClustering.cs), [filters](../src/ThermalWatch.Telegram/) | Domain rules, selection order, thresholds, representative choice, or fail-open/fail-closed policy changes. |
| [FIRMS ingestion](components/firms-ingestion.md) | Polling, country/area acquisition, geometry fallback, parsing, segment publication, and staleness. Read for ingestion or snapshot work. | [FIRMS client](../src/ThermalWatch.Core/FirmsClient.cs), [poller](../src/ThermalWatch.Api/FirmsPollingService.cs), [snapshot store](../src/ThermalWatch.Core/AnomalySnapshotStore.cs) | FIRMS request, parsing, fallback, polling, segment, or snapshot behavior changes. |
| [Telegram notifier](components/telegram-notifier.md) | Validation, snapshot consumption, in-memory state, preview handling, sending, and disable/retry behavior. Read for Telegram delivery work. | [Notification service](../src/ThermalWatch.Telegram/TelegramNotificationService.cs), [GIBS client](../src/ThermalWatch.Core/GibsClient.cs) | Telegram lifecycle, state, GIBS usage, delivery, or error handling changes. |
| [Web viewer](components/web-viewer.md) | Viewer project boundary, static UI, API consumption, backend imagery, provider adapters, diagnostics, and browser failures. Read for viewer or map-provider work. | [Viewer controller](../src/ThermalWatch.Viewer/wwwroot/app.js), [viewer endpoints](../src/ThermalWatch.Viewer/ViewerEndpoints.cs), and [Core tile client](../src/ThermalWatch.Core/GibsMapTileClient.cs) | Viewer state, API consumption, imagery behavior, provider behavior, external browser dependencies, layout, or UI validation changes. |
| [Decision records](decisions/README.md) | ADR criteria, numbering, lifecycle, and registry. Read before recording or changing a significant architectural choice. | Accepted ADRs and their linked evidence | A durable decision is proposed, accepted, rejected, or superseded. |
| [ADR template](decisions/0000-template.md) | Required structure for new ADRs. Read only when creating an ADR. | [ADR policy](decisions/README.md) | Required ADR evidence or lifecycle fields change. |

## Agent working documents

| Document | Contains and read when | Authoritative sources | Update when |
| --- | --- | --- | --- |
| [Root agent guide](../AGENTS.md) | Repository-wide commands, invariants, routing, workflow, and completion criteria. Read at the start of a Codex task. | The linked project documents, source, tests, and workflows; this guide is authoritative only for agent workflow. | A broadly applicable rule or recurring failure-prevention instruction changes. |
| [ExecPlan standard](../.agent/PLANS.md) | Format and maintenance rules for complex or resumable implementation plans. Read when deciding whether a task needs an ExecPlan and while maintaining one. | The standard itself and the governing active plan. | Planning requirements or required plan sections change. |
| [Documentation maintenance skill](../.agents/skills/maintain-project-docs/SKILL.md) | Reusable procedure for classifying and applying documentation updates alongside code changes. Read when the skill's trigger description matches a task. | The skill workflow plus this index's current routing table. | Documentation-impact triggers or the maintenance workflow changes. |

Active ExecPlans belong under `.agent/plans/` and should be opened only for the task they govern. That directory is intentionally absent when no plan is active.
