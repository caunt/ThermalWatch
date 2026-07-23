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
| [0002](0002-core-owned-notification-candidates.md) | Accepted | Keep notification candidate policy/lifecycle in Core and Telegram as a message/delivery adapter. |
| [0003](0003-core-owned-on-demand-nearby-context.md) | Accepted | Keep nearby mapped context on demand in Core and outside raw observations and notification policy. |

`0000-template.md` is a template and is not an architectural decision.
