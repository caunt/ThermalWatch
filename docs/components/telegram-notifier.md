# Telegram notifier

> **Purpose:** Explain the Telegram transport boundary, validation, message construction, delivery, and disable behavior.
> **Scope:** Credential validation, prepared-candidate consumption, formatting, automatic and manual sends, concurrency, and transport failures.
> **Sources of truth:** [Notification service](../../src/ThermalWatch.Telegram/TelegramNotificationService.cs), [Telegram options](../../src/ThermalWatch.Telegram/TelegramOptions.cs), [message formatter](../../src/ThermalWatch.Telegram/TelegramMessageFormatter.cs), and [Core candidate engine](../../src/ThermalWatch.Core/NotificationCandidateEngine.cs).
> **Update when:** Telegram startup, formatting, sending, concurrency, acknowledgement, or transport error handling changes.

Read the [notification policy](../domain/notification-policy.md) for clustering, eligibility, land cover, previews, deduplication, diagnostics, and manual ranking. Those responsibilities belong to Core; this document focuses on the Telegram adapter.

## Boundary and enablement

`ThermalWatch.Telegram` contains no clustering, filter, candidate-state, preview-selection, or ranking policy. The API host always registers one singleton `TelegramNotificationService` so the manual endpoint can report availability. It starts that singleton as a hosted service only when both Telegram credentials are present.

At startup the service creates an outbound-only bot client, calls `GetMe`, resolves the configured chat, requires channel type, and requires the bot to be owner or an administrator allowed to post. Failure logs a safe message and disables Telegram delivery for the process lifetime. Validation is not retried automatically, and polling, Viewer diagnostics, candidate policy, and HTTP APIs remain available.

The validated bot client and channel ID form the adapter's availability state. Bot tokens and channel identifiers remain the only options owned by the Telegram project; the API host parses notification-policy options into neutral Core types.

## Automatic delivery

The service is the single reader of the snapshot store's bounded update channel. For each update it calls the Core candidate engine and supplies a delivery callback. Core owns ready-snapshot handling, new-observation selection, clustering, metadata and land-cover policy, seen/delivered/pending state, preview retries, and delivery acknowledgement.

For each prepared candidate passed to the callback, Telegram:

1. Builds the HTML caption and inline map buttons.
2. Sends a photo when Core supplied preview bytes, otherwise sends text with link previews disabled.
3. Returns `Delivered`, `RetryLater`, or `Stop` to Core.

Core records delivered-episode history only after `Delivered`. A transient transport failure returns `RetryLater`, so the pending candidate remains and a later snapshot update retries it. Telegram `400`, `401`, or `403` returns `Stop`, clears the validated client, and ends automatic processing until restart. Cancellation stops the hosted service normally.

## Message construction

Messages use Telegram HTML and paired inline Google and Yandex Maps buttons. The formatter selects a single- or multi-satellite template and progressively compacts it to Telegram's photo-caption limit. It HTML-encodes dynamic values.

A preview caption names its contextual base and representative thermal overlay rather than claiming sensor-matched imagery when Core used a fallback base. The candidate's detection count, satellites, representative metadata, cluster diameter, land-cover summary, preview dimensions, and GIBS attribution arrive as prepared Core data; Telegram does not recalculate eligibility.

## Manual send path

`SendTopAsync` requires a validated client and uses a nonblocking semaphore so only one manual operation runs at a time. It asks Core to prepare and rank the requested candidates from `snapshotStore.Current`; it does not wait for or request a FIRMS refresh.

Telegram sends an introductory status message and then sends the selected prepared candidates individually. A status-message failure ends the operation with a distinct result. Individual candidate failures are collected by cluster ID without stopping later sends. Core's manual preparation does not inspect or mutate automatic seen IDs, delivered episodes, or pending previews.

The endpoint status mapping and input validation remain in [Program.cs](../../src/ThermalWatch.Api/Program.cs). `/api/telegram/send-top` is unauthenticated and side-effecting, so it must be protected by the deployment network boundary.

## Tests and diagnostics

Core tests cover clustering, policy, candidate lifecycle, land cover, preview handling, manual ranking, and read-only Viewer diagnostics. Telegram tests cover options, formatter output, location buttons, and adapter-visible response contracts. Tests use fake HTTP handlers and never call Telegram or NASA live.

Operational signals are console logs and manual endpoint results. There is no Telegram health endpoint, webhook, inbound update loop, durable outbox, persisted state, or independent retry timer.
