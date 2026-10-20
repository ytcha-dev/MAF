# S2D-A-R3 — test fixture budget isolation

## Baseline and correction

Baseline/HEAD/main/origin/main: `e245214e73123377ed45208cb0ab19f062c41a82`.
Only pre-existing untracked `.opencode/` was present; not inspected or changed. Personal
Windows, cached dependencies and reviewed offline fixtures only. [R2 measurements](LOCAL_SOLO_MVP_S2D_A_R2_CHECKPOINT_TIMING_RESULTS.md)
showed normal guarded fixture work exhausting 2s, including a 449–602ms composite pre-Create
receipt path. This correction addresses test budget isolation, not a demonstrated production bug.

Optional `TimeSpan? operationTimeout` is passed through **SyntheticProtectedHandoff →
SyntheticCancellation → SyntheticApprovalApplication → SyntheticRuntime** before the policy
snapshot is constructed. Runtime stores the selected value in a readonly field; null still
means 2s. Request, Task.StageTimeout, input scope and all policy-rule BudgetTicks agree;
fixture assertions check those bindings before SQLite initialization. Existing task/run/
execution identities, fingerprint computation and v4 admission/receipt operations are reused. No request
mutation, task replacement, re-admission or global mutable setting.

Changed files: the four constructor files above; `SourceCandidateIntegrationTests.cs`,
`LocalSoloWorkflowTests.cs`, `NativeEvidenceReviewerTests.cs`, `ProtectedHandoffTests.cs`;
this report and one scoped-status line in `docs/ROADMAP.md`. Production runtime, configuration,
schema, CLI and authorization code unchanged.

## Explicit classification

| Existing selected method / variants | External budget | Reason |
| --- | --- | --- |
| Source IndependentReaderAndPostReviewValidationRemainAuthoritative, all 6 | 5s | Completed provider result is prerequisite to independent rejection/freshness tests |
| Host CompletedDoesNotImplySuccessfulReview, all 4 | 5s | Review/source failure must not accidentally become external timeout |
| Native InvalidAgentBehaviorCannotChangeCompletedFactsOrGrantApproval, all 33 | 5s | External completion precedes native behavior rejection; timeout row isolated below |
| Native AdvisoryNegativeResultsRemainNonExecutable, all 3 | 5s | Completed external result precedes advisory assessment |
| Native IndependentSourceValidationSurroundsNativeAgent, all 4 | 5s | Independent acquisition/tool/post-review validation |
| Protected InFlightObservationPreservesChildOwnershipAndNeverAborts, all 5 | **2s** | Preserve timeout, cancel-wait, dispose-owner, cancel-control and policy interruption semantics |

Native `timeout` retains a **2s Reviewer deadline**, started only when its existing Reviewer
callback is entered. A linked CTS supplies that token to the real NativeEvidenceReviewer;
the existing scripted client still waits for cancellation, and the existing official Workflow
still determines the outcome. Assertions outside the Workflow additionally require this
short deadline to have cancelled, its parent token to remain uncancelled and exactly one
scripted call. No status substitution, assertion removal or custom orchestration loop.
The outer immutable task/stage budget is 5s; it is not changed after external admission.

All other callers retain the 2s fixture default, including Host lost-Create/pending cases.
Prepared PersonalLab tests retain their existing separately bound prepared task budgets.
No timeout/cancellation/ambiguity case was globally lengthened. The existing infinite wait
in the timeout scripted client is unchanged; no sleep, arbitrary delay or retry was added.

R2 bounded timing remains useful for checking unexpected Unknown and effect counts in the
two selections. Its internal production hook is unchanged, nullable/non-authoritative and
not registered by Host; removing it would add an unrelated production diff, so it is retained.

## Commands and actual validation

```powershell
dotnet build tests/MAF.Tests/MAF.Tests.csproj --no-restore -m:1 -nr:false -v minimal
$filter = 'FullyQualifiedName~IndependentReaderAndPostReviewValidationRemainAuthoritative|FullyQualifiedName~InFlightObservationPreservesChildOwnershipAndNeverAborts|FullyQualifiedName~CompletedDoesNotImplySuccessfulReview|FullyQualifiedName~InvalidAgentBehaviorCannotChangeCompletedFactsOrGrantApproval|FullyQualifiedName~AdvisoryNegativeResultsRemainNonExecutable|FullyQualifiedName~IndependentSourceValidationSurroundsNativeAgent'
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter $filter --logger 'trx;LogFileName=s2da-r3-serial.trx' --results-directory TestResults/home-validation -- RunConfiguration.TestSessionTimeout=180000 xUnit.ParallelizeTestCollections=false
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter $filter --logger 'trx;LogFileName=s2da-r3-parallel.trx' --results-directory TestResults/home-validation -- RunConfiguration.TestSessionTimeout=180000 xUnit.ParallelizeTestCollections=true xUnit.MaxParallelThreads=2
```

