# NetAgents analyzer migration

## Purpose and observable outcome

Install NetAgents.Analyzers in place of Meziantou.Analyzer in all projects, remove the root .editorconfig, and pass the complete repository verification on the latest origin/main.

## Context and repository orientation

Directory.Build.props supplies shared project settings; Directory.Packages.props supplies versions. NetAgents applies source fixes during builds and enforces additional design rules. Consult docs/README.md, docs/development.md, and .agents/skills/maintain-project-docs/SKILL.md. Docker provides the .NET 10 SDK on this host.

## Progress

- [x] 2026-10-11 Fast-forwarded main to e23c805, preserving task changes and upstream package updates.
- [x] 2026-10-11 Removed .editorconfig and replaced the central analyzer reference/version.
- [x] 2026-10-11 Inspected diagnostics in surrounding code and applied individually authored fixes; Release compilation has no warnings or errors.
- [x] 2026-10-11 Restore, Release build, all 330 .NET tests, all 36 Node tests, format verification, publish/static-file assertion, live viewer screenshots, and diff checks passed.
- [x] 2026-10-11 Reconciled analyzer and test-runner guidance, corrected moved-file documentation links, and resolved this plan.

## Surprises and discoveries

The initial fetch failed due to missing local Git objects; git fetch --refetch succeeded. NetAgents builds rewrite source files and require public XML documentation, dedicated result types, and asynchronous coordination.

Upstream xUnit 4 uses Microsoft.Testing.Platform, which requires the .NET 10 test-runner selection in global.json and explicit --solution/--project command arguments. The file selects only the runner, preserving SDK selection. Regression tests found nullable preview conversion could produce an empty non-null memory payload; constructor control flow now preserves absence, and delivery requires nonempty image bytes. Final review found cancellation could interrupt discussion-waiter cleanup; cleanup now acquires its gate without the caller token, with a retry-after-cancellation regression test.

## Decision log

- Preserve upstream dependency updates while replacing only the requested analyzer. User requested fast-forward and continued migration.
- Remove the old AnalysisMode override so the package can enforce its shared analysis policy.

## Concrete implementation steps

1. Restore with the official .NET 10 SDK container.
2. Build, inspect diagnostics and source rewrites, then make minimal behavior-preserving fixes.
3. Update development guidance and agent rules to agree with the installed analyzer and deleted editor configuration.
4. Run all documented validation and review the final diff.

## Validation and acceptance criteria

Run dotnet restore ThermalWatch.slnx; dotnet build ThermalWatch.slnx -c Release --no-restore --nologo; dotnet test --solution ThermalWatch.slnx -c Release --no-build; dotnet format ThermalWatch.slnx --verify-no-changes --no-restore in the SDK container. All must pass. Run the documented Node checks, publish/static-file assertion, and browser screenshot verification. Run git diff --check and inspect changes for preserved runtime behavior and absence of credentials.

## Recovery or rollback guidance

Stop the task's container before changing inputs. Preserve the ignored .env. Task changes can be restored from Git without undoing the fast-forward; a pre-fast-forward task stash is retained until migration completion. Never discard unrelated changes.

## Outcomes and retrospective

The migration is complete on the fast-forwarded upstream main. Diagnostics were resolved through individually authored edits after reading surrounding code; no bulk source-rewriting script or analyzer suppression was used for those fixes. NetAgents itself reformats code during builds. Feature directories satisfy its directory limits while preserving Core's namespace and dependency direction. Named results, typed URI/image contracts, explicit resource ownership, and asynchronous coordination satisfy its design rules. Regression coverage preserves preview absence and cancelled-waiter cleanup. Restore, warning-free Release compilation, all 330 .NET tests, all 36 Node tests, documentation validation, and format verification pass. Published viewer assets and live NASA/Google desktop and narrow views were inspected, including selection and provider roundtrip; no horizontal overflow or direct NASA browser requests were observed. Temporary credentials remained ignored and Telegram was disabled during live checks. Durable documentation now describes the analyzer and upstream test runner and resolves moved-file references.
