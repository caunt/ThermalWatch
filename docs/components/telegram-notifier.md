# Telegram notifier

> **Purpose:** Explain the stateful Telegram integration and its delivery, retry, and disable boundaries.
> **Scope:** Credential validation, snapshot consumption, seen/pending state, GIBS collaboration, automatic delivery, and manual sends.
> **Sources of truth:** [Notification service](../../src/ThermalWatch.Telegram/TelegramNotificationService.cs), [automatic state](../../src/ThermalWatch.Telegram/TelegramAutomaticNotificationState.cs), [Telegram options](../../src/ThermalWatch.Telegram/TelegramOptions.cs), [GIBS client](../../src/ThermalWatch.Core/GibsClient.cs), and [message formatter](../../src/ThermalWatch.Telegram/TelegramMessageFormatter.cs).
> **Update when:** Telegram startup, state, filtering collaboration, imagery, formatting, sending, concurrency, or failure handling changes.

Read the [notification policy](../domain/notification-policy.md) for domain eligibility and clustering rules. This document focuses on component lifecycle and failure boundaries.

## Enablement and startup validation

The API host registers one singleton `TelegramNotificationService` regardless of configuration so the manual endpoint can report availability. It starts that singleton as a hosted service only when both Telegram credentials are present.

At startup the service creates an outbound-only bot client, calls `GetMe`, resolves the configured chat, requires channel type, and requires the bot to be owner or an administrator allowed to post. Failure logs a safe message and disables notifications for the process lifetime. Validation is not retried automatically.

The validated bot client and channel ID form the service's availability state. Polling and HTTP APIs continue when Telegram is missing, invalid, or disabled.

## Automatic snapshot processing

The service is the single reader of the snapshot store's bounded update channel. It ignores snapshots until at least one FIRMS segment has succeeded.

For every ready update it:

1. Expires seen IDs and delivered-episode history, then identifies new observations.
2. Builds notification clusters, optionally including related active context.
3. Coalesces related pending clusters, or suppresses and extends a related successfully delivered episode.
4. Applies visibility metadata and optional NASA land-cover evaluation to the current merged cluster.
5. Adds accepted clusters to an in-memory pending list with first-seen time and selected crop dimensions.
6. Revisits pending entries to fetch exact-date previews and either send, keep pending, discard according to policy, or suppress one made redundant by an earlier successful send.
7. Logs aggregate visibility, duplicate-episode, and land-cover outcomes.

Seen IDs are timestamped before evaluation. Delivered history is recorded only after a successful automatic send; related later detections extend it transitively without sending or editing another message. Seen and delivered detections are each bounded by configured retention and a hard cap of 100,000. Pending entries retain their earliest preview retry start when merged. None of this state is persisted, so restart resets all three collections.

## GIBS collaboration

The notifier uses one [GibsClient](../../src/ThermalWatch.Core/GibsClient.cs) for two independent purposes:

- Preview composition: verify exact availability for the representative thermal overlay, probe pass-matched same-date base crops for usable pixels, and request a bounded PNG WMS composite with the first usable base. The representative base is preferred; fallback proceeds through its sensor family before the other supported family.
- Land cover: find the newest annual date common to required tiles, fetch bounded indexed PNG tiles, decode official colors to IGBP classes, and sample the cluster area.

The shared memory cache is limited by the API host to 64 MiB. Successful previews include selected-base attribution and expire after two hours. Black or otherwise unusable probes are not cached, while negative global availability and land-cover results use short expirations so transient GIBS gaps can recover. GIBS cancellation propagates; other failures return unavailable results rather than stopping notification processing.

## Delivery behavior

Messages use Telegram HTML and paired inline Google and Yandex Maps buttons. The formatter selects a single- or multi-satellite template and progressively compacts it to Telegram's photo-caption limit. It HTML-encodes dynamic values.

When a preview exists, the service sends a photo with caption. A fallback caption names its contextual base and the representative thermal overlay instead of claiming sensor-matched imagery. When text fallback is allowed, the service sends a message with link previews disabled.

- Telegram `400`, `401`, or `403` during automatic delivery is treated as permanent and disables automatic processing until restart.
- Other send failures are treated as transient; the current pending entry remains and processing returns to wait for another snapshot update.
- A send failure or preview timeout does not establish delivered-episode history.
- Cancellation stops the hosted service normally.

There is no independent retry timer for pending sends. Another published snapshot drives the next pass.

## Manual send path

`SendTopAsync` first requires a validated client and uses a nonblocking semaphore so only one manual operation runs at a time. It reads `snapshotStore.Current`; it does not wait for or request a FIRMS refresh.

The manual path evaluates and ranks the full snapshot, sends a status message, and then sends selected clusters individually. A status-message failure ends the operation with a distinct result. Individual candidate failures are collected by cluster ID without stopping later sends.

Manual operations do not inspect or mutate automatic seen IDs, delivered episodes, or pending previews. The endpoint status mapping and input validation are in [Program.cs](../../src/ThermalWatch.Api/Program.cs).

## Tests and diagnostics

The tests cover option defaults, active-context clustering, delivered-episode continuity and pending coalescing, land-cover policy, land-cover tile decoding/cache reuse, preview no-data detection and fallback ordering, message link behavior, and formatter output. They do not call Telegram or NASA live.

Operational signals are console logs and manual endpoint results. There is no Telegram health endpoint, webhook, inbound update loop, durable outbox, or persisted deduplication state.