Selection/effects were inspected before execution; no live/model/process acceptance tests.
One build, including necessary Host/external/persistence/fixture dependencies: exit 0,
0 warnings/errors, 73.75s. Session bounds are separate from operation budgets.

After both 55-case selections passed, exactly one reviewed scoped regression was run:

```powershell
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~PersonalLabAdmissionTests|FullyQualifiedName~PersonalLabWorkflowTests|FullyQualifiedName~LocalExecutionPreparationTests|FullyQualifiedName~LocalSoloHostTests|FullyQualifiedName~LocalSoloWorkflowTests|FullyQualifiedName~LocalSoloReadinessTests|FullyQualifiedName~ExternalConsoleOutputTests' --logger 'trx;LogFileName=s2da-r3-scoped-regression.trx' --results-directory TestResults/home-validation -- RunConfiguration.TestSessionTimeout=180000 xUnit.ParallelizeTestCollections=false
```

| Selection | Discovered / passed / failed / skipped | Runner / TRX session seconds |
| --- | --- | --- |
| Serialized affected methods | 55 / 55 / 0 / 0 | 46 / 49.046 |
| Two-thread affected methods | 55 / 55 / 0 / 0 | 35 / 39.462 |
| S2D-A/S2C + Host | 202 / 202 / 0 / 0 | 22 / 25.051 |

The 202 comprise 46 Lab, 36 preparation and 120 Host/S1/S2A/S2B cases. They overlap the
affected Host method; do not sum runs into a unique test count. All selections completed,
zero skipped, no retries or additional confirmation runs. TRX stays ignored under
`TestResults/home-validation`; historical unsuccessful R1/R2 artifacts are untouched.

In each affected-method run all **50 ordinary external journeys completed**, with no error
code and no unintended ObservationTimedOut. Reviewer callback reached 42/55; the other 13
are expected source-unavailable/interruption paths. Aggregate fake request attempts were
**Create/Prompt/GET/Abort = 55/55/490/0** in each run; each ordinary case retained its
1/1/9/0 assertion. Measured maximum normal budget-start→child-return was 653.897ms serial,
1336.162ms parallel. These bounded runs did not recreate the R2 slow tail, so they do not
prove indefinite stability or erase earlier failed runs.

**Short deadline preservation:** all five Protected in-flight variants passed twice at 2s.
Timeout and cancel-wait still returned Unknown/ObservationTimedOut; dispose-owner returned
Unknown/PersistenceUnavailable; cancel-control and policy returned Unknown/RuntimeAuthorizationDenied.
Each retained Create/Prompt/GET/Abort = 1/1/8/0, original ownership and no-Abort assertions.
Native `timeout` passed twice with Completed external prerequisite, Reviewer reached,
ReviewUnavailable, its 2s token cancelled and its 5s parent token still live (additional
assertions passed). The default-budget Host lost-Create test also passed in the scoped
regression, preserving Unknown/original identity/no replacement or resubmission.

## Filesystem and acceptance

No new directory allocator, eager xUnit fixture, location change or cleanup. Existing explicit
SQLite/source fixtures are required by these integration tests and clean only their generated
owned roots. Pure S2C/admission/parser/result tests remain untouched and do not acquire new
filesystem resources. No unrelated files, credentials or private recipient settings included.
R1/R2 failure evidence remains intact; no broad 403-case regression or repeated-until-green run.

No unexplained failure remains **in these R3 selections**. Recommendation: **Conditional
S2D-A acceptance**, ready for Owner acceptance review of the offline boundary and this scoped
correction. This is not unconditional full-regression clearance: nonselected Source/Reviewer/
Coverage/Protected cases retain their defaults and were not broadly rerun. In particular,
the historical R1 invalid-metadata traversal case is outside this selection and its prior
failure is not erased. Owner acceptance is not declared here; no 403-case pass is claimed.
ROADMAP records scoped progress only; README acceptance status is not promoted.

ProductionTrustUnavailable, PersonalLabNotApproved, exact ownership/
receipts, intent-before-wire, no ambiguous POST retry, no implicit Abort, independent evidence,
native tool observation and HumanGate versus acceptance remain unchanged. No live activation,
operational CLI or S2D-B. **STOP after R3.**
