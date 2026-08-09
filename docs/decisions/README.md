# Architecture decision records

> **Purpose:** Define when ThermalWatch records an architecture decision and how those records are maintained.
> **Scope:** Durable, significant technical choices with meaningful alternatives or long-term consequences.
> **Sources of truth:** Accepted ADRs, their linked code and tests, and the current [architecture documentation](../architecture.md).
> **Update when:** ADR criteria, lifecycle, numbering, or registry entries change.

## When to write an ADR

Create an ADR when a choice is expected to outlive its implementation task and materially affects architecture, public contracts, data ownership, security, operational behavior, or dependency direction. The decision should have credible alternatives and consequences that future maintainers need to understand.

Do not create an ADR for routine implementation details, formatting choices, one-off task instructions, or facts that belong in another durable document. Record temporary implementation state in an [ExecPlan](../../.agent/PLANS.md), and record current system behavior in the relevant document routed through the [documentation index](../README.md).

Do not manufacture retrospective rationale. A past choice may be documented only when its context, drivers, and evidence can be established from authoritative repository sources.

## Numbering and lifecycle

- Copy [0000-template.md](0000-template.md) and assign the next unused sequential four-digit identifier, starting with `0001`.
- Name the file `NNNN-short-decision-title.md`; never reuse an identifier, including one belonging to a rejected or superseded ADR.
- Use `Proposed`, `Accepted`, `Rejected`, or `Superseded` as the status.
- Add every non-template ADR to the registry below in numeric order.
- Treat accepted and rejected ADRs as historical records. Correct typographical errors or broken links without rewriting their original context or decision.
- When an accepted decision changes, create a new ADR, mark the old one `Superseded`, and link both records through their `Supersedes / Superseded by` sections.

An ADR becomes accepted only when the implementation and cited validation support the decision. Keep implementation detail in source and tests; link to those sources instead of copying them into prose.

## Registry

| ADR | Status | Decision |
| --- | --- | --- |
| [0001](0001-server-mediated-viewer-imagery.md) | Accepted | Keep one Api host while Viewer owns assets/routes and Core mediates NASA imagery. |
| [0002](0002-core-owned-notification-candidates.md) | Superseded | Keep notification candidate policy/lifecycle in Core and Telegram as a message/delivery adapter. |
| [0003](0003-core-owned-on-demand-nearby-context.md) | Superseded | Keep nearby mapped context on demand in Core and outside raw observations and notification policy. |
| [0004](0004-snapshot-based-notification-evaluation.md) | Superseded | Reevaluate complete active snapshots without retaining unsent notification candidates. |
| [0005](0005-eligibility-based-startup-incident-suppression.md) | Accepted | Suppress only eligible startup incidents while leaving initially ineligible incidents retryable. |
| [0006](0006-domain-explicit-public-naming.md) | Accepted | Use domain-explicit HTTP, configuration, code, and diagnostic naming without compatibility aliases. |
| [0007](0007-rank-nearby-context-by-osm-tag-count.md) | Accepted | Rank shared nearby mapped context by OSM tag count before distance. |
| [0008](0008-use-in-memory-daily-firms-baseline.md) | Superseded | Use an in-memory 30-day daily FIRMS baseline for historical-FRP notification filtering. |
| [0009](0009-use-95th-percentile-historical-frp-threshold.md) | Superseded | Compare current total FRP with the historical 95th percentile instead of the maximum. |
| [0010](0010-require-substantial-historical-frp-excess.md) | Accepted | Require current total FRP to exceed both multiplier and offset thresholds derived from historical p95. |
| [0011](0011-use-ukraine-worldview-for-area-fallback.md) | Accepted | Use Natural Earth's Ukraine worldview for area-fallback country attribution. |

`0000-template.md` is a template and is not an architectural decision.
