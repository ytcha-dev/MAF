# Local Solo MVP-S2D-A — PersonalLab admission and offline effects

2026-10-20. Required and observed baseline:
`5714ff7293f7039c886586b194c530b29e80d673`, branch `main`, matching
`origin/main`. Only pre-existing untracked `.opencode/` was present; its contents were
not inspected or changed. Owner scope: implementation and bounded offline validation
on the personally controlled Windows workstation. No live execution authorization.

## Delivered implementation

- [PersonalLabAdmission](../../src/MAF.Host/PersonalLabAdmission.cs): small internal,
  in-process cooperative research admission and offline effect reservation/observation.
  No policy, HTTP client, model client, store, executable, factory or process dependency.
- [LocalExecutionPreparation](../../src/MAF.Host/LocalExecutionPreparation.cs): retains
  all S2C immutable bindings and denial defaults; adds a one-time atomic claim so the
  same prepared operation cannot obtain another admission after use, revocation or expiry.
- [PersonalLabAdmissionTests](../../tests/MAF.Tests/PersonalLabAdmissionTests.cs): exact
  binding, default denial, lifecycle, budgets, concurrent one-attempt POST, unknown delivery,
  production/CLI isolation and post-revocation observation tests; no directory allocation.
- [PersonalLabWorkflowTests](../../tests/MAF.Tests/PersonalLabWorkflowTests.cs): existing
  official graph, guarded scripted native clients/fake HTTP, actual independent source reader,
  native Reviewer tool observation and S2A Host mapping. No new orchestration loop.
- [LocalExecutionPreparationTests](../../tests/MAF.Tests/LocalExecutionPreparationTests.cs):
  eager fixture correction before S2D-A source implementation; original assertions retained
  or replaced with stronger checks for no workspace allocation / unchanged existing entries.
- Test-only [SyntheticRuntime](../../tests/MAF.ApprovalAcceptance/SyntheticRuntime.cs) and
  [SyntheticProtectedHandoff](../../tests/MAF.ApprovalAcceptance/SyntheticProtectedHandoff.cs):
  optional original prepared task identity, scripted PCA access, optional briefing and
  before/after **fake** transport observation hook. Defaults remain unchanged for old tests.
  No Host dependency on either fixture and no new friend assembly relationship.
- [NativeEvidenceReviewerTests](../../tests/MAF.Tests/NativeEvidenceReviewerTests.cs) and
  [SourceCandidateIntegrationTests](../../tests/MAF.Tests/SourceCandidateIntegrationTests.cs):
  bounded failure diagnostics for prerequisite status/error/trace only. No assertion or
  fixture timeout relaxed; no raw model transcript or private configuration logged.
- README and ROADMAP: minimal current-status and next-boundary updates.

Production Core policy, ProtectedExternalRuntime, OpenCodeClient, receipt transport,
SQLite schema/admission/ownership and the four-stage Workflow implementations are unchanged.
`--local submit`, `--external-task`, `--recover`, `--recover-pending` retain their blockers;
`--local personal-run` is not registered. No environment/configuration activation exists.

## Exact cooperative admission semantics

`AdmitOffline` requires a real S2C preparation object, its complete exact immutable input,
PersonalLab profile and a bounded preselected execution identity. It rechecks the selected
workspace metadata. No TaskId, WorkflowRunId or operation identity is recreated. Admission
binds OperationId, original HandoffTask (including objective/root/stage timeout), execution
identity, OpenCode/ReadOnly/profile, all three recipient configuration values and evidence
budgets. Missing roles/recipients fail S2C validation before admission. There is no partial
matching or conversion to a production trusted-caller, policy decision or approval receipt.

| State | Meaning |
| --- | --- |
| Invalid preparation/configuration | S2C validation error; no admission issued |
| Prepared, unadmitted | No admission object; Reserve fails LabAdmissionRequired |
| Admitted | Explicit internal cooperative offline research admission; effects still denied |
| Running | BeginOffline consumed the one foreground start; exact offline reservations possible |
| Consumed / Expired / Revoked / Cancelled | New starts/effects denied; no renewal or second admission on that preparation |
| ProductionProtected | Separate authority domain; LabProfileRequired rejects its preparation; existing production trust remains unavailable |

Lifetime is four times the immutable stage timeout (four existing stages, at most eight
minutes), checked against UTC expiry and elapsed monotonic time; UTC rollback invalidates
this in-process admission. No background renewal, durable replay or production anti-rollback
claim. A synchronized state/attempt gate prevents concurrent duplicate starts/POST attempts
on one preparation. This is not a machine-wide scheduler or multi-process Lab ownership.

