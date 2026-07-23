# Telegram GIBS imagery fallback

## Purpose and observable outcome

Telegram must not send a syntactically valid NASA GIBS preview whose contextual base imagery is an opaque black or transparent no-data crop. ThermalWatch will probe the representative satellite's exact-date base layer, fall back deterministically to another supported exact-date satellite base when necessary, keep the representative satellite's thermal-anomaly overlay, and state both sources in fallback captions. If every base is unusable, existing automatic retry, timeout, required-preview, and text-fallback behavior remains authoritative. The FIRMS snapshot, anomaly API, viewer, filtering, and notification eligibility are out of scope.

## Context and repository orientation

Before this change, `src/ThermalWatch.Core/GibsClient.cs` accepted any bounded response with a PNG signature after checking global exact-date domains; it did not inspect crop pixels. `src/ThermalWatch.Core/Models.cs` carried only preview bytes, so `src/ThermalWatch.Telegram/TelegramMessageFormatter.cs` could not describe a fallback base. Both automatic pending delivery and manual `send-top` already called the same `GibsClient.GetPreviewAsync` path.

Durable behavior is routed through `docs/domain/notification-policy.md`, `docs/components/telegram-notifier.md`, and `docs/operations.md`. Validation follows `docs/development.md`. Dependency direction remains `Api -> Telegram -> Core` and `Api -> Core`; no new package, environment variable, endpoint, or ADR is required.

## Progress

- [x] 2026-07-23 12:28 UTC: Inspected repository guidance, worktree, current GIBS preview flow, formatter, tests, and routed documentation.
- [x] 2026-07-23 12:38 UTC: Implemented exact-date base probes, deterministic fallback selection, PNG no-data inspection, and preview attribution.
- [x] 2026-07-23 12:38 UTC: Updated Telegram fallback caption formatting and added focused unit tests; 21 focused tests pass.
- [x] 2026-07-23 12:38 UTC: Synchronized notification policy, notifier component, and operations documentation.
- [x] 2026-07-23 12:38 UTC: Completed focused and full validation and visually inspected the public supplied-coordinate GIBS crop.
- [x] 2026-07-23 12:38 UTC: Reconciled the final diff and retrospective with no credentials or unrelated changes; the verified change is ready to commit and push to `main`.

## Surprises and discoveries

- A supplied NOAA-20/date/crop that had reportedly been black later returned normal true-color pixels from public GIBS. This supports a transient spatial-ingestion gap: global date availability can precede usable crop content.
- The public GIBS WMS response inspected during planning was an 8-bit, non-interlaced RGBA PNG. The implementation will also accept the equivalent RGB form and fail closed for unsupported probe encodings.

## Decision log

- **Decision (2026-07-23):** Preserve the representative thermal overlay and fall back only the contextual base. **Reason:** This keeps the notified anomaly evidence tied to its representative while fixing black context for single- and multi-satellite clusters. **Consequence:** Fallback captions must identify both layers.
- **Decision (2026-07-23):** Prefer the representative base, then the same sensor family, then the other family. **Reason:** Determinism and minimum semantic drift, while the user requested all supported satellites as eventual candidates. **Consequence:** MODIS tries its other platform before VIIRS; VIIRS tries other VIIRS platforms before MODIS.
- **Decision (2026-07-23):** Probe a 64 by 64 version of the exact requested crop and require at least half its pixels to be usable. Pixels with low alpha or all RGB channels at or below 8 are no-data. **Reason:** This matches the existing viewer's near-black classification while keeping probe payloads small and rejecting mostly blank crops. **Consequence:** Unsupported or mostly no-data probes are retried through existing policy.
- **Decision (2026-07-23):** Decode the bounded GIBS probe internally instead of adding an image package. **Reason:** The service is deliberately small and already contains bounded PNG decoding utilities. **Consequence:** Only verified 8-bit RGB/RGBA, non-interlaced probe PNGs are accepted.

## Concrete implementation steps

1. In `src/ThermalWatch.Core`, split base and overlay metadata into an ordered imagery-source catalog. Keep existing source/satellite aliases, choose pass-matched base products, and expose selected base attribution through `GibsPreview`.
2. In `GibsClient.GetPreviewAsync`, verify the representative overlay's exact date, probe candidate bases sequentially at 64 by 64 over the exact crop, and select the first base with at least 50 percent nontransparent/non-near-black pixels. Request the final configured-size WMS composite using that base followed by the representative overlay. Cache only successful previews with attribution; do not cache black probe outcomes.
3. Add bounded RGB/RGBA PNG parsing that validates signature, dimensions, bit depth, color type, compression/filter/interlace modes, chunk limits, decompressed scanlines, and the no-data percentage. Preserve cancellation and unavailable failure boundaries.
4. Pass `GibsPreview` rather than a Boolean into Telegram formatting. Preserve current matched wording; on fallback, render the selected base satellite/instrument and representative thermal-overlay satellite/instrument in both single- and multi-satellite templates.
5. Add fake-HTTP preview tests for selection order, layer composition, pixel thresholds, malformed data, caching, and recovery, plus formatter attribution tests. Update durable preview and failure documentation without changing docs routing or architecture prose.

## Validation and acceptance criteria

- `dotnet test tests/ThermalWatch.Tests.csproj -c Release --nologo --filter 'FullyQualifiedName~GibsClientPreviewTests|FullyQualifiedName~TelegramMessageFormatterTests'` passes without live providers.
- `dotnet restore ThermalWatch.slnx`, Release build, full Release tests, and `dotnet format ThermalWatch.slnx --verify-no-changes --no-restore` all pass.
- The focused `DocumentationValidationTests` command passes and the complete diff contains no credentials or unrelated changes.
- A public exact-date GIBS request for a supplied coordinate produces a preview that can be opened and has usable context. No live Telegram post is required or authorized by this validation step.
- Git history receives one focused commit and `origin/main` is updated only after validation and diff review.

## Recovery or rollback guidance

All edits are source, tests, and documentation with no migration or persistent state. Re-run failed checks after correcting only task-owned files. If interrupted, use this progress list and `git diff`; preserve unrelated work. Reverting the focused final commit restores prior behavior. Do not use destructive reset or checkout commands, and never place supplied credentials into files or commands.

## Outcomes and retrospective

The client now rejects base probes that are black, transparent, malformed, unsupported, or less than half usable; falls back deterministically across all supported same-date/pass bases; preserves the representative anomaly overlay; and carries selected-base attribution into accurate Telegram captions. Successful previews remain cached while rejected spatial probes can recover on a later snapshot. Focused tests passed 21 of 21, the full suite passed 54 of 54, documentation validation passed 7 of 7, Release build and formatting passed without warnings or changes, and the public NOAA-20 example crop was opened and found to contain usable daytime context with thermal markers.

Durable behavior was moved into the notification-policy, Telegram-notifier, and operations documents. Architecture, documentation routing, configuration, the anomaly API, and the viewer were intentionally unchanged. A remaining provider limitation is that same-date imagery is not exact-minute imagery; when every platform lacks usable crop pixels, the existing retry and timeout policy still applies.
