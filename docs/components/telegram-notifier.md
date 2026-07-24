# Telegram notifier

> **Purpose:** Explain the Telegram transport boundary, validation, message construction, delivery, and disable behavior.
> **Scope:** Credential validation, prepared-candidate consumption, formatting, automatic and manual sends, concurrency, and transport failures.
> **Sources of truth:** [Notification service](../../src/ThermalWatch.Telegram/TelegramNotificationService.cs), [Telegram options](../../src/ThermalWatch.Telegram/TelegramOptions.cs), [message formatter](../../src/ThermalWatch.Telegram/TelegramMessageFormatter.cs), and [Core candidate engine](../../src/ThermalWatch.Core/NotificationCandidateEngine.cs).
> **Update when:** Telegram startup, formatting, sending, concurrency, acknowledgement, or transport error handling changes.

Read the [notification policy](../domain/notification-policy.md) for clustering, eligibility, land cover, previews, nearby mapped context, deduplication, diagnostics, and manual ranking. Those responsibilities belong to Core; this document focuses on the Telegram adapter.

## Boundary and enablement

`ThermalWatch.Telegram` contains no clustering, filter, candidate-state, preview-selection, or ranking policy. The API host always registers one singleton `TelegramNotificationService` so the manual endpoint can report availability. It starts that singleton as a hosted service only when both Telegram credentials are present.

At startup the service creates an outbound-only bot client, calls `GetMe`, resolves the configured chat, requires channel type, and requires the bot to be owner or an administrator allowed to post. It derives the channel's linked discussion from Telegram metadata, requires a reciprocal supergroup link, and requires the bot to be a member allowed to send messages there. Failure logs a safe message and disables Telegram delivery for the process lifetime. Validation is not retried automatically, and polling, Viewer diagnostics, candidate policy, and HTTP APIs remain available.

The validated bot client, configured channel ID, resolved numeric channel ID, and linked discussion ID form the adapter's availability state. Bot tokens and channel identifiers remain the only options owned by the Telegram project; no separate discussion setting is required. The API host parses notification-policy options into neutral Core types.

## Automatic delivery

The service is the single reader of the snapshot store's bounded update channel. For each update it calls the Core candidate engine and supplies a delivery callback. Core owns ready-snapshot handling, complete active-snapshot clustering, eligibility-based startup-incident suppression, metadata and land-cover policy, preview evaluation, nearby-feature enrichment, delivered-episode history, and delivery acknowledgement.

For each prepared candidate passed to the callback, Telegram:

1. Builds the shared HTML channel caption and the appropriate single- or multi-satellite detail comment.
2. Sends the main channel post as a photo when Core supplied preview bytes, otherwise as text with link previews disabled.
3. Sends one text comment to the linked discussion with cross-chat reply parameters that identify the exact channel post.
4. Returns `Delivered`, `RetryLater`, or `Stop` to Core according to the main-post result.

Core records delivered-episode history only after `Delivered`. A transient main-post failure returns `RetryLater`; Core retains no candidate, and the next snapshot reevaluates the active cluster. A main-post Telegram `400`, `401`, or `403` returns `Stop`, clears the validated client, and ends automatic processing until restart. Once the main post succeeds, a comment failure is logged but the candidate remains delivered so a later snapshot cannot duplicate the channel post. Cancellation stops the hosted service normally.

## Message construction

Messages use Telegram HTML without reply markup. Every channel post uses one compact template containing the cluster countries, newest observation time and pass, optional nearby context, diameter, representative coordinates, and inline Google and Yandex Maps links; the Yandex link opens the representative coordinates in satellite view. The formatter progressively compacts nearby names and lists to Telegram's photo-caption limit and HTML-encodes dynamic values.

The linked-discussion comment holds the sensor detail. A single-satellite comment uses representative satellite, source, confidence, FRP, thermal contrast, detection count, and preview detail. A multi-satellite comment uses confirmation count, satellites, feeds, peak metrics, detection count, land-cover summary, and preview detail. Preview wording names its contextual base and representative thermal overlay rather than claiming sensor-matched imagery when Core used a fallback base. These values arrive as prepared Core data; Telegram does not recalculate eligibility or call Overpass.

When Core supplies one or more nearby features, “Possible nearby sources” appears in the channel post before its diameter and renders every result as a distance-first monospace line using the fixed `0.00 km` form. Empty or unavailable lookups add no section. Progressive compaction shortens names while retaining all five bounded results within the photo-caption limit.

## Manual send path

`SendTopClustersAsync` requires a validated client and uses a nonblocking semaphore so only one manual operation runs at a time. It asks Core to prepare and rank the requested candidates from `snapshotStore.Current`; it does not wait for or request a FIRMS refresh. Core enriches only the selected representatives after ranking.

Telegram sends an introductory status message to the channel and then sends the selected prepared candidates individually as the same channel-post/discussion-comment pair used by automatic delivery. A status-message failure ends the operation with a distinct result. Main-post failures are collected by cluster ID without stopping later sends; a comment failure after a successful main post is logged and the candidate counts as sent. Core's manual preparation does not inspect or mutate automatic startup incidents or delivered episodes.

The endpoint status mapping and input validation remain in [Program.cs](../../src/ThermalWatch.Api/Program.cs). `/api/telegram/send-top` is unauthenticated and side-effecting, so it must be protected by the deployment network boundary.

## Tests and diagnostics

Core tests cover clustering, policy, candidate lifecycle, land cover, preview handling, nearby-feature enrichment, manual ranking, and read-only Viewer diagnostics. Telegram tests cover options, exact channel/comment formatting and bounds, nearby-context wording, in-text map links, linked-discussion request sequencing, partial failures, and adapter-visible response contracts. Tests use fake HTTP handlers and never call Telegram, NASA, or Overpass live.

Operational signals are console logs and manual endpoint results. There is no Telegram health endpoint, webhook, inbound update loop, durable outbox, persisted state, or independent retry timer.
