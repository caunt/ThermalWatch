# Telegram linked-discussion comments

## Purpose and observable outcome

ThermalWatch currently places all notification details in one Telegram channel post and adds Google and Yandex Maps as inline buttons. This change will keep a compact, common anomaly summary in the configured channel, place map links in that summary, and send the satellite-specific details as one comment beneath the corresponding post in the channel's linked discussion. Automatic and manual candidate sends will share this behavior; manual status messages will remain unchanged.

## Context and repository orientation

`src/ThermalWatch.Telegram/TelegramMessageFormatter.cs` owns Telegram HTML and caption compaction. `src/ThermalWatch.Telegram/TelegramNotificationService.cs` validates the configured channel and sends prepared Core candidates. `tests/TelegramMessageFormatterTests.cs` covers current formatting, but linked-discussion request sequencing needs new fake-HTTP coverage. Durable behavior belongs in `docs/components/telegram-notifier.md`, with configuration and failure semantics synchronized in `docs/operations.md`.

The bot continues to use only `TELEGRAM_BOT_TOKEN` and `TELEGRAM_CHANNEL_ID`. Telegram's channel metadata supplies the linked discussion ID. Core remains responsible for clustering, representative selection, preview preparation, nearby enrichment, and delivery acknowledgement.

## Progress

- [x] 2026-07-24T11:55:50Z: Inspected repository guidance, formatter, sender, tests, Telegram.Bot capabilities, and routed documentation.
- [x] 2026-07-24T12:09:00Z: Implemented shared channel formatting and detail-comment formatting.
- [x] 2026-07-24T12:09:00Z: Resolved and validated the linked discussion, then delivered and replied without inline keyboards.
- [x] 2026-07-24T12:09:00Z: Added focused formatter and fake-HTTP transport tests; all 40 focused Telegram tests pass.
- [x] 2026-07-24T12:13:00Z: Synchronized durable Telegram and operations documentation.
- [x] 2026-07-24T12:16:00Z: Completed focused and full validation with no warnings, failures, formatting drift, or diff errors.
- [x] 2026-07-24T12:19:00Z: Created the focused commit and completed the final push preflight for `main`.

## Surprises and discoveries

- The cluster representative is selected primarily by FRP and can be older than the newest member. Only the channel post's observation time and pass will move to the newest member; representative location, single-satellite metrics, map links, and prepared preview remain aligned.
- Telegram.Bot 22.10.2.1 exposes `ChatFullInfo.LinkedChatId` and cross-chat `ReplyParameters`, so no inbound update loop or new configuration value is required.

## Decision log

- 2026-07-24: Put Google and Yandex links on a separate final map line in the shared channel text. This removes reply markup without overloading the coordinate line.
- 2026-07-24: Treat a successfully posted main message as delivered even if its detail comment later fails. This avoids duplicate public posts; the missing comment is logged.
- 2026-07-24: Fail Telegram startup validation when the channel has no usable linked discussion or the bot cannot write there. This avoids knowingly operating in a permanently incomplete mode.
- 2026-07-24: Preserve existing public formatter overloads, while adding an internal paired result for the sender. This keeps the change source-compatible for formatter callers.

## Concrete implementation steps

1. Refactor `TelegramMessageFormatter` so its existing `Format` overloads return the compact common channel caption. Select the newest member deterministically for observed time/pass, retain representative location and map URLs, use the common diameter label, preserve nearby-source compaction, and add HTML map links. Add an internal result containing the main caption and the appropriate single- or multi-satellite detail comment.
2. Extend Telegram startup validation to resolve the configured channel's numeric ID, require its linked chat to be a reciprocal supergroup discussion, and require the bot to be a member allowed to send messages. Store both destinations in the validated state and add safe validation logs.
3. Change candidate delivery to send the photo-or-text channel post without reply markup, capture its returned message ID, and send the formatted detail to the linked discussion with cross-chat reply parameters referencing that channel post. Preserve main-send errors. Propagate cancellation, but catch and log comment errors after a successful main send.
4. Replace obsolete formatter and keyboard tests with exact common-main and detail-comment coverage. Add a fake Telegram HTTP handler around an internal delivery seam to assert request order, destinations, reply parameters, absence of reply markup, and partial-comment failure behavior.
5. Rewrite stale Telegram component documentation in place and update operations validation/failure text. Do not add configuration, an ADR, or documentation routing.

## Validation and acceptance criteria

Run from the repository root:

```bash
dotnet test tests/ThermalWatch.Tests.csproj -c Release --nologo --filter FullyQualifiedName~Telegram
dotnet test tests/ThermalWatch.Tests.csproj -c Release --nologo --filter FullyQualifiedName~DocumentationValidationTests
dotnet restore ThermalWatch.slnx
dotnet build ThermalWatch.slnx -c Release --no-restore --nologo
dotnet test ThermalWatch.slnx -c Release --no-build --nologo
dotnet format ThermalWatch.slnx --verify-no-changes --no-restore
git diff --check
```

Acceptance requires exact shared channel content for both cluster types, exact detail templates, no inline keyboard, correct linked-discussion reply request shape, latest-member time/pass with representative-aligned location/detail, nonfatal comment failure after a successful main post, synchronized docs, and no live Telegram calls.

## Recovery or rollback guidance

All changes are local and idempotent. Tests use fake HTTP only. If implementation fails partway, inspect the worktree and amend the focused files without resetting unrelated changes. Reverting the formatter, sender, tests, logs, and two documentation sections restores the prior single-message behavior. No external messages, migrations, stored state, or irreversible operations are involved.

## Outcomes and retrospective

ThermalWatch now sends one compact shared channel template for every candidate, with the newest observation time/pass, representative location, diameter, nearby context, and in-text map links. It sends the appropriate sensor detail as a cross-chat reply in the channel's validated linked discussion and no longer emits inline keyboards. Main-post failures retain the previous automatic/manual semantics; a comment failure after a successful main post is logged and does not produce a duplicate channel notification.

Focused Telegram tests passed 40 of 40, documentation validation passed 7 of 7, and the full suite passed 216 of 216. Release restore/build, analyzer enforcement, formatting verification, and `git diff --check` all passed. No live Telegram request, credential, configuration name, endpoint, Core policy, architecture boundary, or unrelated file was changed.

Durable behavior was synchronized in the Telegram notifier and operations documents. The documentation index, architecture, domain policy, root guide, and ADR registry were consulted or routed as required and intentionally left unchanged because their contracts and routing remain accurate.
