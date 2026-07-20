---
name: maintain-project-docs
description: Maintain repository documentation alongside code changes. Use when a task changes public or externally observable behavior, API contracts, architecture or component boundaries, domain rules, configuration or environment variables, build/test/deployment/operational workflows, dependencies with architectural consequences, or recurring repository instructions; also use for documentation audits and drift correction.
---

# Maintain Project Documentation

Keep durable documentation aligned with verified repository behavior without turning it into a change diary.

## Workflow

1. Read the root `AGENTS.md` and [documentation index](../../../docs/README.md).
2. Review the complete code diff, including staged and unstaged changes, then inspect the affected source, tests, executable configuration, and existing documentation.
3. Classify the documentation impact using the placement guide below. Treat no-impact as a conclusion that requires evidence, not a default.
4. Locate the affected documents through `docs/README.md`. Update existing documents instead of appending chronological notes.
5. Correct or remove stale statements. When code and documentation disagree, verify the behavior from code or executable artifacts and fix the documentation.
6. Create an ADR only when the criteria in `docs/decisions/README.md` are met. Do not create one for a routine implementation detail.
7. Update `docs/README.md` when adding, removing, or changing the purpose of a routed document.
8. Check local links and source references, then run the documentation validation command from `docs/development.md` plus the validations required by the code change.
9. Report documentation consulted, updated, created, removed, or intentionally left unchanged, with a short reason for intentional omissions.

## Knowledge placement

| Knowledge | Destination |
| --- | --- |
| Repeated agent behavior or repository-wide rule | Root `AGENTS.md` |
| Specialized rule for one directory | Nearest nested `AGENTS.md`, only when that directory genuinely differs |
| Current durable project behavior | Relevant document routed by `docs/README.md` |
| Significant decision and rationale | Numbered ADR |
| Temporary implementation state or resumable work | ExecPlan under `.agent/plans/` |
| Reusable task procedure | Repository skill |
| Obvious implementation detail | Code, test, or focused comment |

## Quality rules

- Keep `AGENTS.md` below its enforced line limit and move specialized detail into routed documents.
- Document intent, boundaries, contracts, invariants, failure behavior, and operational consequences.
- Link to source-of-truth files, types, endpoints, tests, workflows, or generated artifacts instead of copying code, schemas, or complete API definitions into prose.
- Preserve useful structure and rewrite stale sections in place. Do not add release notes or diary-style entries to durable documents.
- State uncertainty explicitly. Do not present live external state, historical rationale, or inferred behavior as verified fact.
- Never record secrets, tokens, credentials, personal data, transient logs, or sensitive values.
- Keep temporary details in the active ExecPlan. Move only durable outcomes into project documentation or ADRs.
