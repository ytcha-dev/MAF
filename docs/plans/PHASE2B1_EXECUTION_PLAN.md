# Phase 2B-1 execution plan

Authorized scope: durable local execution and explicit restart reconciliation, including the owner's amended read-only live acceptance. No approvals UI, registry, automatic specialist pipeline or Hybrid work.

**2026-10-15 security hold:** Symantec terminated testhost during the baseline. Subsequent owner instruction permits source/test authoring and static review only. Build, restore, tests, new binaries and live execution remain paused pending explicit clearance. Earlier live authorization does not override this hold. See [security event](../acceptance/PHASE2B1_SECURITY_EVENT.md) and [pending results](../acceptance/PHASE2B1_RESULTS.md).

## Baseline

- Base: `036cba8`; .NET SDK 10.0.103, Agent/Workflow SDK 1.24.0. Existing native PCA/Review and CLI output contracts remain unchanged.
- SQLite feasibility: Microsoft.Data.Sqlite 10.0.0 with explicit SQLitePCLRaw.bundle_e_sqlite3 3.0.5 to avoid the observed NU1903 dependency. Use configured NuGet feeds without policy changes.
- Earlier pre-event inspection found localhost:4096 listening; current health is unverified. Do not stop/restart or contact the shared server during the security hold. Any future live work needs renewed execution clearance and dedicated test-owned Host processes.
- Baseline/final tests must distinguish offline fixtures from real OpenCode. Final acceptance requires a real Host restart and unchanged synthetic source/index/status.

## Design and sequence

1. Core: provider-neutral durable snapshot, submission phase, validated transitions and IExecutionStore. Persist identity/fingerprint, scope, root, timestamps and bounded diagnostics; never persist prompts, summaries, tool transcripts or credentials.
2. SQLite project: schema version 1, explicit local path, short transactions, primary key plus optimistic version checks. Per-task exclusive OS file ownership releases on process death; no database transaction spans HTTP. Temporary databases are allowed in automated tests; Host rejects Temp/target-repository persistent paths.
3. Reattach-capable provider contract: restore an existing handle only after current policy validation. Preallocate stable request correlation, persist session identity before submit, commit submission intent before the one prompt POST. A lost create-session response remains Unknown; no replacement session is invented.
4. Durable application service and client decorator: use the existing official Workflow/Executor. Persist lifecycle boundaries; recovery bypasses create/submit and observes the original handle. Terminal metadata survives restart; response bodies remain at the provider and are not stored locally.
5. Explicit Host database configuration and recovery commands; keep json/text/verbose and existing status/exit codes. In durable mode a waiting-budget timeout returns recoverable Unknown without abort; explicit caller cancellation still requests abort. The original ephemeral Phase 2A path keeps its behavior.
6. Fault-injected SQLite/provider tests, independent-service and separate-process recovery; then build/offline regression and authorized live restart/unavailability/reconciliation. Record actual failures and limitations in PHASE2B1_RESULTS and ADR-004. Commit by verified milestones.

## Critical boundaries

- Crash after local Created, before/during session creation, after session response but before durable identity, after submission intent, after accepted POST with lost response, and before durable final outcome.
- Unknown is recoverable, not confirmed failure. Intent does not prove a POST occurred, and HTTP timeout/abort acknowledgment does not prove cancellation.
- Existing TaskId with another fingerprint/scope is rejected. A competing owner cannot submit; stale versions and terminal-state rewrites are rejected.
- Recovery must never POST a session or prompt. If session/correlation is unavailable or mismatched, retain identity and report Unknown.
- SQLite cannot provide atomic remote delivery or distributed exactly-once. The first scope is one local host and bounded explicit recovery.

References: [Roadmap](../ROADMAP.md), [2A evidence](../acceptance/PHASE2A_RESULTS.md), [SQLite smoke](../acceptance/SQLITE_SMOKE_RESULTS.md), [ADR-003](../architecture/ADR-003_OPENCODE_WORKFLOW_EXECUTOR.md).