PersonalLab assumes one cooperative operator, personal Windows/workspace/test data and
three separately selected recipients, foreground ReadOnly only. It excludes independent
authentication, protected human-consent evidence, hostile same-principal isolation,
corporate authorization, protected custody and guaranteed OS/provider sandboxing. Production
failure never selects this profile. Existing production requirements remain intact.

## Effects, observations and result boundaries

`Reserve` denies without a Running admission; it compares the full operation/task/run/
execution/input/budget value, checks lifetime and cancellation, revalidates canonical
directory metadata and accepts only ControlledOfflineFixture. Network and undefined
actions are always rejected, even with matching recipient metadata.

Allowed fixture effects: scripted PCA inference, Harness validation, one CreateSession,
one SubmitPrompt, bounded Observe and scripted Reviewer inference. Each operation allows
at most 128 effect reservations and eight calls per native role (Reviewer uses its prepared
eight-call evidence budget). Existing eight-source/twenty-line/32768-character evidence
limits remain enforced by source/Reviewer code. These are offline work budgets, not live
PCA/Harness cost or network restrictions.

Create/Prompt reserve before the fake effect, never refund after an exception, missing
response or lost observation. Prompt additionally requires a returned Create observation;
that observation is **not** a verified session receipt. The nested protected fixture still
independently validates/finalizes the v4 original-response receipt and each actual fake
provider effect. Unknown outcomes do not create replacement sessions or resubmit prompts.

Write, shell, elevation, permission reply and Abort are absent from the allowed effect set.
Permission waiting remains WaitingForApproval. Cancellation/revocation stops new fixture
effects; it neither dispatches Abort nor proves the provider cancelled. An in-flight result
can be recorded after revocation without restoring permission. Dispose of an unfinished
attempt records Returned=false (delivery uncertainty, not proof the action did not occur);
RecordReturned records only a fixture call returning, not
success, terminal state, authorization or human acceptance. Snapshots are bounded immutable
observation arrays, explicitly PersonalLab / ControlledOfflineFixture / NotLiveProvider.

No effect API executes a callback/client. The offline target enum is not transport attestation
or containment against hostile code. Test wrappers exclusively invoke reviewed in-memory
handlers/scripts; there is no operational composition that could select another transport.

## Official Workflow integration and reused guarantees

Actual test path:

```text
S2C preparation (original task/run) -> AdmitOffline -> BeginOffline
 -> independently bound scripted PCA/Reviewer adapters
 -> existing ControlledHandoffWorkflow / official Microsoft Agent Framework
    PCA -> ExternalDelegation -> EvidenceReviewer -> HumanGate
 -> existing ProtectedHandoffDelegation / ProtectedExternalRuntime (test-owned trust)
 -> unchanged SQLite v4 atomic admission/ownership/intent/original-response receipts
 -> existing OpenCodeClient / official child executor, fake HTTP guarded before handler
 -> provider candidates -> mandatory VerifiedSourceMode(BoundedSourceReader)
 -> actual native Reviewer ReadEvidence tool observation and source currentness recheck
 -> existing WorkflowOutcome -> LocalSoloOutcomeMapper -> LocalResultView
 -> Consume (never human acceptance)
```

The diagram shows stage internals, not a new linear orchestrator. Exact executor events,
original task/run/execution IDs, source bindings, receipt status and HTTP/model counts are
asserted. The successful fixture actually observes Example.cs line 120 using native evidence
tools; missing/partial evidence and Reviewer failure remain visible. HumanGate readiness
never changes LocalHumanChoice.NotEstablished. All views retain
`ControlledOfflineFixture / NotLiveProvider` and the existing operational blocker.

Lab admission supplies **no production authority**. The reviewed protected fixture separately
supplies its historical synthetic test authority, as before; it does not derive one from the
Lab object and cannot be registered by Host. A current policy Deny still yields zero fake
HTTP, no execution row and Blocked, despite Lab admission. Production runtime construction
and internals remain in the existing assembly; no Host-access friend relationship was added.

## Storage/provenance and real-recipient blockers

Lab lifecycle/reservations/observations exist only in memory; there is no DB path/open API,
schema change, new migration, reset, serialization-based re-admission or Lab approval row.
The same preparation cannot be reused in-process, but this is not restart deduplication.
The original-session v4 guarantees are demonstrated by the **independent protected test
composition**, not established as operational Lab persistence. SQLite writable state is not
protected custody. Production approval rows are never reinterpreted as PersonalLab records.

Safe sharing stops at the existing v4 authority prerequisite: atomic admission and receipt
coordination require current protected policy/ownership evidence. Lab admission implements
none of those interfaces. A genuine Lab provenance/admission/receipt storage contract is
still needed before operational use; no synthetic production resolver was inserted to bridge it.

