# S2D-A-R2 — bounded checkpoint timing

## Baseline and scope

Personal Windows offline investigation from `2fb7ff25d03d114ebef5dc53fbdd7bff9b1d8838`;
HEAD/main/origin/main matched. Only pre-existing untracked `.opencode/` was present and
was not inspected. [R1 history](LOCAL_SOLO_MVP_S2D_A_R1_STABILITY_RESULTS.md) remains intact.
Exactly one serial and one two-collection-thread affected-method selection; no full suite,
live provider/model, process-launch acceptance test, restore, package or security probe.

## Changes and measurement limits

* `ProtectedExternalRuntime.cs`: nullable internal friend-test checkpoint sink at budget start,
  identity/scope, ownership, policy/control, admission, child start/return and cancellation.
  No public input/Host registration/new friend access. Sink exceptions are contained; no
  authorization result, token deadline, transaction or effect semantics changed. Null by default;
  cancellation observation is registered only when diagnostics are explicitly attached.
* Test-only `OfflineCheckpointTiming.cs`: per-test AsyncLocal scope, monotonic Stopwatch ticks,
  at most 256 in-memory points; JSON output only after the test body/HTTP disposal. No filesystem
  allocator, output during a protected transaction, sleep, retry or executable selection.
* `SyntheticProtectedHandoff.cs`: reuse existing SQLite callbacks (including BeforeCommit and
  receipt/dispatch commits), fake HTTP begin/end/failure and actual Reviewer callback. The
  diagnostic store has the same authority/profile/capacity; no extra SQL or transaction changes.
* Existing six affected methods in LocalSoloWorkflow, SourceCandidateIntegration,
  ProtectedHandoff and NativeEvidenceReviewer tests opt into the scope and record outcomes.
  All assertions and 2s task/5s gate/10s completion bounds remain unchanged.

FixtureReady separates explicit SQLite/file initialization from budget time. BudgetStart is
recorded immediately after CancelAfter; ChildWorkflowReturned precedes final ownership unwind.
Class-owned directory deletion is outside the measured test-body scope. Fixed checkpoint names
and fake relative routes only; no objective, file contents, credentials or endpoint origin.
Instrumentation overhead is not independently calibrated; measurements are diagnostic evidence,
not a production benchmark. Existing explicit owned SQLite/source fixtures remain necessary;
pure tests/constructors do not allocate any new directories. No broad cleanup.

## Commands and outcomes

```powershell
dotnet build tests/MAF.Tests/MAF.Tests.csproj --no-restore -m:1 -nr:false -v minimal
$filter = 'FullyQualifiedName~IndependentReaderAndPostReviewValidationRemainAuthoritative|FullyQualifiedName~InFlightObservationPreservesChildOwnershipAndNeverAborts|FullyQualifiedName~CompletedDoesNotImplySuccessfulReview|FullyQualifiedName~InvalidAgentBehaviorCannotChangeCompletedFactsOrGrantApproval|FullyQualifiedName~AdvisoryNegativeResultsRemainNonExecutable|FullyQualifiedName~IndependentSourceValidationSurroundsNativeAgent'
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter $filter --logger 'trx;LogFileName=s2da-r2-serial.trx' --results-directory TestResults/home-validation -- RunConfiguration.TestSessionTimeout=180000 xUnit.ParallelizeTestCollections=false
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter $filter --logger 'trx;LogFileName=s2da-r2-parallel.trx' --results-directory TestResults/home-validation -- RunConfiguration.TestSessionTimeout=180000 xUnit.ParallelizeTestCollections=true xUnit.MaxParallelThreads=2
```

Selection was inspected with `--list-tests` before execution: 55 parameter rows, no process
tests. One build (including transitive Host/external/persistence/test-fixture projects) passed,
zero warnings/errors, 134.95s. Test-session limit is per selection; no aggregate 180s suite.
TRX and bounded per-case timing JSON remain ignored under `TestResults/home-validation`.

## Checkpoint measurements and serialized/parallel comparison

| Run | Discovered / passed / failed / skipped | Runner / TRX session seconds |
| --- | --- | --- |
| Serialized | 55 / 54 / 1 / 0 | 86 / 94.496 |
| Two threads | 55 / 54 / 1 / 0 | 65 / 72.706 |

Both sessions completed; both failures are retained. No confirmation C or additional rerun.
Each TRX contains 55 bounded timing records, including successful cases. Excluding the five
intentional in-flight gate/cancellation cases, budget-start to child-return measurements:

| 50 normal external journeys per run | Min ms | Median ms | Max ms |
| --- | --- | --- | --- |
| Serialized | 766.751 | 1052.481 | 2140.156 |
| Two threads | 775.273 | 916.252 | 2105.956 |

Max includes cancellation unwind; it does not mean the deadline was extended. The Native
Reviewer timeout variant intentionally times out Review, but its external prerequisite is
ordinary completion work, so it belongs in these external-journey measurements.

Failed cases: serial `NativeEvidenceReviewerTests.InvalidAgentBehaviorCannotChangeCompletedFactsOrGrantApproval("timeout")`
(case 4302.005ms); parallel `LocalSoloWorkflowTests.CompletedDoesNotImplySuccessfulReview("reviewer-unavailable")`
(case 3772.934ms). Both external outcomes: **Unknown / ObservationTimedOut**, Reviewer not
reached; Create/Prompt/GET/Abort respectively **1/1/5/0** and **1/1/9/0**. Thus the original
historical Host parameter reproduced with a measured external prerequisite failure.

