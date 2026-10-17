# Phase 2B-4A — minimal deterministic multi-agent typed handoff

Date: 2026-10-17. Baseline: `32ec0e9cb7d5f617e2d424798aada6a45a1a34e1`.
Scope: actual official Workflow execution, controlled offline read-only proof.
No production Host activation, live provider or human approval. The new graph has
no SQLite integration; historical regression fixtures use disposable synthetic databases.

## Implemented components and graph

| Component | Ownership and implementation |
| --- | --- |
| [Core contracts](../../src/MAF.Core/TypedHandoffContracts.cs) | Immutable task, TaskBrief proposal, TaskEvidence, ReviewAssessment, WorkflowOutcome and stage/gate enums; ITaskBriefing and bounded independent validation |
| [PCA adapter](../../src/MAF.Agents.Comprehension/TaskBriefingAdapter.cs) | Existing ComprehensionAgentFactory/official ChatClientAgent and the same ProjectTools root; bounded strict JSON translation, not another agent loop |
| [Workflow composition](../../src/MAF.Workflows.External/ControlledHandoffWorkflow.cs) | Internal test-owned four-executor graph, typed edges, bounded waits, exact correlation, failure propagation and final gate |
| [Acceptance tests](../../tests/MAF.Tests/TypedHandoffTests.cs) | Scripted IChatClient, synthetic whole-task delegation and separate simulated evidence reviewer; manual timer expiration for stage timeouts |

```mermaid
flowchart LR
    U[HandoffTask] --> P[PCA]
    P -->|BriefHandoff| E[ExternalDelegation]
    E -->|ExternalHandoff| R[EvidenceReviewer]
    R -->|ReviewHandoff| H[HumanGate]
    H --> O[WorkflowOutcome]
```

`WorkflowBuilder(pca).AddEdge(pca, external).AddEdge(external, review)
.AddEdge(review, gate).WithOutputFrom(gate).Build()` is executed by
`InProcessExecution.RunAsync`. All four executors derive from official typed
`Executor<TInput,TOutput>`. They never invoke the next specialist. Blocked paths
still carry typed envelopes through the graph; stage reports distinguish skipped
specialist work from the official executor being invoked to propagate that state.
The engine stays alive after caller cancellation to emit the human-visible outcome.

## Actual official event evidence and identities

The happy-path test asserts four ExecutorInvokedEvent and four ExecutorCompletedEvent
entries in this exact order:

`PCA → ExternalDelegation → EvidenceReviewer → HumanGate`.

Completed-event payload types are respectively `BriefHandoff`, `ExternalHandoff`,
`ReviewHandoff`, `WorkflowOutcome`. Exactly one WorkflowOutputEvent has a
WorkflowOutcome payload. Actual emitted event sequence:

```text
WorkflowStartedEvent
SuperStepStartedEvent, ExecutorInvokedEvent, ExecutorCompletedEvent, SuperStepCompletedEvent
SuperStepStartedEvent, ExecutorInvokedEvent, ExecutorCompletedEvent, SuperStepCompletedEvent
SuperStepStartedEvent, ExecutorInvokedEvent, ExecutorCompletedEvent, SuperStepCompletedEvent
SuperStepStartedEvent, ExecutorInvokedEvent, ExecutorCompletedEvent, WorkflowOutputEvent, SuperStepCompletedEvent
```

Synthetic correlation is preserved as TaskId=`task-original`, WorkflowRunId /
official SessionId=`run-original`, ExecutionId=`execution-original`, provider
SessionId=`session-original`, MessageId / ExternalRequestId=`message-original`.
The official Workflow session ID is not the external provider session ID.
The externally supplied original execution DTO is reused with immutable collection
snapshots. Wrong task/run/execution/provider/session/message IDs block review;
execution facts cannot be replaced by a reviewer assessment.

## What actually ran; what was simulated

- Most cases execute the existing native ChatClientAgent through ScriptedBriefClient.
  The happy path invokes the real registered ReadFile tool on a disposable README
  containing hostile project instructions. The scripted model then returns a bounded
  proposal. This proves the real agent/tool/translation plumbing and deterministic
  rejection boundaries, **not** an actual model's prompt-injection resistance.
- Six explicitly named stand-in briefing cases test graph-side validation independently
  of the PCA adapter; they are not native-agent execution evidence.
- External delegation is an internal IControlledDelegation fixture, not OpenCodeExecutor,
  OpenCodeClient or ProtectedExternalRuntime. A separate orchestrator-owned assessment
  dependency (FakeAdmission, not the harness) returns
  Allow/Deny/RequireApproval. This proves routing only, not durable policy enforcement.
  No synthetic identity becomes production trust. Existing protected runtime paths
  remain unchanged and are covered by historical regressions.
- Happy-path logical fake Create/Prompt/Abort counters are **1/1/0**; actual HTTP is
  **0** throughout 4A. Denied/RequireApproval/invalid-brief/pre-delegation-cancel paths
  have **0/0/0** logical effects. These counters are fixture activity, not reliable
  real-provider request accounting or proof of transport behavior.
- The separate test reviewer evaluates completion, exact task/run consistency,
  available evidence references and usable summary. Its scripted alternatives test
  insufficiency, unavailability and NotApplicable. It performs no production semantic
  review. The original ReviewService/ReviewAgentFactory and their tests are untouched.
