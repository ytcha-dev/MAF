# Local Solo MVP-S2D-A-R1 — bounded offline stability investigation

2026-10-20. Required/observed baseline `7041a83aefbeeb0e45f84f8086dbc3eaa45abfa3`;
`main` and `origin/main` matched. Only pre-existing untracked `.opencode/` was present;
its contents were not read or modified. Personal Windows, cached SDK/packages and reviewed
offline fixtures only. No live provider, model, server, process-crash/competition tests,
company workload, security-log probe or endpoint-security change.

## Historical evidence retained

The [original S2D-A report](LOCAL_SOLO_MVP_S2D_A_PERSONAL_LAB_ADMISSION_RESULTS.md)
and its ignored TRX artifacts remain unchanged. Historical results:

| Selection | Discovered / passed / failed / skipped | Duration |
| --- | --- | --- |
| Initial new Lab cases, before expectation corrections | 45 / 42 / 3 / 0 | See original TRX |
| Final Lab + preparation | 82 / 82 / 0 / 0 | 8s |
| Initial Host | 120 / 119 / 1 / 0 | 11s |
| Targeted Host repeat | 4 / 4 / 0 / 0 | 5s |
| Final serial Host | 120 / 120 / 0 / 0 | 8s |
| Native Reviewer | 66 / 66 / 0 / 0 | 56s |
| Source candidates | 59 / 58 / 1 / 0 | 64s |
| Protected handoff, excluding process cases | 34 / 33 / 1 / 0 | 36s |
| Source coverage | 42 / 42 / 0 / 0 | 40s |
| Source/protected diagnostic methods | 11 / 11 / 0 / 0 | 12s |
| Combined regression | 201 selected; 120 completed: 117 passed / 3 failed / 0 skipped | Aborted at 180s |

The combined run has **81 selected cases without a completed result**, not skipped/passed
cases. Failed Native Reviewer variants were permission (null Review, 3.31s), uncertain
(null Review, 3.23s) and changed source (expected callback 1, actual 0, 5.03s).
Source missing-file had an empty candidate collection (2.56s); protected cancel-wait
timed out waiting for its gate (6.49s); Host reviewer-unavailable returned Unknown
instead of Completed (6.11s). Successful repeats do not erase these failures.

## Static investigation and pre-run hypotheses

Reviewed the ROADMAP fixture rule; all nine requested test/fixture classes; linked
runtime/delegation/Workflow budgets; fake HTTP ordering and SQLite ownership. Compared
`5714ff7293f7039c886586b194c530b29e80d673` to the required baseline using `git diff`.
No baseline checkout/worktree or baseline execution was performed.

* **Session budget: confirmed orchestration defect.** The separately observed serialized
  class durations sum to approximately 196s (56+64+36+40), exceeding 180s. This explains
  the configured combined abort, not the individual failed assertions. R1 uses separate
  reviewed groups. A combined confirmation would need approximately 300–360s; it was not
  run. Production/task/stage timeouts remain unchanged.
* **Per-operation timing: initially plausible; observed cases are distinguished below.** `SyntheticRuntime.Request` defaults
  to 2s. `ProtectedExternalRuntime.RunCoreAsync` starts one real cancellation budget before
  ownership, admission, receipt/effect SQLite work and provider observation. The executor
  also has a bounded budget. `SyntheticClock` overrides UTC only; its timers/timestamp
  remain real. `SyntheticProtectedHandoff.Flow` uses `TimeProvider.System`. A frozen policy
  clock therefore does not freeze execution deadlines. Unknown/noncompletion skips the
  Reviewer and can explain missing callbacks only if the prerequisite trace confirms it.
  Historical assertion durations alone do not prove budget exhaustion.
* **Source case:** missing-file changes display/input paths to the same missing owned
  file; it does not remove the correlated completed provider read candidate. The independent
  reader should reject that candidate afterward. Fake message transformation is synchronous
  after fake response; source files exist before Workflow invocation. Wrong range/anchor,
  locked file, redaction and post-tool mutation retain their original assertions. Summary-only
  evidence is still rejected; no evidence validation was relaxed.
