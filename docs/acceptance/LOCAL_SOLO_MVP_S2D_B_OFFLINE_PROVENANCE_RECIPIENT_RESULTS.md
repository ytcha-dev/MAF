# S2D-B — offline Lab provenance and recipient boundaries

## Scope and actual implementation

2026-10-20, personal Windows. Baseline/HEAD/main/origin/main matched
`74a03cb8aae904498781036d75bdb4a54fdb37be`. Only pre-existing untracked `.opencode/`
was present; not inspected. S2D-A is Owner-conditionally-accepted as an offline foundation;
this does not establish full-suite clearance or live execution approval.

* [PersonalLabOfflineOperation.cs](../../src/MAF.Host/PersonalLabOfflineOperation.cs): internal,
  exclusive per-operation journal, durable intent-before-effect, inspection-only reopen and
  existing Host outcome mapping. Small Lab-specific binding; no general store/authority framework.
* [PersonalLabAdmission.cs](../../src/MAF.Host/PersonalLabAdmission.cs): internal exact
  preparation/state checks at begin and after durable IO. Existing guards unchanged.
* [BoundNativeRecipient.cs](../../src/MAF.Host/BoundNativeRecipient.cs): concrete role-specific
  OpenAI SDK/client/transport ownership and pre-wire checks; normal Network effects still denied.
* New tests: PersonalLabProvenanceTests, BoundNativeRecipientTests, PersonalLabDurableWorkflowTests.
  Existing PersonalLabWorkflowTests.GuardedScript gains an optional durable offline reservation
  using the same scripted clients; default behavior is unchanged. README/ROADMAP link this report.

No Program.cs/CLI registration, external runtime, SQLite schema/approval/receipt, policy engine,
provider permission rule or production timeout changed. No package/restore/install, live model,
OpenCode server, process-crash/competition acceptance test or company/security probe.

## Durable semantics and reused boundaries

The app chooses an existing local storage directory **outside** the selected ReadOnly workspace.
The filename is only the original validated operation ID plus `.lab-journal`; no model/CLI path.
CreateNew never overwrites evidence. FileShare.None holds one cooperative Windows writer;
each record uses WriteThrough plus Flush(true). Limits: 1MiB, bounded sequence/effect counts.
There is no background writer, reset or migration. Persistence failure revokes the in-memory
admission and does not return a usable durable operation.

The versioned header retains original OperationId, TaskId/WorkflowRunId/objective/workspace,
ExecutionId/OpenCode/request ID, ReadOnly/profile, all three recipient configurations and budgets.
SHA-256 of the serialized full identity is an **intention fingerprint, not an authenticated seal**.
Intent, Returned, Uncertain, provider observation, consumed/revoked records are distinct. Session
IDs from a returned fake Create are only unverified candidates. Provider status, Review status
and HumanGate status are retained separately; no human acceptance or generic Approved flag.

Reservation goes through existing PersonalLabAdmission, then flushes Intent **before** the
controlled effect. Missing/lost callbacks cannot refund Create/Prompt. Admission lifetime/revocation/cancellation and caller cancellation
are rechecked after flush, so disk IO cannot extend effect authority. A new writer cannot
reattach to the same file. Inspection validates format/sequence/fingerprint and exact binding;
partial records block inspection, and complete pending/uncertain records remain Unknown/blocked.
Reopen returns data only: CanResume=false, Receipt=NotEstablished, HumanAcceptance=NotEstablished.
It never recreates preparation/admission or launches/resubmits a task.

SQLite v4 remains the authoritative **protected-fixture** execution/ownership, atomic admission,
intent and original-response receipt mechanism. The existing protected runtime requires caller/
policy authority, its owned receipt transport and exact receipt registration; Lab journal rows
cannot satisfy these interfaces. No Lab approvals are inserted into production tables. The new
journal therefore remains independent rather than falsely reusing v4 authorization rows.
Writable local storage is not protected custody/anti-rollback/human consent. Same-principal
malice is outside PersonalLab's cooperative trust model; inspection is never trusted authority.

## Recipient enforcement and explicit gaps

