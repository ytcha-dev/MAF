# Phase 2A — External Harness Workflow-first Execution Plan

> Version: 0.1 · 2026-10-14 · Status: Ready for implementation planning  
> Prerequisite: `docs/ROADMAP.md`  
> Architecture: `docs/architecture/EXTERNAL_HARNESS_ARCHITECTURE.md`

## Objective

Prove **one deterministic end-to-end delegation**:

`CLI → official Microsoft Agent Framework Workflow → OpenCodeExecutor → OpenCode Server → typed ExecutionResult → CLI`.

No additional orchestrator LLM call is required. OpenCode's own harness, rather than MAF, performs its internal task. The first success criteria concern **correct lifecycle integration**, not coding output quality.

## Constraints and assumptions

- Use the existing C#/.NET solution, DI and conventions. Inspect pinned MAF SDK APIs before choosing base classes or method signatures.
- Existing PCA and Review Agent must keep working; don't casually refactor them.
- OpenCode server is external and may already run on the developer's machine. **Do not auto-install/start/stop it** without user approval.
- OpenCode endpoints may differ across versions; verify the running server's actual API/schema and official documentation. Treat endpoint examples as tentative.
- Use only a disposable, explicitly approved test repository and least-privileged OpenCode profile. No production/company source sent to unapproved endpoints.
- First tasks must be **read-only**. OpenCode's tool permission policy must enforce this; wording in the prompt is insufficient protection.
- Do not create a local `IFileWriter` as a prerequisite. OpenCode owns its file operations.
- `.ps1` may be blocked by company policy: use C#, `dotnet`, or Python for cross-platform test harnesses when appropriate.

## Scope of MVP

### Must have

1. Validate reachable OpenCode server (and authentication if enabled).
2. Create/reuse an OpenCode session for exactly one workflow run.
3. Submit a task and correlate the request with its remote Session ID.
4. Track progress via a supported status/polling or event mechanism, without blocking indefinitely.
5. Produce typed result containing status, trace ID, external ID, summary, limitations/errors; optionally diff/artifacts when actually available.
6. Support CancellationToken and attempt remote abort; confirm or report uncertain final status.
7. Handle server unreachable, invalid responses and partial/unknown completion explicitly.
8. Unit/contract tests with fake HTTP/API; one opt-in live integration test.
9. CLI command such as `--external-task` or `--workflow` implemented without breaking existing `--ask`, `--review`, `/reset` behavior.
10. Documentation and measured validation report.

### Not in this phase

- Automatic source edits, automatic PR/commit, automatic approval or reviewer feedback loop.
- PCA → OpenCode → Review pipeline (reserved for after first gate).
- A2A gateway or extra service process; arbitrary remote shell bridge.
- Web UI, Open WebUI connector, RAG ingestion, distributed queue, database persistence.
- LLM planner, recursive delegation, unbounded retry, intelligent agent routing.

## Work packages

### P0 — Repository and API discovery (no implementation yet)

- Inspect solution tree, `Program.cs`, `IAgentFactory`, existing DI, agent classes, tests, security guardrails.
- Confirm installed/pinned Microsoft Agent Framework workflow package and **compilable C# API**, including `WorkflowBuilder`, custom Executor and execution entry point.
- Confirm OpenCode version, enabled API endpoints, session creation, asynchronous prompt submission, event/status semantics, message collection, abort and auth; note any unsupported feature.
- Verify whether existing adapter from earlier experiments can be reused, without assuming it is present in this repository.
- Produce a short design delta and API matrix before writing.

**Gate P0:** All required provider operations have a verified API mapping or an explicitly documented fallback. If key operations are unsupported, stop and present options rather than invent code.

### P1 — Contracts and fake implementation

Propose minimal stable contracts (names illustrative, not mandated):

- `ExternalTaskRequest`: TaskId, ProjectRoot, TaskText, ExecutionPolicy, RequestedCapabilities, Timeout.
- `ExternalExecutionHandle`: ExecutionId, Provider, ExternalSessionId.
- `ExternalTaskResult`: Status, Summary, Handle, Artifacts, Diagnostics/Limitations.
- `IExternalHarnessClient` (or provider-specific API layer): start, query, collect result, abort.

Implement an in-memory fake provider that can simulate running, completion, failure, cancellation, and ambiguous network disconnect. Keep provider payloads out of generic Core types.

**Gate P1:** Tests demonstrate status transitions and result serialization without any real LLM calls.

### P2 — OpenCode transport adapter