* **Gate case:** callback is installed before `RunAsync`; `TaskCompletionSource` retains
  its signal, so a callback occurring before the await is not a lost event. The original
  test waited only for the gate even when the Workflow had already ended. Its failure path
  could dispose HTTP and then clear pools/delete its owned directory without first awaiting
  the child. This is a **confirmed failure-path lifecycle defect**, not proof it caused the
  historical gate failure. The R1 correction observes early Workflow termination and drains
  the original task in `finally`, without duplicate Create/Prompt or provider Abort.
* **Shared state/scheduling:** fixture roots and runtime authority/clock/HTTP state are
  instance-owned. Request accounting is a ConcurrentQueue. Selected class disposal calls
  global `SqliteConnection.ClearAllPools`, so cross-collection disposal overlap is a plausible
  contention contributor, not a demonstrated cause. No global mutable task identity was
  found in these fixtures. Serial versus parallel evidence is reported separately below.
* **S2D-A comparison:** no changes to production external Workflow/runtime, SQLite or
  Native Reviewer implementation. Existing fixture defaults still use the same 2s identity/
  scope. Optional prepared identity/briefing and fake-effect observer are unused in the
  affected existing cases; observer defaults to null. Source/Reviewer prior test changes
  added diagnostics only. This narrows the suspect surface but does not prove the defect
  predates S2D-A or exclude scheduling effects of new collections.

## Focused changes

* `tests/MAF.Tests/ProtectedHandoffTests.cs`: wait for gate **or original Workflow** under
  the existing 5s bound; require the gate, report early outcome code/trace and HTTP counts;
  `finally` cancels local observation, releases the fake gate and awaits the same pending
  Workflow with the existing 10s completion bound before fixture disposal. All ownership,
  Create/Prompt/Abort and HumanGate assertions remain. A drain timeout is still a failure.
* `tests/MAF.Tests/LocalSoloWorkflowTests.cs`: bounded completion-prerequisite diagnostics.
* `tests/MAF.Tests/NativeEvidenceReviewerTests.cs`: include elapsed time and diagnose missing
  Review in the changed-source method before checking actual callback counts.
* `tests/MAF.Tests/SourceCandidateIntegrationTests.cs`: after the R1 full-class traversal
  failure, add the same bounded external-prerequisite code/trace/duration/count diagnostics
  to invalid-metadata cases. Assertions, response payloads and timeouts remain unchanged.
* This report records test orchestration and retained unsuccessful history. No generic
  fixture layer, retry logic, artificial delay, production change or new dependency.

## R1 execution evidence

TRX evidence stays ignored under `TestResults/home-validation`.
All commands use cached artifacts (`--no-restore`; tests also `--no-build`). Excluded process
cases are exclusions, not skipped cases. Test discovery was inspected before execution.

`git fetch origin` confirmed the remote-tracking baseline as well. Builds:

```text
dotnet build src/MAF.Host/MAF.Host.csproj --no-restore -m:1 -nr:false -v minimal
dotnet build tests/MAF.Tests/MAF.Tests.csproj --no-restore -m:1 -nr:false -v minimal
```

Both exit 0, zero warnings/errors: Host 31.28s, test project 18.85s.
Before rebuilding, the unchanged baseline test binary was exercised three times with the
same six method selections and all original parameter rows/assertions:

```powershell
$filter = 'FullyQualifiedName~IndependentReaderAndPostReviewValidationRemainAuthoritative|FullyQualifiedName~InFlightObservationPreservesChildOwnershipAndNeverAborts|FullyQualifiedName~CompletedDoesNotImplySuccessfulReview|FullyQualifiedName~InvalidAgentBehaviorCannotChangeCompletedFactsOrGrantApproval|FullyQualifiedName~AdvisoryNegativeResultsRemainNonExecutable|FullyQualifiedName~IndependentSourceValidationSurroundsNativeAgent'
# Executed once for attempt 1, then a bounded foreach for attempts 2..3.
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --list-tests --filter $filter
foreach ($attempt in 1..3) {
    dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter $filter --logger "trx;LogFileName=s2da-r1-repro-$attempt.trx" --results-directory TestResults/home-validation -- RunConfiguration.TestSessionTimeout=180000 xUnit.ParallelizeTestCollections=false
}
```

| Baseline attempt | Discovered / passed / failed / skipped | Runner duration / TRX session |
| --- | --- | --- |
| 1 | 55 / 55 / 0 / 0 | 56s / 58.961s |
| 2 | 55 / 55 / 0 / 0 | 44s / 46.657s |
| 3 | 55 / 55 / 0 / 0 | 57s / 63.793s |

Each attempt contains Source independent reader 6 cases; protected in-flight 5; Host
completed/review 4; Native invalid behavior 33 (including permission); Native advisory 3
(including uncertain); Native independent source 4 (including changed). Thus each originally
affected parameter was exercised three times without reproduction. These are overlapping
attempts, not 165 unique tests. No retries were added inside a test.

The reviewed final selection was also listed before execution: 403 cases, including 34
protected cases after explicitly excluding the two process-launch methods (nine parameter
rows). The following commands run one testhost at a time and produce separate evidence:

```powershell
$groups = @(
    @('focused','FullyQualifiedName~PersonalLabAdmissionTests|FullyQualifiedName~PersonalLabWorkflowTests|FullyQualifiedName~LocalExecutionPreparationTests'),
    @('host','FullyQualifiedName~LocalSoloHostTests|FullyQualifiedName~LocalSoloWorkflowTests|FullyQualifiedName~LocalSoloReadinessTests|FullyQualifiedName~ExternalConsoleOutputTests'),
    @('source','FullyQualifiedName~SourceCandidateIntegrationTests'),
    @('protected','(FullyQualifiedName~ProtectedHandoffTests&FullyQualifiedName!~RealProcessCrashRecoversOriginalChildWithoutParentResumeOrPost&FullyQualifiedName!~CompetingProcessCannotExecuteOrInspectWhileOriginalOwnerIsActive)'),
    @('native','FullyQualifiedName~NativeEvidenceReviewerTests'),
    @('coverage','FullyQualifiedName~SourceEvidenceCoverageTests')
)
foreach ($group in $groups) {
    dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter $group[1] --logger "trx;LogFileName=s2da-r1-$($group[0])-final.trx" --results-directory TestResults/home-validation -- RunConfiguration.TestSessionTimeout=180000 xUnit.ParallelizeTestCollections=false
}
```

The session budget is per group, not a 180s aggregate deadline. The largest historical
group took 64s; 180s leaves more than twice that historical measured time, while retaining all 2s
task/stage, 5s gate and 10s completion bounds. No combined 201-case rerun is needed merely
to repeat these individually completed classes; collection behavior is compared with the
bounded affected-method selection instead. The historical aborted session stays incomplete.

**R1 unsuccessful run retained:** `s2da-r1-source-final.trx` has 59 discovered/executed,
58 passed, 1 failed, 0 skipped (runner 80s, TRX session 90.952s). The failed case is
`InvalidMetadataCannotUpgradeCompletedExecutionToVerifiedEvidence("traversal")`:
expected Completed, actual Unknown; case duration 2.536s. The original missing-file case
passed. This is a newly observed variant of the external-completion prerequisite failure,
not reproduction of the exact historical parameter. It happened in a serialized single-class
session well below its 180s limit, so **aggregate session exhaustion cannot explain all
instability**. That method had no prerequisite diagnostics before this failure; the TRX
does not establish whether its Unknown came from ObservationTimedOut, persistence, or another
guard. No security-control termination was reported. No operating-system/security cause is
inferred from elapsed time. The subsequent diagnostic rerun does not erase this result.

