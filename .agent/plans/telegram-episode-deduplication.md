# Telegram episode deduplication

## Purpose and observable outcome

Prevent automatic Telegram messages from repeating the same continuous thermal-anomaly episode when later FIRMS snapshots add detections or satellites. After one automatic message sends successfully, detections connected through the configured cluster radius and time window extend that delivered episode without another message. A spatially or temporally disconnected episode remains eligible. Manual `send-top` behavior is unchanged.

## Context and repository orientation

`src/ThermalWatch.Telegram/TelegramNotificationService.cs` currently deduplicates observations by FIRMS anomaly ID before clustering. A later satellite pass has new IDs, so the notifier creates and sends an expanded cluster even when it overlaps the previously delivered cluster. Pending exact-date previews are also stored independently and can represent successive versions of the same episode.

The implementation will keep automatic episode and pending state inside `ThermalWatch.Telegram`, using the existing `TELEGRAM_CLUSTER_RADIUS_KM`, `TELEGRAM_CLUSTER_TIME_WINDOW`, and `TELEGRAM_SEEN_RETENTION` values. Generic clustering remains in Core; the anomaly API and manual Telegram endpoint remain unchanged. Durable behavior belongs in `docs/domain/notification-policy.md`, `docs/components/telegram-notifier.md`, and the state summaries in `docs/architecture.md` and `docs/operations.md`.

## Progress

- [x] 2026-07-23 12:48 UTC: Confirmed the worktree is clean and reviewed repository guidance, routed documentation, notifier source, clustering source, and existing tests.
- [x] 2026-07-23 12:53 UTC: Implemented delivered-episode tracking, pending-candidate coalescing, successful-send activation, and duplicate diagnostics.
- [x] 2026-07-23 12:54 UTC: Added and passed nine focused state tests covering satellite updates, transitive continuity, time/radius breaks, expiry, pending merge/removal, and retry eligibility.
- [x] 2026-07-23 12:56 UTC: Synchronized notification policy, Telegram component, architecture, and operations documentation.
- [x] 2026-07-23 12:58 UTC: Completed focused and full validation and reconciled the diff; the verified change is ready to commit and push to `main`.

## Surprises and discoveries

- The existing cluster ID hashes every member ID, so adding observations necessarily creates a different notification ID.
- Active clustering context is enabled only when the visibility filter requires multiple detections. Episode matching therefore must compare observations directly and must not rely on a previous member being included in the new candidate cluster.
- An automatic candidate is marked seen before filtering and sending. Delivered-episode state must be separate so rejection, preview timeout, or transport failure does not incorrectly establish a delivered episode.
- `docs/operations.md` also summarizes all process-local state, so its state inventory requires synchronization even though no configuration or recovery procedure changes.

## Decision log

- 2026-07-23: Define continuity with the existing spatial radius and acquisition-time window. Add every suppressed continuation to delivered history so A-to-B-to-C chains remain one episode even when A and C are not directly related.
- 2026-07-23: Record an episode only after successful automatic delivery. Rejections, preview timeout, and send failures retain current retry/seen behavior but do not suppress a future related candidate.
- 2026-07-23: Merge related pending candidates before evaluation, preserve the earliest preview retry timestamp, and recompute representative-dependent filtering, land cover, preview sizing, and formatting from the merged cluster.
- 2026-07-23: Keep episode history process-local, retention-bounded, and capped at 100,000 detections. This is a focused notification-policy correction and does not warrant an ADR or new configuration.

## Concrete implementation steps

1. Extract Telegram-specific clustering helpers into a focused source file and add shared detection/cluster relationship and connected-cluster merge operations using Core's existing clustering algorithm.
2. Add an internal automatic-notification state type that stores pending candidates and successfully delivered episode detections. Its candidate preparation operation must transitively merge related pending entries, retain their earliest first-seen time, then either suppress and extend a related delivered episode or return the merged candidate for current filter evaluation.
3. Integrate that state into `TelegramNotificationService`: expire state with seen IDs, prepare every automatic cluster before filters, queue accepted prepared clusters, suppress redundant pending entries before preview work, and mark delivery only after Telegram succeeds. Add aggregate/debug diagnostics for episode suppression. Do not touch the manual path.
4. Add unit tests for direct and transitive continuation, spatial/time breaks, retention expiry, pending coalescing, delivered-to-pending bridging, and non-delivered candidate behavior.
5. Update routed documentation to describe one alert per continuous delivered episode, pending coalescing, successful-send activation, memory bounds, restart behavior, and the unchanged manual bypass.

## Validation and acceptance criteria

- `dotnet test tests/ThermalWatch.Tests.csproj -c Release --nologo --filter FullyQualifiedName~TelegramAutomaticNotificationStateTests` passes without external calls.
- `dotnet restore ThermalWatch.slnx` succeeds.
- `dotnet build ThermalWatch.slnx -c Release --no-restore --nologo` succeeds with warnings treated as errors.
- `dotnet test ThermalWatch.slnx -c Release --no-build --nologo` passes.
- `dotnet format ThermalWatch.slnx --verify-no-changes --no-restore` reports no changes required.
- `dotnet test tests/ThermalWatch.Tests.csproj -c Release --nologo --filter FullyQualifiedName~DocumentationValidationTests` passes.
- `git diff --check` passes, the diff contains no secrets or unrelated changes, and API/config/manual-send contracts remain unchanged.

## Recovery or rollback guidance

All changes are local source, tests, documentation, and this working plan; there are no migrations, provider calls, or irreversible actions. Stop safely between patches or validation commands. Re-run failed deterministic tests after correcting source. Reverse only task-owned hunks with a targeted patch, preserving unrelated user changes and the ignored `.env`.

## Outcomes and retrospective

Automatic delivery now records lightweight location/time history only after Telegram accepts a message. Related later satellite observations extend that history transitively and are logged as duplicate episodes instead of being filtered, imaged, or sent again. Related pending candidates merge into one current cluster while retaining the original preview deadline; pending entries made redundant by an earlier successful send are removed. Manual sends, configuration, the anomaly API, and generic Core clustering contracts are unchanged.

Nine focused episode-state tests pass. The Release build succeeds without warnings, all 63 tests pass, formatting requires no changes, all seven documentation validations pass, and `git diff --check` is clean. Durable behavior is synchronized in the notification-policy, Telegram-notifier, architecture, and operations documents. The root README and documentation index remain unchanged because their overview and routing are still accurate; no ADR is warranted. Episode state remains intentionally bounded and process-local, so restart resets it and permits the stateless service to begin fresh.
