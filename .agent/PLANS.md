# ThermalWatch ExecPlans

> **Purpose:** Define the self-contained working-plan format for complex or resumable changes.
> **Scope:** Complex features, significant refactors, migrations, risky work, and changes spanning several components.
> **Sources of truth:** The governing active plan, [repository guidance](../AGENTS.md), [documentation index](../docs/README.md), and the code, tests, and executable configuration linked by the plan.
> **Update when:** ExecPlan criteria, required sections, maintenance rules, or storage conventions change.

## When an ExecPlan is required

Use an ExecPlan for a complex feature, significant or cross-component refactor, migration, risky operational change, or any task likely to be resumed by another stateless agent. A focused, low-risk change that can be completed and validated in one session does not need one.

Store each active plan at `.agent/plans/descriptive-kebab-case.md`. Do not create the directory when no active plan exists. Plans are working documents, not permanent architecture records.

## Operating rules

An ExecPlan must be self-contained: a new agent should be able to continue using the repository and the plan without chat history. Write repository-relative paths, exact commands, current constraints, and observable acceptance criteria. Never include secrets, credentials, personal data, transient logs, or unsupported claims.

Maintain the plan continuously while work proceeds:

- Update progress whenever a step starts, completes, or changes.
- Record discoveries and decisions when they occur, including evidence and consequences.
- Reconcile the concrete steps and validation criteria when discoveries change the approach.
- Keep incomplete work and blockers explicit; do not describe intended work as complete.
- Before closing the plan, move durable system facts into the appropriate project document and qualifying architectural rationale into an ADR.

## Required format

Every plan must contain all sections below.

### Purpose and observable outcome

Explain why the work matters and what a user or operator will be able to observe when it is complete. Define scope boundaries where needed to prevent accidental expansion.

### Context and repository orientation

Describe the relevant current behavior, component boundaries, important files or symbols, and prerequisites. Link to source-of-truth files and only the durable documentation needed for the task.

### Progress

Use a checklist of concrete milestones. Add UTC timestamps to completed or materially revised entries so another agent can distinguish current state from original intent.

### Surprises and discoveries

Record unexpected behavior, constraints, failed assumptions, and evidence. State `None so far` only while there are no discoveries.

### Decision log

For each nontrivial implementation decision, record the decision, reason, date, and consequences. Escalate a durable decision that meets the [ADR criteria](../docs/decisions/README.md) into a separate ADR.

### Concrete implementation steps

Give an ordered, decision-complete sequence with repository-relative locations, intended behavior, interfaces, data flow, and important failure handling. Include safe checkpoints for risky work.

### Validation and acceptance criteria

List exact commands and expected observable results. Cover focused tests, complete solution validation, documentation validation, relevant manual checks, and proof that unrelated application behavior remains unchanged.

### Recovery or rollback guidance

Explain how to stop safely, retry idempotently, recover partial work, and reverse risky changes without discarding unrelated user work. Identify irreversible operations explicitly.

### Outcomes and retrospective

At completion, summarize delivered behavior, validation evidence, remaining limitations, and lessons that should affect future work. Until then, state that the section will be completed with the implementation.

## Closing a plan

A plan is complete only when its observable outcome and acceptance criteria are satisfied, its progress and retrospective match repository state, and durable knowledge has been routed to the proper documentation. Completed plans may be removed after durable knowledge is preserved; Git history remains the record of the working document.