PCA and Reviewer reuse S2C independent role configuration/client metadata matching; the
wrappers identify scripted clients only. Metadata is not outbound authorization. Real native
transport/recipient/redirect/retry/proxy/model-override and credential/data-flow enforcement
remain unavailable. OpenCodeOptions and provider scope bind loopback origin/root/agent/read
rules, not downstream provider/model; prompt_async currently selects agent without an exact
provider/model binding. No server configuration attestation/model pin exists here. S2C's
HarnessRecipientVerificationUnavailable remains unchanged even for equal configuration.
No live model/OpenCode/configuration probe or actual recipient setting was used or published.

## Fixture compliance and validation scope

| Test group | Directory allocation and reason |
| --- | --- |
| PersonalLabAdmissionTests | None. Value/state/denial tests have no allocator or cleanup. Valid preparation reads only existing test-output directory metadata. |
| S2C configuration/binding/denial/CLI/native metadata cases | None. NoWorkspaceAllocated asserts the owned-workspace flag and path remain unused; positive native binding checks existing directory entries unchanged. |
| S2C ExactImmutableBindingReusesTaskAndCreatesNoFiles | One explicitly allocated empty owned directory for canonical metadata/no-file-effects assertion; exact non-recursive cleanup. |
| PersonalLabWorkflowTests.RemovedWorkspaceFailsBeforeAnyEffectReservation | One explicitly allocated empty owned directory, deleted non-recursively to test metadata invalidation; no DB/client. |
| PersonalLabWorkflowTests official journey cases | Existing SourceCandidateIntegrationTests.Fixture allocates isolated owned SQLite/source data only when called by a filesystem-dependent test. Existing reviewed fixture initialization/migrations and exact generated-root cleanup only; no new migration API/code. |
| S1/S2B/legacy protected/source/Reviewer regressions selected below | Existing lazy/explicit metadata or owned source/SQLite fixtures; no eager allocation in selected xUnit constructors/initializers. |

No new xUnit constructor or field initializer creates a directory. Disposal never initializes
unused fixtures; no arbitrary user/project path is cleaned. These are assertions/static
allocation-path checks, not proof of zero OS-wide side effects. Tests requiring a valid prepared
operation may read existing output metadata; they do not claim to be filesystem-read-free.

Before execution, selected source and fixture paths were inspected for process launches,
real transports/models, credentials and filesystem setup. Excluded: live/provider/model
launchers, process crash/competition methods, unreviewed broad suites and company probes.
The old RuntimeEnforcementTests class still eagerly allocates a directory; its three duplicate
Host-denial cases were deliberately excluded. Equivalent behavior is covered by the existing
allocation-free LocalSoloHostTests and new Lab isolation cases; no broad unrelated fixture
refactor was performed. Personal validation does not clear corporate execution restrictions.

## Validation results

No package changes, new dependencies, external restore or installation were needed.
Existing cached SDK/assets only. PowerShell interactive/native CLI; no `.ps1` execution.
The final regression commands disable parallel xUnit collections and bound the test session
to 180 seconds; the focused suite has a 180-second session bound and only two test classes.

