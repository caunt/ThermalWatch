# Total cluster FRP

## Purpose and observable outcome

Add the sum of available member fire radiative power to notification clusters. Operators can require at least 50 MW total cluster FRP by default, Viewer users can see and prioritize the aggregate, and Telegram detail comments include it without changing the raw anomaly API or claiming the sum is instantaneous physical power.

## Context and repository orientation

`NotificationCluster` already owns the immutable member collection, `NotificationPolicy` owns shared eligibility criteria, and `NotificationCandidateEngine` supplies automatic, manual, and Viewer candidates. `ApplicationConfiguration` parses exact uppercase environment names. Viewer eligible-cluster and diagnostic records are public camel-case JSON contracts, while `TelegramMessageFormatter` owns presentation only. Durable guidance lives in `docs/architecture.md`, `docs/operations.md`, `docs/domain/notification-policy.md`, `docs/components/web-viewer.md`, and `docs/components/telegram-notifier.md`.

## Progress

- [x] Add the aggregate, configuration, policy, rejection telemetry, and focused Core tests. (2026-07-25T11:24:15Z)
- [x] Expose total FRP through candidate ordering, Viewer contracts/UI, and Telegram comments with tests. (2026-07-25T11:24:15Z)
- [x] Synchronize durable documentation. (2026-07-25T11:24:15Z)
- [x] Complete focused, full, publish, and live visual validation. (2026-07-25T11:32:00Z)
- [x] Commit and push the verified change to `origin/main` as `2bffb7e`. (2026-07-25T11:34:50Z)

## Surprises and discoveries

- 2026-07-25: The authorized UKR live run published 155 anomalies and 57 clusters; the total-FRP-focused validation profile produced four eligible rows. The FIRMS country endpoint was temporarily unavailable, so the existing verified area fallback supplied the complete snapshot without affecting this feature.
- 2026-07-25: One uncached NASA tile pass returned documented degraded coverage and the neutral background. Switching providers retried the uncached result and restored complete visible coverage; both final NASA desktop captures and the narrow capture were clean.

## Decision log

- 2026-07-25: Sum every available finite member FRP and report the total unavailable only when no member has usable FRP or the aggregate is non-finite.
- 2026-07-25: Add `NOTIFICATION_MIN_CLUSTER_TOTAL_FRP_MW` with a 50 MW default while retaining the independent 50 MW representative threshold and its existing setting.
- 2026-07-25: Rank manual and Viewer candidates by total FRP, then representative/peak FRP, before the existing deterministic tie-breakers.
- 2026-07-25: Preserve peak FRP presentation and add total FRP; do not change clustering, representative selection, preview sizing, vegetation exceptions, lifecycle identity, or `/api/anomalies`.
- 2026-07-25: Treat this as an extension of established boundaries, so no ADR is warranted.

## Concrete implementation steps

1. Add a derived nullable total to `NotificationCluster`, a visibility option and environment parser, an exhaustive diagnostic criterion, and a distinct low-total rejection/log category.
2. Add total FRP to eligible-cluster and diagnostic records. Change shared manual/Viewer ordering to total then peak, update Viewer validation and rendering, and keep individual anomaly data unchanged.
3. Add total FRP to single- and multi-satellite Telegram detail comments while preserving existing FRP lines and main captions.
4. Add focused aggregation, configuration, policy, ordering, endpoint, UI-contract, and formatter tests. Update routed durable documentation without changing documentation routing.
5. Run automated, publish, live Viewer, screenshot, and visual validation. Review the diff, commit, push `main`, and record outcomes here.

## Validation and acceptance criteria

Run focused .NET tests while iterating; both Viewer JavaScript syntax checks and the Node suite; documentation validation; `dotnet restore`, Release build/test, and format verification; `git diff --check`; and Release publish with the static-asset assertion. Run the published host from the existing ignored `.env` with Telegram variables removed. Capture and open NASA and Google screenshots at 1440x900 and 390x844 plus a provider round trip, with total FRP visible in eligible rows and selected diagnostics. The final worktree must contain only intended files, preserve `.env`, include no secrets, and have local `main` equal to `origin/main` after push.

## Recovery or rollback guidance

All code and documentation changes are source-only and repeatable. Preserve unrelated work and use the plan plus `git diff` to resume. Stop live servers and browsers normally. Never enable Telegram or call the manual-send endpoint during validation. Never force-push; if remote `main` advances, rebase the task commit, resolve only task-owned conflicts, rerun validation, and push after a clean result.

## Outcomes and retrospective

Core now computes one nullable finite total from available member FRP values and applies an independently configurable 50 MW default criterion. Manual and Viewer priority uses total then peak FRP. Eligible-cluster and diagnostic JSON, the framework-free Viewer, and both Telegram detail-comment variants expose the aggregate while preserving representative FRP and every existing clustering, lifecycle, preview, land-cover, and anomaly-API boundary.

Validation completed with a zero-warning Release build, 270 passing .NET tests, 36 passing Node tests, seven passing documentation checks, clean format/diff checks, and a successful Release publish/static-asset assertion. A Telegram-disabled live run exercised four passing rows and selected diagnostics across NASA and Google at desktop and narrow sizes plus two provider round trips. All five final images under `/tmp/thermalwatch-total-frp.jLJUrV/` were opened and inspected; total/peak values and the active criterion were readable, maps and markers remained usable, and no overflow or provider defect remained. The ignored owner-only `.env` was preserved and no Telegram request was made.

Durable behavior is recorded in the root README plus the architecture, operations, notification-policy, web-viewer, and Telegram-notifier documents. Documentation routing remains accurate and no ADR was warranted. Implementation commit `2bffb7e` was pushed to `origin/main`; this plan closure is the only follow-up publication step.