| First final-source group | Discovered / passed / failed / skipped | Runner duration / TRX session |
| --- | --- | --- |
| Lab + preparation | 82 / 82 / 0 / 0 | 10s / 14.446s |
| Host / Local Solo | 120 / 120 / 0 / 0 | 13s / 24.174s |
| Source candidates | 59 / 58 / 1 / 0 | 80s / 90.952s |
| Protected handoff, no process cases | 34 / 34 / 0 / 0 | 29s / 39.500s |
| Native Reviewer | 66 / 66 / 0 / 0 | 62s / 70.963s |
| Source coverage | 42 / 42 / 0 / 0 | 38s / 40.771s |

These are 403 distinct selected cases, 402 passes and one failure, zero skipped; not a clean
403/403 regression. Every group completed within its session bound. The core four regression
groups alone used 91+39+71+41 = approximately 242s including runner startup, further supporting
separate session budgeting. Runner duration and TRX session timing measure different spans.

After the new source failure, rebuilt the test project only (same build command, exit 0,
zero warnings/errors, 14.53s) for the added diagnostics. Then executed exactly one additional
source class and one bounded collection comparison; no indefinite retry sequence:

```powershell
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~SourceCandidateIntegrationTests' --logger 'trx;LogFileName=s2da-r1-source-diagnostic.trx' --results-directory TestResults/home-validation -- RunConfiguration.TestSessionTimeout=180000 xUnit.ParallelizeTestCollections=false
# $filter is the same six-method filter defined above; original parameter rows unchanged.
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter $filter --logger 'trx;LogFileName=s2da-r1-parallel-diagnostic.trx' --results-directory TestResults/home-validation -- RunConfiguration.TestSessionTimeout=180000 xUnit.ParallelizeTestCollections=true xUnit.MaxParallelThreads=2
```

| Last bounded diagnostic | Discovered / passed / failed / skipped | Runner duration / TRX session |
| --- | --- | --- |
| Source class, diagnostics added | 59 / 59 / 0 / 0 | 67s / 72.176s |
| Same six methods, parallel collections, max two threads | 55 / 52 / 3 / 0 | 64s / 67.656s |

The parallel comparison uses the existing four owning collections and a maximum of two
threads; no external load or artificial contention is added. It does not reproduce the
exact historical default-parallel whole-Host environment. All three failures occurred well
before the test-session bound:

| Parallel failure | Case duration | Captured prerequisite evidence |
| --- | --- | --- |
| Source independent reader, wrong-anchor | 2.974s | Unknown / ObservationTimedOut; executor elapsed 1893.7672ms; PolicyVerified, SessionCreated, Submitted, Unknown |
| Protected in-flight, timeout | 2.830s | Unknown / ObservationTimedOut; same trace; Create/Prompt/GET/Abort = 1/1/4/0; observation gate never reached |
| Native invalid behavior, null-limits | 3.330s | Null Review; external Unknown / ObservationTimedOut; executor elapsed 1754.8009ms; PolicyVerified, Unknown |

**Established mechanism for these three R1 failures:** operation cancellation/budget exhaustion
before the required completion/observation prerequisite, not aggregate test-session exhaustion.
The protected gate failure is now reported from the completed original Workflow instead of
waiting five seconds for a callback that this child never reached. Native Review was correctly
not invoked after external Unknown. Source candidate absence is downstream of the unfinished
execution; it is not evidence that independent source verification should be relaxed.
Executor elapsed can be less than 2s because the outer protected budget starts earlier,
including admission/storage work. Case duration includes fixture initialization and unwind.

The exact historical six parameters all passed in the three pre-rebuild attempts, their
owning-class runs, and the final parallel comparison. Thus the **exact original parameter
failures did not reproduce**; related method-level symptoms did, with different rows.
Host Unknown and historical changed-source/uncertain/permission failures remain unassigned
to a proven cause. The R1 serialized traversal Unknown also lacks a captured error code;
the later successful source run cannot retrospectively identify it.