- Implement typed HTTP client with configurable base URL/auth (never hardcode credentials).
- Confirm allowlisted destination host, HTTPS except an explicitly approved localhost-only HTTP development configuration; avoid arbitrary user-supplied URL as agent tool input.
- Support CreateSession, SubmitTask, QueryStatus, ReadMessages/Artifacts, Abort using verified endpoints.
- Use HTTP timeouts, bounded polling/backoff and response size limits.
- Handle permissions/input-needed states without auto-approving.
- Maintain single-flight request identity; never blindly resubmit after ambiguous network failure.
- Redact secrets in logs, responses and serialized diagnostics.

**Gate P2:** Fake API tests cover pagination/event loss, invalid JSON, 401/403, 404, 429/5xx, timeout and unknown state (where applicable).

### P3 — MAF Workflow integration

- Create a true MAF Workflow containing a custom `OpenCodeExecutor` step. **Do not** substitute a hand-written method chain while calling it a MAF Workflow.
- Do not add `OpenCodeAgentFactory` or instantiate an extra `ChatClientAgent` unless a validated need emerges.
- Encode typed input/output routing; keep provider-session state separate from MAF AgentSession.
- Wire CLI and DI registrations. Show the workflow graph and events in local diagnostics.

**Gate P3:** Unit test proves execution actually flows through the official Workflow engine and returns the typed executor output.

### P4 — Live read-only E2E

- Run against explicitly permitted OpenCode instance and throwaway repository.
- Ask a bounded task: summarize repository structure and cite filenames; forbid tool writes with **enforced permissions**.
- Repeat at least 3 times, recording latency, completion status, session identity, result quality notes, HTTP/API errors and model calls if available.
- Record baseline content hashes / `git status` before and after to validate no unexpected writes.
- Do not automatically retry an ambiguous submission.

**Gate P4:** Three read-only runs completed with correct session mapping and zero unauthorized file mutations; any deviation explicitly investigated.

### P5 — Resilience validation and delivery

Required scenarios:

| Case | Expected behavior |
| --- | --- |
| OpenCode offline / host unreachable | Typed failure, no hanging, actionable diagnostic without secrets |
| Slow remote job | Running observed, budget/timeout honored |
| Cancellation | Abort attempt recorded; final state confirmed or `Unknown`, never fabricated `Cancelled` |
| Connection loss after prompt submit | Reconcile original session; no blind re-submit |
| Permission request | Surface `WaitingForApproval` or equivalent, no silent grant |
| Malformed/incomplete result | `Partial` or `Failed` with limitations |
| Zero changed files | Valid result; do not assume coding failure |
| Concurrent requests | Isolation of task/session IDs, bounded parallelism |
| CLI regression | PCA and Review existing commands still work |

**Gate P5:** Evidence includes `dotnet build`, `dotnet test`, fake provider suite, three live runs, negative/failure scenarios, no-write checks, and a written known-limitations section. If real server access unavailable, mark live acceptance **Not Run**, not Passed.

## Expected deliverables

1. Minimal C# adapter/client and custom Workflow executor with tests.
2. CLI invocation sample and config template without credentials.
3. `docs/acceptance/PHASE2A_RESULTS.md` with date, SDK/server versions, actual commands, passes/failures, run IDs (sanitized), unknowns and exact scope.
4. Updated README linking the run instructions.
5. Architecture decision note explaining why direct API + executor was selected over A2A and tool-wrapped OpenCode.

## Codex working instructions

- Read `docs/ROADMAP.md`, this plan, and `docs/architecture/EXTERNAL_HARNESS_ARCHITECTURE.md` before code changes.
- Treat repository/installed SDK as source of truth for types. Do not invent framework method signatures.
- Start with P0 and report deviations before implementation when major assumptions fail.
- Work incrementally; avoid broad renames/refactors of existing PCA/Review Agent.
- Never weaken an existing security guard to pass a test.
- No unapproved write-capable live testing or disabling corporate controls.
- Honor the repository's existing Git conventions and commit policy; stage only task-related changes. Do not commit secrets or generated outputs.
- Present clear human-readable C#, no compressed multi-statement lines; run scoped formatting and tests.
- Clearly distinguish executed vs skipped tests; never claim a result based on a fixture alone as live model validation.

## Phase exit / handoff

**When Phase 2A passes**, prepare Phase 2B hardening (durability, approvals and reconciliation) and only then a deterministic PCA → OpenCode → Reviewer pipeline. The LLM-controlled Hybrid path is **Phase 3**, not an MVP feature.
