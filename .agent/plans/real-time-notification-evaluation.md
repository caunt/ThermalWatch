# Real-time notification evaluation

## Purpose and observable outcome

ThermalWatch currently evaluates only clusters containing newly seen detections and retains accepted clusters in an in-memory pending list while exact-date GIBS imagery is unavailable. Replace that lifecycle with direct evaluation of every active-snapshot cluster on every published FIRMS snapshot. An unavailable required preview rejects the cluster for the current cycle, and the next snapshot evaluates the live cluster again. No unsent notification candidate or preview deadline remains in memory.

Successful automatic deliveries must still establish an in-memory delivered episode so the five-minute snapshot loop does not resend the same ongoing episode. `TELEGRAM_NOTIFY_EXISTING_ON_STARTUP` remains supported: when false, clusters made entirely from the first ready snapshot stay suppressed until they gain a later detection. The anomaly API, clustering rules, manual sends, GIBS overlay/base selection, Telegram message format, and polling interval remain unchanged.

## Context and repository orientation

- `src/ThermalWatch.Core/NotificationCandidateEngine.cs` owns automatic, manual, and Viewer diagnostic candidate preparation.
- `src/ThermalWatch.Core/NotificationAutomaticState.cs` currently combines delivered-episode history with a pending-candidate list; it will become delivered-history-only state.
- `src/ThermalWatch.Api/ApplicationConfiguration.cs` maps legacy `TELEGRAM_*` environment variables into neutral Core options.
- `src/ThermalWatch.Telegram/TelegramNotificationService.cs` consumes every published snapshot and maps delivery success, transient failure, and permanent failure back to Core.
- Durable behavior is routed through `docs/domain/notification-policy.md`, `docs/components/telegram-notifier.md`, `docs/operations.md`, and `docs/architecture.md`.

The worktree was clean at task start. The repository requires .NET 10 and the complete validation sequence in `docs/development.md`. No live Telegram send is required or authorized for validation.

## Progress

- [x] 2026-07-24T06:41:19Z — Confirmed the clean worktree, current pending/seen implementation, routed documentation, tests, and configuration contracts.
- [x] 2026-07-24T06:49:00Z — Refactored automatic evaluation and delivered-episode state; removed pending and seen-ID machinery.
- [x] 2026-07-24T06:49:00Z — Updated configuration, summaries, logging, and focused tests; Release build and 31 focused tests pass.
- [x] 2026-07-24T07:18:37Z — Superseded ADR 0002 with ADR 0004 and synchronized architecture, operations, domain, component, root, and agent documentation.
- [x] 2026-07-24T07:18:37Z — Completed the full repository validation sequence, documentation and Viewer checks, live Telegram-disabled target verification, and desktop/narrow image inspection.

## Surprises and discoveries

- The pending list is not required by clustering. It exists because all new detection IDs are marked seen before filtering, so an unchanged cluster is otherwise excluded from later automatic cycles.
- `TELEGRAM_REQUIRE_PREVIEW=false` currently still waits for `TELEGRAM_PREVIEW_RETRY_WINDOW` before sending text. In the target lifecycle, optional previews are attempted once per cycle and missing imagery sends text immediately.
- The Viewer diagnostic explanation explicitly promises a retry window even though the diagnostic itself is read-only; that text must change with the lifecycle.

## Decision log

- **Decision:** Evaluate every complete active-snapshot cluster on every consumed snapshot. **Reason:** The current snapshot is already authoritative and naturally supplies retry input without retaining unsent candidates. **Date:** 2026-07-24.
- **Decision:** Keep exact-date preview as a current-cycle filter when `TELEGRAM_REQUIRE_PREVIEW=true`. **Reason:** Preserve the configured imagery safety requirement while removing deadline state. **Date:** 2026-07-24.
- **Decision:** Preserve startup suppression with a fixed startup-baseline ID set and preserve once-per-episode delivery history. **Reason:** Avoid restart floods and repeated five-minute sends without reintroducing an unsent-candidate queue. **Date:** 2026-07-24.
- **Decision:** Remove `TELEGRAM_PREVIEW_RETRY_WINDOW` from the recognized contract and silently ignore an existing variable. **Reason:** It has no meaning without retained candidates, and silent retirement avoids breaking existing deployments. **Date:** 2026-07-24.
- **Decision:** Record the durable lifecycle change in ADR 0004, superseding ADR 0002 without rewriting its historical text. **Reason:** The accepted ADR explicitly selected Core-owned seen/pending/retry behavior and configuration compatibility. **Date:** 2026-07-24.