Native PCA and Reviewer have separate immutable role/configuration/model/endpoint identities.
The owned factory supports the existing OpenAI SDK only (`Provider=openai`, HTTPS); no generic
AI_MODEL/AI_BASE_URL fallback, arbitrary IChatClient proof or operational transport injection.
Its owned adapter-pair binding checks operation and full role/configuration identities even
when both roles select the same endpoint/model. Raw S2C metadata compatibility is not that proof.
Immediately before the owned HTTP transport, it checks exact POST destination/path/Host and a
single matching JSON model field, with bounded body buffering. SDK retry=0; the owned handler
disables automatic redirects, proxy and cookies. Redirect responses or changed final URI fail.
Current admission revocation/consumption and durable intent are checked before each fixture send.
The explicit friend-test transport seam is analogous to ReceiptTransport.ForTests and has no
Host registration. Real factory construction always selects Network, which admission rejects
before any inner send, even with matching metadata/credentials. No inference authorization exists.

These checks constrain the logical HTTPS recipient and requested model, **not** DNS/IP custody,
server honesty, vendor identity behind a custom endpoint, server-side failover, remote tools or
downstream routing. Metadata-only S2C BindNativeAdapters remains compatibility only. Unsupported
protocol/provider/HTTP configurations stay blocked; credentials/payloads are not journaled/logged.