- The README remains byte-identical and no extra file is created by the happy flow.
  No Git-diff review is performed. No Changes would not establish task success.

## Bounded contracts, gates and failure semantics

Task/run IDs are bounded to 100 characters; workspace 1024; objective 4000. Each
stage has a 10 ms–2 min budget. Model output is limited to 8192 characters and strict
required JSON fields/known enum values; unknown fields are rejected. Scope and
objective must echo original application input exactly. Provider hints, approval
flags and workspace switches are never accepted from the proposal. PCA citations
must be empty because this adapter does not independently verify source references.
The proposal explicitly records that limitation, even after real tool use.

Arrays are immutable at new handoff boundaries: at most 16 references/limitations
of 256 characters each, external trace at most 64 entries, external summary 4000,
review summary 2000. The final aggregate contains only the bounded stage limitations
(at most 50), four stage states and two generated evidence references; no raw agent
conversation/tool transcript is retained. Existing external DTOs/fingerprint/schema
are unchanged. Serialization roundtrip and malformed/oversized inputs are tested.

| Condition | Deterministic result |
| --- | --- |
| PCA failure/timeout | BriefingUnavailable, no delegation/reviewer work |
| Invalid or scope-expanded brief, fabricated citation | InvalidTaskBrief; no delegation |
| Deny/unknown decision/source failure | Blocked; zero fake dispatch |
| RequireApproval | WaitingForApproval; no approval record, execution or dispatch |
| Unknown/Partial/Failed/Cancelled/Running/Pending | Original status retained; reviewer skipped; NeedsHumanReview |
| Provider WaitingForApproval | Original waiting state; no permission reply |
| Interrupted/ambiguous delegate response | Unknown with original expected handle; no retry, replacement session or Abort |
| Invalid external correlation/shape | Blocked; no untrusted result passed to Reviewer |
| Completed but insufficient/missing usable summary evidence | InsufficientEvidence; NeedsHumanReview |
| NotApplicable | NeedsHumanReview, never independent task acceptance |
| Reviewer failure/timeout/invalid identity/citation/enum | Typed ReviewUnavailable; original external facts retained |
| SufficientEvidence | ReadyForHumanReview, no automatic acceptance/Approve/Resume |

Manual test clocks explicitly expire each uncooperative asynchronous stage; no
sleep-based timeout synchronization or unbounded retry is used. Stopping a wait
cannot undo already-started external work. No Abort API exists on the fake delegation
contract. Repeated/concurrent same-task calls share one Lazy graph within the same
composition instance; changed input/run ID is rejected. Capacity is 100 task entries.
This is not persistent workflow checkpointing or restart deduplication; the test-only
composition must not be recreated to dispatch an existing external task. Existing
durable recovery-only/no-resubmit behavior is unchanged, not reimplemented here.

## Validation evidence

Local TRX files are retained under `TestResults/home-validation/` (not committed).

- Final focused `FullyQualifiedName~TypedHandoffTests`: **68 discovered / 68 passed /
  0 failed / 0 skipped**, 360 ms (`phase2b4-a-focused.trx`).
- Final solution `dotnet build MAF.slnx --no-restore`: **0 warnings / 0 errors**.
- Final offline filter `FullyQualifiedName!~MAF.Tests.OpenCodeLiveTests`:
  **1044 discovered / 1044 passed / 0 failed / 0 skipped**, 2 minutes 6 seconds
  (`phase2b4-a-offline.trx`). Test-name comparison retains all **976** post-M4
  historical cases with exactly **68** new TypedHandoffTests; none were removed.
- Relative links in both new documents and the staged whitespace check passed.

An intermediate rebuild overlapped an active Windows testhost and failed DLL copies
with MSB3021/MSB3027. The final build and verification were rerun sequentially after
that process ended. No historical assertion was changed to address that build failure.

## Guardrails, limitations and next gate

All seven guardrails remain intact: official graph owns order; the fake whole-task
harness boundary has no internal tool loop; policy is independently supplied and
never granted by PCA/Reviewer; human identity is not simulated as consent; stores
never launch workflows and are untouched; no Registry/availability permission grant
is added; deterministic validation bounds every model proposal. No new project,
package, orchestration engine, DB schema or production entrypoint is added. Internal
visibility is a composition restriction, **not** malicious same-principal isolation.

4A establishes executed typed plumbing and honest evidence limitations. Source-level
semantic sufficiency, live provider behavior, production trust, human authentication,
durable cross-stage workflow restart and corporate endpoint compatibility are not
demonstrated. The Human Gate is an output only; no UI or durable approval is created.

Current ProtectedExternalRuntime owns admission, ownership lifetime, side-effect guards
and its official external Workflow. Reusing it safely requires an explicit adapter
contract for parent/child run identity and lifetime; 4A deliberately does not call raw
public provider APIs or turn a policy outcome into a dispatch token. M3-C remains
blocked and mandatory before operational production activation.

Recommended separately authorized **4B**: one controlled fake-HTTP integration through
the existing protected runtime and v4 fixtures, proving exact identity/lifetime,
guarded request counts, denied side effects and original-execution recovery after a
stage-boundary crash. Preserve the existing guard/admission/continuation contracts;
stop if embedding requires an unsafe bypass. See the [execution plan](../plans/PHASE2B4_EXECUTION_PLAN.md).
No live provider, Host activation, write task, new schema, Registry or Hybrid work
is authorized by this recommendation. Stop after 4A and owner review.