## Concrete implementation steps

1. Replace new-detection selection in `NotificationCandidateEngine` with `NotificationClustering.Create(snapshot.Items, ...)`. On the first ready snapshot with startup notification disabled, capture baseline IDs and return. On later cycles, skip baseline-only clusters and delivered-episode continuations before policy work.
2. Evaluate metadata and land cover, select and fetch one preview, reject a missing required preview for that cycle, and otherwise enrich and deliver immediately. Record delivery history only after `Delivered`; leave transient failures unrecorded so the next snapshot reevaluates them, and preserve permanent-stop behavior.
3. Reduce automatic state to bounded delivered-episode history. Delete pending-candidate/preparation types and new-only/pending merge helpers that no longer have callers. Make manual preparation cluster the complete snapshot directly.
4. Remove preview-retry configuration and stale processing-summary/log fields. Retain the `TELEGRAM_SEEN_RETENTION` environment key as the compatibility name for delivered-history retention and rename the Core option accordingly.
5. Rewrite focused tests for repeated current-snapshot evaluation, preview recovery, optional text delivery, transient-send recovery, startup baseline behavior, and delivered-episode transitivity. Preserve diagnostic non-mutation, manual selection, and nearby-context coverage.
6. Add ADR 0004 and update the ADR registry/status links. Rewrite current lifecycle, state, configuration, diagnostics, failure, and recovery documentation, including root agent invariants.

## Validation and acceptance criteria

- Focused tests prove that a missing required preview is rejected on successive snapshots and a later available preview delivers without retained pending state.
- Optional missing imagery delivers text in the same cycle; transient Telegram failure retries on the next snapshot; successful episodes and linked continuations do not resend.
- Startup-disabled baseline clusters remain suppressed across snapshots until a post-start detection joins; startup-enabled evaluation runs on the first ready snapshot.
- No source, log, summary, environment table, or current-behavior documentation refers to pending previews or a preview retry window outside historical ADR/plan context.
- Run:
  - `dotnet restore ThermalWatch.slnx`
  - `dotnet build ThermalWatch.slnx -c Release --no-restore --nologo`
  - `dotnet test ThermalWatch.slnx -c Release --no-build --nologo`
  - `dotnet format ThermalWatch.slnx --verify-no-changes --no-restore`
  - `dotnet test tests/ThermalWatch.Tests.csproj -c Release --nologo --filter FullyQualifiedName~DocumentationValidationTests`
  - Viewer JavaScript syntax/tests from `docs/development.md`.
- With Telegram credentials removed from the child process, capture and visually inspect NASA and Google desktop/narrow Viewer screenshots showing an unavailable exact-preview diagnostic and the new later-snapshot explanation.

## Recovery or rollback guidance

All edits are source-controlled and contain no migration or external mutation. Stop after any failed checkpoint, inspect `git diff`, and fix forward without resetting unrelated work. Reverting this task means restoring the prior seen/pending lifecycle, option fields, environment parser, tests, ADR status, and durable documentation together. Do not delete or source the ignored `.env` unless a user-authorized live Viewer check requires it; never enable Telegram during that check.

## Outcomes and retrospective

Automatic processing now derives every decision from the complete active snapshot published by the FIRMS polling cycle. Core retains only the fixed startup baseline and successful delivered-episode history; pending candidates, general seen-ID selection, preview deadlines, and their supporting types are gone. Required missing imagery rejects only the current evaluation, optional missing imagery sends text immediately, and transient delivery failures retry from current data after a later publication.

Release restore/build/test/format validation passed with zero warnings and all 150 .NET tests passing. The focused documentation suite passed 7 tests, and the Viewer JavaScript suite passed all 32 tests plus both syntax checks. Live NASA and Google desktop/narrow captures for the reported `59.79049, 30.44065` anomaly showed the new later-snapshot explanation, complete cluster highlighting, usable controls and details, no horizontal overflow, and a successful NASA-to-Google-to-NASA roundtrip. Screenshots are outside the repository under `/tmp/thermalwatch-real-time-notifications.vsEzuf/`; Telegram credentials were removed from the live child process.

The polling cadence, GIBS representative-overlay and contextual-base fallback rules, manual-send behavior, message formatting, and raw anomaly API remain unchanged. The key operational limitation is deliberate: with exact previews required, a still-active cluster cannot send until a later snapshot evaluation finds usable exact-date imagery.
