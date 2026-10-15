# Phase 2A P0 — verified API and minimal design delta

> Historical P0/P1 snapshot from 2026-10-14: statements such as "no external executor yet" and pending gates below describe that checkpoint. Phase 2A is now implemented and accepted; see [actual acceptance](../acceptance/PHASE2A_RESULTS.md) and [ADR-003](../architecture/ADR-003_OPENCODE_WORKFLOW_EXECUTOR.md). Later work follows [Roadmap v0.2](../ROADMAP.md), not the original P0 next actions.

Date: 2026-10-14. The three planning documents already exist in the local repository. Existing Core / Comprehension / Review / Host / Tests remain the baseline; there is no external executor or Workflow package reference yet.

## SDK evidence

Agent SDK is pinned to Microsoft.Agents.AI 1.24.0. Restored matching Microsoft.Agents.AI.Workflows 1.24.0 in an ignored, self-authored SDK probe. A compiled `Executor<string,string>` override of `HandleAsync(string,IWorkflowContext,CancellationToken)` executed through `WorkflowBuilder(executor).WithOutputFrom(executor).Build()` and `InProcessExecution.RunAsync`. Observed WorkflowStartedEvent, ExecutorInvokedEvent, ExecutorCompletedEvent and WorkflowOutputEvent with the expected typed output. The official run exposes `SessionId`; it is separate from AgentSession and OpenCode session IDs.

References: [official executors](https://learn.microsoft.com/en-us/agent-framework/concepts/workflows/executors), [Workflow package](https://www.nuget.org/packages/Microsoft.Agents.AI.Workflows/1.24.0). Installed package XML and the compiled probe take precedence over pseudocode in planning documents.

## Provider evidence

Initial localhost:4096 probes failed with connection refused, including outside the sandbox. User explicitly authorized starting an installed native test server. Native executable and `GET /global/health` both report **1.18.35**. Server PID and the independent, test-owned temporary Git fixture are recorded locally in ignored TestResults; no existing server was stopped. Its OpenAPI document advertises 162 paths. The first agent query timed out during cold initialization; the next bounded query returned the configured read-only agent.

| Operation | Verified OpenCode 1.18.35 route / shape | Decision |
|---|---|---|
| Health | GET /global/health → healthy, version | Fail before launching on unavailable/unhealthy server |
| Schema | GET /doc → OpenAPI JSON | Require relevant capabilities before submission |
| Create | POST /session?directory=…; body supports agent, title, metadata, permission: PermissionRuleset | New session for each workflow; deny by default; verify returned rules and directory |
| Session | GET /session/{sessionID}?directory=… | Confirm identity and permission boundary |
| Submit | POST /session/{sessionID}/prompt_async; messageID, agent, parts; 204 | Submit once; never blindly retry ambiguous POST |
| Observe | GET /session/status?directory=… → busy/retry/idle map | Absent status alone does not prove completion |
| Messages | GET /session/{sessionID}/message?directory=…&limit=100[&before=…] | Match assistant.parentID to submitted messageID; require completed time and terminal finish |
| Approval/input | GET /permission and GET /question, sessionID fields | Surface waiting; never answer or approve automatically |
| Abort | POST /session/{sessionID}/abort → boolean | An acknowledgment alone is insufficient terminal evidence |

Message pagination uses `before` on this route; alternate `/api/session/...` cursor endpoints exist but are not required. MVP will bound collection and mark Partial on page/output limits. HTTP Basic authentication is supported by the documented server password/username; this test server's health/schema are reachable without credentials. Credentials must never appear in diagnostics.

The older sibling OpenCode-Agent-Adapter exists and was inspected. Its status/directory/message concepts are reusable, but it uses a synchronous message worker, persistence and WebUI tools outside this phase's scope. Do not import its host, queue or persisted state. Use direct asynchronous API with a new minimal adapter. [Official OpenCode server reference](https://opencode.ai/docs/server/).

## Design before implementation

- Core: typed request, handle, observation, result, execution policy and external client contract; no provider JSON or Workflow SDK dependency.
- `MAF.Workflows.External`: official OpenCodeExecutor and Workflow facade, single-flight in-memory mapping, bounded lifecycle; provider-specific HTTP adapter in its own namespace/folder.
- Host: `--external-task` branch before AI model setup, existing DI and Console retained. Explicit approved URL/root configuration. No extra orchestrator model call.
- Tests: in-memory fake provider, scripted HTTP handler and opt-in live fixture validation. No `.ps1`, no external task before enforced permission verification.

No core PCA/Review behavior changes, FileWriter, Shell Tool, second Agent Loop, A2A or Hybrid planner. Read-only session rules deny unknown tools, shell, edits, subagents, network and external directory access; secret/metadata reads are additionally excluded. The provider's returned session rules and agent permissions are checked, and live tests verify source/index hashes. This is harness policy enforcement, not an OS sandbox or universal data-leak guarantee. Trace and limits are sanitized; no raw HTTP error bodies, config credentials or tool payloads enter reports.

P0 API mapping is established. P1 must pass before HTTP delegation; P4 remains contingent on enforced policy verification and three actual, no-write runs.

P1 validation: 11/11 Fake Provider / official Workflow tests passed (2026-10-14). Covered serialization, actual WorkflowOutputEvent, running/terminal/waiting/partial/unknown states, offline behavior, duplicate request single-flight, ambiguous submit + read reconnection, timeout abort confirmation and caller cancellation. No model calls were made. TRX is retained locally at `tests/MAF.Tests/TestResults/phase2a-p1.trx`.