Selected checkpoint deltas (ms); pairs explicitly name the interval, not additive totals:

| Interval | Serial failure | Parallel failure |
| --- | --- | --- |
| TestStart → FixtureReady, outside budget | 1123.952 | 877.162 |
| FixtureReady → BudgetStart, outside budget | 834.298 | 601.833 |
| BudgetStart → identity/scope checked | 5.415 | 4.421 |
| Identity/scope checked → ownership acquired | 17.957 | 38.596 |
| Ownership acquired → policy/control checked | 104.977 | 95.430 |
| AdmissionBegin → AdmissionConsumed | 217.012 | 171.323 |
| ChildWorkflowBegin → agent GET response | 330.645 | 289.460 |
| **Agent GET response → ReceiptIntentWritten** | **601.835** | **448.571** |
| ReceiptIntentWritten → ReceiptIntentCommitted | 32.057 | 64.174 |
| Create fake HTTP begin → end | 69.950 | 30.937 |
| Create response → ReceiptResponseCommitted | 176.232 | 151.889 |
| Prompt fake HTTP begin → end | 14.270 | 10.905 |
| Prompt response → status GET response | Not reached | 362.873 |
| Message GET begin → response | Not reached | 72.369, cancellation during interval |
| BudgetStart → cancellation callback | 2014.080 | 2006.629 |
| BudgetStart → child return | 2140.156 | 2105.956 |

Serial Prompt response was at budget 1847.127ms; last HTTP completed was session GET at
1972.015ms, then cancellation. Parallel status response was at 1943.613ms; message GET
started at 1982.539ms, cancellation occurred at 2006.629ms, response returned at 2054.908ms.
Cancellation may arrive during synchronous fake response work; its return is not permission
to ignore the cancelled protected budget. Both last runtime checkpoints were ChildWorkflowReturned.

The largest non-intentional adjacent protected interval in either run was **601.835ms**, not
an ownership/gate wait. `OpenCodeExecutor`/`PersistingClient` enter the Create intent path;
`ProtectedAccess.VerifyAsync` invokes `CommitCreateIntentAsync`, which uses
`VerifyEffectTransactionAsync` plus receipt construction/serialization/insertion. This entire
interval ends at ReceiptIntentWritten. It is **not a measurement of one SQLite commit**:
the following dispatch commit itself took 30.045ms serial / 61.710ms parallel. Later guarded
reads/persistence also accumulate tens to approximately 150ms between checkpoints.

Reviewer was reached in 41/55 cases in each run. Other non-reaches include deliberately
invalid/missing source and gate cases, not 14 unexplained Reviewer failures. Every case
recorded exactly one Create and Prompt, and zero Abort; GET counts vary with intentional
interruption and the two unexpected expirations. Full per-case counts/deltas are in TRX.
Aggregate fake request attempts Create/Prompt/GET/Abort: serial **55/55/486/0**, parallel
**55/55/490/0**. The deliberately blocked gate timeout waited 1058.708ms / 1195.542ms at
status GET before cancellation, so those intentional waits are excluded from the normal
work maximum above. They still returned Unknown and retained their original assertions.

## Confirmed facts, hypotheses and root-cause decision

**Category A established for the observed runs:** normal integrated fixture work does not
reliably fit the 2s operation budget, even serialized. Identity/ownership alone were small;
admission, validation, the pre-Create receipt path and later guarded observation together
used the budget. This is not the aggregate session timeout or Reviewer's intended failure.
Median journeys had roughly 947.5ms / 1083.7ms remaining, but the measured tail exhausted it.

**Not established:** a specific SQLite lock, deadlock, lost event, transaction bug, malicious
provider behavior, or security-software cause. First-use/JIT/serialization, storage and
scheduling costs inside the 601.835ms composite span remain hypotheses. The probe cannot
separate them; parallelism alone is not the cause, and no production invariant violation
was demonstrated. Historical source/protected failures are not retroactively proven to
have exactly the same lower-level cause.

No timeout correction was made in this bounded increment. A justified next correction is
an explicit **test-only 5s operation budget for non-deadline scenarios**, selected before
policy/intention construction. Dedicated short cancellation/Unknown and Reviewer deadline
tests must retain their intended short bounds. `SyntheticCancellation` snapshots the policy
scope during construction and HandoffTask shares StageTimeout with Review; changing Request
after construction or globally changing SyntheticRuntime's 2s default would violate those
test bindings or broaden the change. That requires a separately scoped fixture correction,
not an unvalidated global timeout increase during R2.

## Remaining risk and S2D-A acceptance recommendation

**Hold unconditional S2D-A acceptance.** R2 delivers actionable timing evidence and is ready
for Owner review, not a green-regression claim. Single best next action: implement/validate
the narrowly parameterized test-only budget correction above, preserving short deadline
coverage and exact scope binding. No need to alter protected production guards or repeat
the entire investigation. ROADMAP status is not promoted; R1 unsuccessful evidence remains.

ProductionTrustUnavailable, PersonalLabNotApproved, v4 admission/
receipt/ownership, no ambiguous POST retry/implicit Abort, independent source verification,
native Reviewer tool observation and HumanGate versus acceptance remain unchanged. No operational
PersonalLab command or S2D-B work. **STOP after R2.**