```text
dotnet build src/MAF.Host/MAF.Host.csproj --no-restore -m:1 -nr:false -v minimal
dotnet build tests/MAF.Tests/MAF.Tests.csproj --no-restore -m:1 -nr:false -v minimal
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --list-tests --filter 'FullyQualifiedName~PersonalLabAdmissionTests|FullyQualifiedName~PersonalLabWorkflowTests|FullyQualifiedName~LocalExecutionPreparationTests'
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --list-tests --filter 'FullyQualifiedName~LocalSoloHostTests|FullyQualifiedName~LocalSoloWorkflowTests|FullyQualifiedName~LocalSoloReadinessTests|FullyQualifiedName~ExternalConsoleOutputTests|(FullyQualifiedName~ProtectedHandoffTests&FullyQualifiedName!~RealProcessCrashRecoversOriginalChildWithoutParentResumeOrPost&FullyQualifiedName!~CompetingProcessCannotExecuteOrInspectWhileOriginalOwnerIsActive)|FullyQualifiedName~NativeEvidenceReviewerTests|FullyQualifiedName~SourceCandidateIntegrationTests|FullyQualifiedName~SourceEvidenceCoverageTests'
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~PersonalLabAdmissionTests|FullyQualifiedName~PersonalLabWorkflowTests' --logger 'trx;LogFileName=local-solo-s2da-focused-final.trx' --results-directory TestResults/home-validation -- RunConfiguration.TestSessionTimeout=180000
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~LocalExecutionPreparationTests' --logger 'trx;LogFileName=local-solo-s2da-preparation.trx' --results-directory TestResults/home-validation -- RunConfiguration.TestSessionTimeout=180000
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~LocalSoloHostTests|FullyQualifiedName~LocalSoloWorkflowTests|FullyQualifiedName~LocalSoloReadinessTests|FullyQualifiedName~ExternalConsoleOutputTests' --logger 'trx;LogFileName=local-solo-s2da-host-final.trx' --results-directory TestResults/home-validation -- RunConfiguration.TestSessionTimeout=180000 xUnit.ParallelizeTestCollections=false
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter '(FullyQualifiedName~ProtectedHandoffTests&FullyQualifiedName!~RealProcessCrashRecoversOriginalChildWithoutParentResumeOrPost&FullyQualifiedName!~CompetingProcessCannotExecuteOrInspectWhileOriginalOwnerIsActive)|FullyQualifiedName~NativeEvidenceReviewerTests|FullyQualifiedName~SourceCandidateIntegrationTests|FullyQualifiedName~SourceEvidenceCoverageTests' --logger 'trx;LogFileName=local-solo-s2da-protected-regression.trx' --results-directory TestResults/home-validation -- RunConfiguration.TestSessionTimeout=180000 xUnit.ParallelizeTestCollections=false
```

Initial Host build passed (0 warnings/errors, 60.27s). Initial test build passed with one
xUnit2031 analyzer warning; changed the new assertion to Assert.Single's predicate overload.
Intermediate rebuild passed with 0 warnings/errors (14.43s); subsequent Host/test builds
also passed (14.75s/20.88s). After the post-metadata lifetime recheck and diagnostic additions,
final Host build passed with 0 warnings/errors (20.00s), final test-project build passed
with 0 warnings/errors (34.22s). Diagnostic-only intermediate test build: 22.64s, 0 warnings/errors.

First focused run used the same focused command with logger `local-solo-s2da-focused.trx`:
45 discovered, 42 passed, 3 failed, 0 skipped. New fixture expectations were corrected:
cancelled original-response parsing preserves a Pending receipt, not Verified; authority
control failure was replaced with explicit current policy Deny to exercise deterministic
Blocked; lost Prompt response can still yield Completed through original-session GETs.
The final suite adds a separate lost-prompt-observed positive case, while lost-prompt with
unconfirmed observation stays Unknown. No production behavior or historical assertion changed.

First Host run used the final Host command without `xUnit.ParallelizeTestCollections=false`
and logger `local-solo-s2da-host-regression.trx`: 120 discovered, 119 passed, 1 failed,
0 skipped. Existing S2A reviewer-unavailable case returned Unknown instead of Completed
(6s case duration). Cause not established; no security alert was observed. Diagnostic:

```text
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~LocalSoloWorkflowTests.CompletedDoesNotImplySuccessfulReview' --logger 'trx;LogFileName=local-solo-s2da-host-diagnostic.trx' --results-directory TestResults/home-validation -- RunConfiguration.TestSessionTimeout=180000
```

All four diagnostic cases passed (4/4/0/0, 5s), with no code/test change, followed by the
final Host regression with parallel collections disabled. This non-reproduced historical
failure remains a validation limitation; the successful repeat does not prove its cause.
TRX artifacts, including the initial failures, remain ignored under TestResults/home-validation.
No endpoint-protection block or testhost crash was observed in tool output; security logs
were not probed and no security controls were changed.

The combined 201-case protected/source/Reviewer selection exceeded the 180-second session
bound. TRX records 120 completed results: 117 passed, 3 failed, 0 skipped; 81 selected cases
did not have completed results at abort. Three failures were existing Native Reviewer
methods (permission variant, uncertain assessment, changed source), with missing review/
expected callback. They are not counted as passing. This was the explicitly configured
test-session timeout, not an observed endpoint-protection termination. No bulk temporary
cleanup was attempted; an interrupted existing fixture may leave its isolated temporary
artifacts. Exact empty directories owned by new tests are cleaned non-recursively.

The remaining regression work was split by already-reviewed class with the same session
bound. The 66-case native class and 42-case coverage class passed. Full source class had
one missing-file candidate-count failure (58/59 pass); full protected class had one
cancel-wait observation-gate timeout (33/34 pass). Added bounded prerequisite diagnostics;
the exact two affected methods then passed all 11 parameter cases, without relaxing their
deadlines/assertions or changing production behavior. Causes remain unconfirmed; no baseline
comparison proves they predate S2D-A. This is **not a clean full-regression result**.

