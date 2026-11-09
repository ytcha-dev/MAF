# PersonalLab offline runtime lifecycle

Separate follow-up: [Owned Harness Process Lifecycle](OWNED_HARNESS_PROCESS_LIFECYCLE.md)
is authored for independent static review, with all execution validation NotRun. It is not
wired into this runtime and does not change the ownership or authorization described below.

Status: bounded Runtime Lifecycle Closure implementation; verification is recorded in
[acceptance evidence](../acceptance/RUNTIME_LIFECYCLE_CLOSURE_RESULTS.md). No live activation.

## Decision and ownership

Use explicit cancellation plus bounded drain, retaining quiescent synchronous disposal.
No new public runtime interface, async-disposal supervisor, process ownership service or
background scheduler is introduced. The internal PersonalLabOfflineRuntime owns one
single-flight child, one single-flight four-stage journey, their linked cancellation
sources, and the external HttpClient/boundary/transport chain. It does not own processes,
other runtime instances, native recipient clients or the PersonalLabOfflineOperation journal.

The caller keeps both native recipients and the operation alive until drain completes,
records the original WorkflowOutcome while the journal is open, then disposes resources.
Same-runtime repeated starts join the original task; they never allocate another execution.
The first start's caller token governs that start. A runtime stop seals new starts, while
queries for an already-started identity still return the original task. Different task/run
identities remain rejected. Independently starting more tasks is unsupported.

## Cancellation and completion

- RequestCancellationAsync seals new starts and requests local cancellation once. Repeated
  requests return the same task. Its successful completion means local cancellation callbacks
  finished, not remote Abort acknowledgement or terminal session completion. Callback errors
  fault that task and remain observable; stopping is not rolled back.
- Caller-supplied cancellation remains supported separately. CancellationRequested indicates
  an explicit runtime stop, not whether some caller/deadline token has cancelled.
- WaitForCompletionAsync(timeout, token) waits on the journey, child, cancellation callbacks
  and registered underlying native/specialist tasks, rechecking after parents finish. Timeout
  or a cancelled wait stops only that caller's wait; it does not dispose or detach the work.
  It returns local faulted/cancelled task counts after quiescence. Counts are diagnostic task
  counts, not remote execution or request counts. Original run tasks/outcomes remain the source
  of domain success/failure; drain itself does not rewrite them or grant execution authority.
- Dispose rejects active work with AwaitLabRuntimeBeforeDisposal. Once quiescent it disables
  access and disposes the owned transport once, releasing linked sources in finally. A handler
  disposal exception is propagated; the runtime remains closed and does not retry disposal.
  Such a failure cannot prove that an arbitrary handler released every internal resource.

The optional internal ownership observer on ControlledHandoffWorkflow captures tasks before
bounded WaitAsync can return. NativeEvidenceReviewer additionally registers its actual Agent
run task, since its own bounded wait can return before that task stops. The existing public
Reviewer constructor and all compositions without an observer keep their existing behavior.
The observer is wired only to the internal Lab ownership ledger; it supplies no authorization.
Completed tasks remain in the ledger for this single bounded journey to preserve diagnostic
status and observe exceptions. This is not an unbounded task service.

## Outcomes and limits

Local cancellation neither authorizes provider Abort nor proves a remote terminal outcome.
The existing executor retains Unknown for interrupted observation, and original receipt,
source validation, approval and no-retry checks remain unchanged. A completed external result
may remain Completed when only the later Reviewer was interrupted; that does not make the
journey review-ready. The caller can persist the original diagnostic outcome with the existing
Lab journal; candidate and verified session identities remain distinct, and reopen is
inspection-only. A cancellation/drain timeout never consumes/refunds a POST attempt or
silently resubmits one. No automatic journal outcome persistence is added.

An uncooperative task can outlive a bounded wait. It remains tracked and prevents disposal;
the caller must retain its resources and arrange cooperative completion before retrying drain.
No thread/process kill, detach fallback, indefinite automatic DisposeAsync, or remote session
recovery is added. Same-principal malicious code is outside this cooperative offline boundary.
A future live runtime still requires an explicitly authorized owned child/transport factory,
recipient/data-flow configuration, downstream custody/attestation decisions, and separate
remote cancellation/reconciliation evidence. This increment alone does not clear those gates.