The cause of time spent inside the 2s bound remains **unresolved**: no evidence localizes
it to a particular SQLite transaction, synchronous fixture file write, Workflow scheduling
step or guard. Parallelism correlates with the three observed cancellations, but one
serialized-class failure prevents claiming collection serialization fixes everything.
There is no demonstrated lost gate signal, automatic Abort, duplicate POST or production
state-transition regression. Absence of proof is not proof that every production path is
defect-free. No per-test or operational budget was raised. No further reruns were performed.

All four R1 failed results remain in their original TRX files (one serialized source case,
three parallel cases). No R1 test session aborted; zero skipped cases in every R1 selection.
Do not combine overlapping diagnostic passes into a unique green regression count.

## Filesystem and safety boundaries

No R1 test constructor/field initializer allocates directories. The modified tests allocate
their existing unique fixtures only through explicit `Fixture()` calls because they require
real SQLite v4 ownership/receipt persistence and/or independent file reads. Source Candidate,
Native Reviewer, Source Coverage, Protected Handoff and LocalSoloWorkflow/PersonalLabWorkflow
compositions require those physical resources. Teardown targets only each generated owned
root; no abandoned/unknown-directory bulk cleanup was attempted.

`LocalExecutionPreparationTests` allocates an empty owned directory only in its explicit
canonical-directory test; other cases use existing output-directory metadata and assert their
workspace was not allocated. `PersonalLabAdmissionTests` creates no directories. Local Solo
Host uses a lazy owned empty workspace only for valid-workspace metadata/preview tests;
parser/denial/result tests assert it remains uninitialized. Readiness has one explicit owned
directory metadata case; output tests allocate none. The legacy eager `RuntimeEnforcementTests`
class is outside the selection. No new fixture framework or filesystem cleanup is introduced.

ProductionTrustUnavailable, PersonalLabNotApproved, exact v4 ownership/original receipts,
intent-before-wire, no ambiguous POST retry, no implicit Abort, independent evidence reads,
native Reviewer tool observation and HumanGate readiness versus acceptance remain intact.
No operational PersonalLab CLI is registered. Offline passes are not corporate clearance.

## Acceptance and next decision

R1 is ready for **Owner review as a bounded investigation with unresolved stability**, not
as a clean regression clearance. **Hold unconditional S2D-A acceptance and defer S2D-B.**
The session-budget defect is established and avoided by measured per-class sessions; the
gate failure-path lifecycle gap is corrected; operation-budget cancellation is directly
observed for three new failures. The remaining bottleneck and historical assertion causes
are not established. Failure-path drain is statically reviewed; the existing five gate
variants exercise its normal completion/cancellation paths. No intentionally failing
assertion was added to manufacture a cleanup result.

Smallest next Owner decision: authorize a separate bounded **offline fixture timing
diagnostic** around the existing admission/receipt/effect/observation checkpoints, retaining
the 2s operational bound, to locate where time is consumed before deciding whether any
fixture scheduling/synchronization correction is justified. Do not substitute a larger
deadline or retries for that evidence. This is a stability follow-up, not S2D-B activation.

Remaining pre-S2D-B product blockers are Lab-specific durable provenance/original-receipt
compatibility and enforceable independent PCA/Harness/Reviewer recipients. No claim of
operational readiness, protected trust or live recipient enforcement. Any next increment
and first live run require separate Owner authorization. README/ROADMAP acceptance status
was not promoted; the original report and all historical failures remain intact.

Actual changed files are the four test files listed above and this report; no production
code, schema, CLI, packages, configuration or historical acceptance records changed.
Final review includes `git diff --check`, scoped diff and staged-file inspection; no actual
credentials, recipient settings or personal machine identifiers are included.
**STOP after R1.**