Exact class/final/diagnostic commands:

```text
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~NativeEvidenceReviewerTests' --logger 'trx;LogFileName=local-solo-s2da-native-final.trx' --results-directory TestResults/home-validation -- RunConfiguration.TestSessionTimeout=180000 xUnit.ParallelizeTestCollections=false
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~SourceCandidateIntegrationTests' --logger 'trx;LogFileName=local-solo-s2da-source-final.trx' --results-directory TestResults/home-validation -- RunConfiguration.TestSessionTimeout=180000 xUnit.ParallelizeTestCollections=false
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter '(FullyQualifiedName~ProtectedHandoffTests&FullyQualifiedName!~RealProcessCrashRecoversOriginalChildWithoutParentResumeOrPost&FullyQualifiedName!~CompetingProcessCannotExecuteOrInspectWhileOriginalOwnerIsActive)' --logger 'trx;LogFileName=local-solo-s2da-protected-final.trx' --results-directory TestResults/home-validation -- RunConfiguration.TestSessionTimeout=180000 xUnit.ParallelizeTestCollections=false
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~SourceEvidenceCoverageTests' --logger 'trx;LogFileName=local-solo-s2da-coverage-final.trx' --results-directory TestResults/home-validation -- RunConfiguration.TestSessionTimeout=180000 xUnit.ParallelizeTestCollections=false
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~SourceCandidateIntegrationTests.IndependentReaderAndPostReviewValidationRemainAuthoritative|FullyQualifiedName~ProtectedHandoffTests.InFlightObservationPreservesChildOwnershipAndNeverAborts' --logger 'trx;LogFileName=local-solo-s2da-targeted-diagnostic.trx' --results-directory TestResults/home-validation -- RunConfiguration.TestSessionTimeout=180000 xUnit.ParallelizeTestCollections=false
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~PersonalLabAdmissionTests|FullyQualifiedName~PersonalLabWorkflowTests|FullyQualifiedName~LocalExecutionPreparationTests' --logger 'trx;LogFileName=local-solo-s2da-lab-final.trx' --results-directory TestResults/home-validation -- RunConfiguration.TestSessionTimeout=180000 xUnit.ParallelizeTestCollections=false
```

| Validation | Discovered / passed / failed / skipped | Result |
| --- | --- | --- |
| Host / test-project final builds | N/A | Both exit 0; 0 warnings/errors |
| S2D-A focused (before last lifetime recheck) | 46 / 46 / 0 / 0 | 34 admission + 12 filesystem/Workflow; 9s |
| S2C preparation | 36 / 36 / 0 / 0 | 158ms |
| Final Lab + preparation, final source | 82 / 82 / 0 / 0 | Includes all 46 S2D-A + 36 S2C; 8s |
| Final Host/S1/S2A/S2B | 120 / 120 / 0 / 0 | 56 Host + 12 Workflow + 23 readiness + 29 output; 8s |
| Native Reviewer class | 66 / 66 / 0 / 0 | 56s |
| Source coverage class | 42 / 42 / 0 / 0 | 40s |
| Full source candidate class | 59 / 58 / 1 / 0 | 64s; failure retained |
| Full protected handoff class (no process cases) | 34 / 33 / 1 / 0 | 36s; failure retained |
| Targeted source/protected diagnostic methods | 11 / 11 / 0 / 0 | 12s; does not erase full-class failures |

Counters were read from TRX. Do not sum overlapping focused/diagnostic runs into a unique
pass count or represent the aborted 201-case selection as completed. There is no live-provider,
model semantics, process-crash, full-suite, production trust or corporate compatibility result.

## Owner review and smallest S2D-B scope

S2D-A implementation and new focused cases are ready for Owner review; unconditional full
acceptance is not recommended until the non-reproduced regression failures/session stability
are understood. It is not operational readiness. Before S2D-B, Owner must separately choose
its exact scope: resolve the **Lab-specific in-process/
durable provenance and original-receipt compatibility contract**, and the independently selected
three-recipient/native outbound and OpenCode downstream model restrictions. Keep production
trust and CLI blockers intact while validating those contracts offline first.

Smallest proposed S2D-B: one narrow disabled Lab composition/recipient-and-storage binding
increment on these seams, without production authority substitution or a new runtime/Registry.
Where actual API/SDK enforcement is unavailable, retain that exact blocker. Any first real
run needs additional explicit approval for the personal fixture, exact three recipients,
data disclosed, foreground task and budget; current admission/metadata is not that approval.

**STOP after S2D-A. No live provider, native inference, operational PersonalLab command,
production guard relaxation or S2D-B implementation.**
