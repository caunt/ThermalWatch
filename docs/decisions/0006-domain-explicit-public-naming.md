# 0006: Use domain-explicit public naming

## Status

Accepted

## Context

The public anomaly snapshot and shared notification configuration use names inherited from earlier implementation boundaries. [AnomalySnapshot](../../src/ThermalWatch.Core/AnomalySnapshot.cs) exposes generic item, count, and source-status concepts even though each value has a specific anomaly or country/source-segment meaning. [ApplicationConfiguration](../../src/ThermalWatch.Api/ApplicationConfiguration.cs) reads shared Core notification policy through `TELEGRAM_*` settings even though Viewer eligibility and diagnostics use the same policy without Telegram delivery.

Internal notification evaluation also uses accepted terminology for both policy eligibility and successful delivery. This makes logs and result contracts ambiguous and increases the chance that a consumer or maintainer assigns the wrong meaning to a value.

## Decision drivers

- Public names should communicate the domain value without requiring implementation knowledge.
- Core notification policy is shared by Viewer and Telegram and should not be named after one adapter.
- Eligibility, selection, and delivery are distinct states and need distinct names.
- Retaining aliases would preserve ambiguity and duplicate the versionless HTTP and environment contracts.
- The existing route shapes and scientific safety language remain useful and do not share the ambiguity.

## Considered options

Retain all public names and clean up only local variables. This avoids migration work but leaves the most consequential ambiguity in deployment and HTTP contracts.

Add new names while temporarily accepting or returning old aliases. This eases migration but creates duplicate sources of truth, precedence rules, and a longer-lived compatibility surface in a deliberately small service.

Replace the affected names in one coordinated migration. This requires consumers and deployments to update together but leaves one explicit contract.

## Decision

ThermalWatch uses anomaly for FIRMS record models and collections, segment for each country/source refresh status, eligible for notification content-policy acceptance, and delivered only after successful transport delivery. Observation remains the neutral scientific explanation of what an anomaly means, while detection count remains the policy and presentation term for cluster member counts.

The anomaly snapshot uses explicit anomaly, segment, country-code, and timestamp names. Shared notification policy and lifecycle configuration uses `NOTIFICATION_*`; only Telegram credentials retain `TELEGRAM_*`. The migration is a clean break: old JSON fields are not returned and old shared policy environment names are not accepted. The established `/api/anomalies` and `/api/telegram/send-top?count=5` route and query names remain unchanged.

## Consequences

HTTP consumers must adopt the replacement snapshot and manual-send response fields. Deployments must rename shared policy settings before adopting this revision; an old setting silently has no effect because application-specific configuration reads only exact recognized names.

Code, tests, logs, Viewer state, and current documentation share one vocabulary. Historical ADRs retain names that were accurate for their original decisions, with source links corrected when files move.

## Validation or evidence

- [Public contract naming tests](../../tests/PublicContractNamingTests.cs)
- [Notification option tests](../../tests/NotificationOptionsTests.cs)
- [Application configuration tests](../../tests/ApplicationConfigurationTests.cs)
- [Viewer controller](../../src/ThermalWatch.Viewer/wwwroot/app.js)

## Related source files and documents

- [Anomaly snapshot](../../src/ThermalWatch.Core/AnomalySnapshot.cs)
- [Application configuration](../../src/ThermalWatch.Api/ApplicationConfiguration.cs)
- [Operations](../operations.md)
- [Architecture](../architecture.md)

## Supersedes / Superseded by

- Supersedes: None.
- Superseded by: None.
