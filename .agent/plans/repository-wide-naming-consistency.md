# Repository-wide naming consistency

## Purpose and observable outcome

Replace conflicting or misleading names across ThermalWatch with one explicit vocabulary. FIRMS records are anomalies in code and API collections; snapshot diagnostics are country/source segments; notification policy results are eligible or rejected; successful Telegram sends are delivered. Shared notification configuration uses provider-neutral `NOTIFICATION_*` environment names. The change intentionally replaces the affected HTTP JSON and environment contracts without compatibility aliases while preserving polling, clustering, filtering, Viewer, and delivery behavior.

## Context and repository orientation

The migration crosses `ThermalWatch.Core` models and engines, the `ThermalWatch.Api` configuration and snapshot endpoint, the Telegram adapter, and the Viewer JavaScript consumer. `AnomalySnapshot` currently exposes generic `Items`, `Count`, and `Sources`; `SourceStatus` mixes `Country` with the model's `CountryCode`; FIRMS anomaly collections are usually called detections; notification eligibility uses accepted terminology; and `AcceptedClusterCount` actually counts successful delivery. Shared policy settings are parsed from `TELEGRAM_*` variables even though Viewer diagnostics consume the same Core policy. Durable references are `README.md`, `docs/architecture.md`, `docs/operations.md`, `docs/domain/notification-policy.md`, all three component documents, and the ADR registry. The worktree was clean and the baseline Release test run passed all 158 tests on 2026-07-24.

## Progress

- [x] Rename Core/API contracts, configuration, and internal anomaly terminology. (2026-07-24T10:38:33Z)
- [x] Update Telegram and Viewer consumers plus all affected tests. (2026-07-24T10:38:33Z)
- [x] Add the naming rule, current documentation, and ADR 0006. (2026-07-24T10:38:33Z)
- [x] Complete focused, full, publish, and live visual validation. (2026-07-24T10:53:22Z)
- [x] Reconcile durable documentation and close this plan. (2026-07-24T10:53:22Z)

## Surprises and discoveries

- 2026-07-24: `AcceptedClusterCount` is incremented only after `NotificationDeliveryOutcome.Delivered`; the existing log text already labels it delivered.
- 2026-07-24: `preview` and Viewer `imagery` are intentionally distinct contracts, and FIRMS/GIBS/VIIRS capitalization already follows prose versus .NET conventions; these names must remain unchanged.
- 2026-07-24: Historical ADRs mention retired configuration names. They remain historically accurate and are excluded from active-vocabulary cleanup.
- 2026-07-24: Contract and configuration theories increase the .NET suite from 158 to 214 tests by checking every new shared notification name and every former Telegram policy name.
- 2026-07-24: A full UKR/RUS live pass rendered 13,490 anomalies in both desktop providers, but Google layer teardown exceeded the two-minute browser action timeout. The completed provider round-trip and responsive inspection used the same live credentials with `FIRMS_COUNTRIES=UKR`, yielding 22 active anomalies without changing `.env`.

## Decision log

- 2026-07-24: Use anomaly for model instances and collections. Keep observation in scientific/user-facing explanations and detection count in policy, configuration, and message language.
- 2026-07-24: Make the snapshot JSON explicit: `configuredCountryCodes`, `segments`, `anomalyCount`, and `anomalies`; segment fields use `countryCode`, `lastAttemptAtUtc`, `lastSuccessAtUtc`, and `isStale`.
- 2026-07-24: Move every shared policy/lifecycle setting to `NOTIFICATION_*`; retain only Telegram credentials under `TELEGRAM_*`. Use `NOTIFICATION_SEND_EXISTING_ON_STARTUP` and `NOTIFICATION_EPISODE_RETENTION` for the two semantically misleading legacy names.
- 2026-07-24: Make a clean break with no old environment aliases or duplicate JSON fields. Preserve `/api/anomalies` and `/api/telegram/send-top?count=5` as explicit route/query exceptions.
- 2026-07-24: Record the durable public-contract and configuration choice in ADR 0006 because it affects HTTP consumers and deployments.
- 2026-07-24: After complete validation, commit the finished migration and push `main` to `origin` as explicitly requested by the user.