OpenCode's [server API](https://docs.opencode.ai/docs/server/) supports a model selector for
message/prompt_async, but its [provider configuration](https://opencode.ai/docs/providers/)
can choose a custom baseURL. Selection labels/assistant model IDs are not reliable downstream
recipient attestation. Audited OpenCodeClient.SubmitAsync currently sends messageID/agent/parts,
not a pinned model; its Scope covers the server/root/agent/read policy, not downstream recipients.
No new model-pin field was presented as authorization or added to protected scope/receipts.
DemandHarnessRecipient still throws HarnessRecipientVerificationUnavailable even for equal
configuration strings. Harness Network effects remain unavailable. A model-pinned Lab adapter
and controlled server-recipient configuration/observation contract are still missing.

## Existing Workflow integration

Five new cases run preparation → exact Lab admission → durable operation → existing official
PCA/ExternalDelegation/EvidenceReviewer/HumanGate Workflow → RecordOutcome → LocalSoloOutcomeMapper.
They reuse the existing fake HTTP/v4 protected fixture, scripted native PCA/Reviewer, actual
independent BoundedSourceReader and native evidence-tool observations. No second orchestrator.
Valid, lost Create, lost Prompt, Reviewer unavailable and insufficient-source outcomes retain
their original meanings. Lost Prompt may reconcile Completed from the original session while
the durable uncertain delivery remains recorded and non-retryable. Lab inspection never upgrades
that observation into a verified receipt. All views remain ControlledOfflineFixture / NotLiveProvider;
HumanGate readiness never becomes human acceptance. Native owned-SDK HTTP checks are tested
separately; a complete live three-recipient composition is **not** claimed.

## Commands and results

Prerequisite before new behavior: one reviewed 39-case serial selection, including all 37
invalid-metadata rows (historical traversal) and two positive protected admission/recovery cases.
39/39 passed, 0 failed/skipped, 34s. Default 2s and original assertions retained; no budget fix
was necessary. Historical R1/R2 failed runs are not overwritten by this success.

```powershell
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~InvalidMetadataCannotUpgradeCompletedExecutionToVerifiedEvidence|FullyQualifiedName~FullNestedOfficialGraphUsesRealV4AdmissionReceiptAndEffects|FullyQualifiedName~SubmitAloneCannotObserveAndRecoveryRequiresIndependentInspect' --logger 'trx;LogFileName=s2db-prerequisite.trx' --results-directory TestResults/home-validation -- RunConfiguration.TestSessionTimeout=180000 xUnit.ParallelizeTestCollections=false
dotnet build tests/MAF.Tests/MAF.Tests.csproj --no-restore -m:1 -nr:false -v minimal
```

Build history retained: initial 80.00s build failed with 0 warnings/3 errors (missing using and
DTO edits made after the Host dependency compiled). Fixed using, waited for completion and
rebuilt settled sources. Successful builds: 19.29s, 20.76s, 15.74s, final test-only strengthening
7.80s, each 0 warnings/errors; dependencies include Host/external/persistence. No test executed
from the failed build. Initial focused 31/31 passed, 9s; final focused adds the pending-intent
reopen case and same-operation mutation checks, not another retry to conceal a failed test.

```powershell
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~PersonalLabProvenanceTests|FullyQualifiedName~BoundNativeRecipientTests|FullyQualifiedName~PersonalLabDurableWorkflowTests' --logger 'trx;LogFileName=s2db-focused.trx' --results-directory TestResults/home-validation -- RunConfiguration.TestSessionTimeout=180000 xUnit.ParallelizeTestCollections=false
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~PersonalLabProvenanceTests|FullyQualifiedName~BoundNativeRecipientTests|FullyQualifiedName~PersonalLabDurableWorkflowTests|FullyQualifiedName~PersonalLabAdmissionTests|FullyQualifiedName~PersonalLabWorkflowTests|FullyQualifiedName~LocalExecutionPreparationTests|FullyQualifiedName~LocalSoloHostTests|FullyQualifiedName~LocalSoloWorkflowTests|FullyQualifiedName~LocalSoloReadinessTests|FullyQualifiedName~ExternalConsoleOutputTests' --logger 'trx;LogFileName=s2db-lab-host-final.trx' --results-directory TestResults/home-validation -- RunConfiguration.TestSessionTimeout=180000 xUnit.ParallelizeTestCollections=false
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~IndependentReaderAndPostReviewValidationRemainAuthoritative|FullyQualifiedName~InFlightObservationPreservesChildOwnershipAndNeverAborts|FullyQualifiedName~CompletedDoesNotImplySuccessfulReview|FullyQualifiedName~InvalidAgentBehaviorCannotChangeCompletedFactsOrGrantApproval|FullyQualifiedName~AdvisoryNegativeResultsRemainNonExecutable|FullyQualifiedName~IndependentSourceValidationSurroundsNativeAgent|FullyQualifiedName~InvalidMetadataCannotUpgradeCompletedExecutionToVerifiedEvidence|FullyQualifiedName~FullNestedOfficialGraphUsesRealV4AdmissionReceiptAndEffects|FullyQualifiedName~SubmitAloneCannotObserveAndRecoveryRequiresIndependentInspect' --logger 'trx;LogFileName=s2db-protected-source-final.trx' --results-directory TestResults/home-validation -- RunConfiguration.TestSessionTimeout=240000 xUnit.ParallelizeTestCollections=false
```

| Selection | Discovered / passed / failed / skipped | Runner / TRX session seconds |
| --- | --- | --- |
| Baseline prerequisite | 39 / 39 / 0 / 0 | 34 / 37.636 |
| Initial new focused | 31 / 31 / 0 / 0 | 9 / 12.900 |
| New + admission/preparation/Host | 234 / 234 / 0 / 0 | 16 / 20.546 |
| Protected/source/Reviewer | 94 / 94 / 0 / 0 | 53 / 55.352 |
| New focused, after pure-memory Network-denial transport strengthening | 32 / 32 / 0 / 0 | 7 / 10.145 |
| Owned SDK → existing native adapter compatibility assertions, both roles | 2 / 2 / 0 / 0 | 0.386 / 2.796 |
| After post-flush lifetime recheck / added expiry case | 33 / 33 / 0 / 0 | 5 / 7.918 |
| Final new cases, including equal-endpoint/model role-swap rejection | 34 / 34 / 0 / 0 | 6 / 8.805 |

234 = 32 new + 82 Lab/preparation + 120 Host. The 94 selection includes all 55 R3 cases
plus the 39 prerequisite cases; four Host cases overlap. Do not sum overlapping reruns into
a unique/full-suite pass count. The traversal parameter passed both before implementation
and in the final targeted regression; no existing source assertion/budget was changed.
No failed/incomplete test sessions, skipped cases or unexpected timeout were observed in
these selections. Historical failures and unselected suites remain historical/unvalidated.

The final fake-Network transport/zero-inner-call strengthening built successfully (19.08s,
0 warnings/errors), then only the 32 new tests were rerun. Both owned SDK clients additionally
bound to the existing TaskBriefingAdapter/NativeEvidenceReviewer without requests during
binding; added compatibility assertions built successfully (10.14s, 0 warnings/errors), then
only those two strengthened cases were run. All operational Network factories remain disabled.
Final review added the post-flush admission recheck plus expiry test (build 17.19s), then the
owned operation/role-pair check plus same-endpoint/model positive HTTP test (build 21.18s).
Both builds had 0 warnings/errors; only the affected new classes were rerun (33, then 34 cases).
The older Host/protected paths were unchanged; no broad repeat was needed. No unsuccessful
test was hidden: all source refinements/strengthening followed passing tests, not retries.
Exact additional commands, same test flags/results directory:

```powershell
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~PersonalLabProvenanceTests|FullyQualifiedName~BoundNativeRecipientTests|FullyQualifiedName~PersonalLabDurableWorkflowTests' --logger 'trx;LogFileName=s2db-focused-final.trx' --results-directory TestResults/home-validation -- RunConfiguration.TestSessionTimeout=180000 xUnit.ParallelizeTestCollections=false
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~BoundNativeRecipientTests.OwnedSdkPinsEachRoleIndependently' --logger 'trx;LogFileName=s2db-owned-adapter-compatibility.trx' --results-directory TestResults/home-validation -- RunConfiguration.TestSessionTimeout=180000 xUnit.ParallelizeTestCollections=false
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~PersonalLabProvenanceTests|FullyQualifiedName~BoundNativeRecipientTests|FullyQualifiedName~PersonalLabDurableWorkflowTests' --logger 'trx;LogFileName=s2db-postflush-final.trx' --results-directory TestResults/home-validation -- RunConfiguration.TestSessionTimeout=180000 xUnit.ParallelizeTestCollections=false
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~PersonalLabProvenanceTests|FullyQualifiedName~BoundNativeRecipientTests|FullyQualifiedName~PersonalLabDurableWorkflowTests' --logger 'trx;LogFileName=s2db-role-binding-final.trx' --results-directory TestResults/home-validation -- RunConfiguration.TestSessionTimeout=180000 xUnit.ParallelizeTestCollections=false
```

Discovery/effects reviewed before execution. The longer group combines
39 prerequisite + 55 R3 cases (~80s previously measured); 240s is a session bound only. R3's
selected 5s operations, five 2s in-flight cases and independent 2s Reviewer deadline are unchanged.
TRX stays ignored under TestResults/home-validation; no full-suite claim or process tests.

## Filesystem, acceptance and next gate

Pure recipient/metadata/denial cases use the existing output-directory metadata and allocate
no directories. No xUnit constructor/field allocates one. Explicit file-backed tests use one
owned root plus an empty workspace, or the existing SQLite/source fixture and one separate
journal directory. New cleanup deletes only known owned flat files and empty directories;
no global/unknown-directory cleanup. A simulated pending intent closes all streams without
inventing a response; it launches/kills no process. Disposable v4 databases use existing reviewed
fixture setup; there is no new SQLite schema or migration.

Recommendation: **accept the bounded S2D-B offline implementation for Owner review; hold all
live execution**. No unresolved failure in the selected runs; no full-suite/corporate clearance.
ProductionProtected/ProductionTrustUnavailable and PersonalLabNotApproved
remain unchanged. First live execution is still blocked by Lab-specific original-session/
receipt runtime compatibility, OpenCode model pin/configuration custody/downstream limits,
and independent Owner approval of the exact workspace, objective/budgets and three data recipients.
The smallest next software step is a separately authorized, still-disabled Lab adapter/receipt
composition preserving original-session semantics, not a synthetic production authority.
The key Owner decision before live work is whether to accept the explicit cooperative downstream
trust limits and approve the exact three recipient/data-flow configurations; this report is not
that approval and software blockers must still be resolved. No CLI activation or live validation.
**STOP after S2D-B.**