## Concrete implementation steps

1. Rename snapshot and segment records, FIRMS result collections, query/store consumers, timestamps, country-code collections, files, and local/test anomaly variables. Keep policy-facing detection-count terminology and deliberate explanatory observation wording.
2. Replace accepted policy terminology with eligible, count successful automatic sends as delivered, and make automatic/manual candidate method and result names explicit. Rename manual response fields to cluster-specific counts and IDs without changing its route or query parameter.
3. Replace shared recognized `TELEGRAM_*` policy parsing with `NOTIFICATION_*`, update option names and safe validation messages, and keep only `TELEGRAM_BOT_TOKEN` and `TELEGRAM_CHANNEL_ID` in Telegram configuration. Do not edit the ignored `.env` or add legacy fallbacks.
4. Update the Viewer controller for the new snapshot wire contract, then update all C#, Node, serialization, endpoint, configuration, and logging tests. Add assertions that old snapshot fields and old recognized policy names no longer work.
5. Add the canonical vocabulary to `AGENTS.md`; synchronize the root README and routed architecture, operations, domain, FIRMS, Telegram, and Viewer documents. Add accepted ADR 0006 and its registry entry without rewriting historical ADRs.
6. Run all focused and complete validation. Publish the host, run it with the ignored credentials file without exposing values and with Telegram disabled, capture/open required NASA and Google desktop/narrow screenshots plus provider round-trip, record evidence here, then commit and push the verified change to `origin/main`.

## Validation and acceptance criteria

Run the Viewer Node syntax/tests, focused configuration/endpoint/documentation tests, then the complete restore, Release build/test, format verification, `git diff --check`, and Release publish/static-asset assertion. The new HTTP serialization must contain only the explicit anomaly/segment and cluster-response names. Active source/current docs must contain no retired shared policy environment names, excluding historical ADRs and deliberate tests proving they are ignored. With the existing ignored mode-600 `.env`, run the published host with Telegram credentials removed; capture and open NASA and Google at 1440x900 and 390x844 plus a provider round trip, verifying unchanged markers, selection, diagnostics, layout, and responsiveness.

Completed evidence on 2026-07-24:

- `dotnet restore`, Release build, all 214 .NET tests, and `dotnet format --verify-no-changes` passed; the build reported zero warnings and zero errors.
- Both Viewer JavaScript syntax checks and all 35 Node tests passed. All 7 documentation validation tests passed.
- Release publish completed, the published `wwwroot/index.html` assertion passed, and `git diff --check` was clean.
- The live published host ran with Telegram credentials removed and the ignored `.env` unchanged. Browser assertions verified the new `anomalies`/`anomalyCount` wire fields, selection diagnostics, no page errors, no horizontal overflow, and no direct browser requests to FIRMS or GIBS hosts.
- NASA desktop, Google desktop, NASA after the provider round trip, NASA narrow, and Google narrow screenshots were captured under `/tmp/thermalwatch-naming-visual.sPETv6/` and each was opened and visually inspected.

## Recovery or rollback guidance

All repository changes are source-only and repeatable. Use this plan and `git diff` to resume partial work, and fix forward after mechanical renames rather than discarding unrelated changes. Do not modify, print, delete, or commit `.env`. Deployments must rename their environment settings when adopting the resulting revision. The only external write is the requested final Git push after validation; if it fails, retain the local commit and report the remote error without rewriting history.

## Outcomes and retrospective

ThermalWatch now uses one explicit vocabulary across Core, Api, Telegram, Viewer, logs, tests, and current documentation. Snapshot and manual-send JSON contracts expose domain-specific names, shared policy configuration is provider-neutral, and eligibility is distinct from delivery. Contract tests prevent the retired fields and environment aliases from returning.

The complete automated and live visual validation passed. ADR 0006 records the clean-break contract decision as accepted, historical ADR wording remains intact, and the existing ignored `.env` retained its original size, mode, timestamp, and checksum. The verified repository state is ready for the requested commit and push to `origin/main`.
