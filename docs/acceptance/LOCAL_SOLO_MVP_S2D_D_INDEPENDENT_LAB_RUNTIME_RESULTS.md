# S2D-D — Independent PersonalLab offline runtime

Date: 2026-10-20 (Asia/Taipei). Baseline/main/origin: `c8de1fc7ef830d01c10aa3fad873ae8509c00578`.
**Recommendation: Conditional acceptance of the bounded offline ownership increment. No operational activation.**
Owner-confirmed personal, non-corporate-managed Windows PC; no corporate execution.
Unrelated .vscode/ preserved/excluded; no untracked .opencode/, secrets or company data read.

## Implementation plan and ownership decision

A. Reuse unchanged OpenCodeClient protocol/validation/observation, OpenCodeExecutor, official
WorkflowBuilder engine, existing Lab admission/journal/HTTP guard, BoundNativeRecipient,
4C reader/R1 Reviewer and S2A Host mapper.
B. Add one internal PersonalLabOfflineRuntime owning an offline HttpClient and exact
Lab-to-protocol access adapter. Existing journal supplies active/verified-session checks.
C. Do not use ITrustedCallerContext, policy snapshots, ProtectedExternalRuntime,
IApprovalStore, IProtectedPolicyAuthority or ISessionReceiptCoordinator as Lab permission.
D. Existing internal executor/access/delegation seams were unavailable to Host. Deliberate
InternalsVisibleTo(MAF.Host) enables trusted assembly composition without exposing a public
factory/CLI. Add one narrow owned-child lifetime marker because a second concrete owned
delegate must await child unwind instead of taking the generic timeout/abandon branch.
This is not malicious same-principal isolation or a new public authorization surface.

| Changed source/test | Responsibility |
| --- | --- |
| [PersonalLabOfflineRuntime](../../src/MAF.Host/PersonalLabOfflineRuntime.cs) | Offline-only internal factory, owned client/transport, original request binding, one child/journey, existing official executor and graph composition |
| [PersonalLabOfflineOperation](../../src/MAF.Host/PersonalLabOfflineOperation.cs) | DemandActiveSession rejects disposed/faulted/revoked/expired operation; returns session only after existing three-step receipt |
| [External AssemblyInfo](../../src/MAF.Workflows.External/AssemblyInfo.cs) | Internal trusted Host composition access; no public raw constructor behavior change |
| [ControlledHandoffWorkflow](../../src/MAF.Workflows.External/ControlledHandoffWorkflow.cs), [ProtectedHandoffDelegation](../../src/MAF.Workflows.External/ProtectedHandoffDelegation.cs) | IOwnedHandoffDelegation marker replaces concrete-type lifetime check; protected behavior preserved, no authority/effect logic changed |
| [IndependentLabRuntimeTests](../../tests/MAF.Tests/IndependentLabRuntimeTests.cs) | Independent no-SQLite/no-protected-authority journey, negatives, isolation, deterministic concurrency |
| [PersonalLabFirstRunBoundaryTests](../../tests/MAF.Tests/PersonalLabFirstRunBoundaryTests.cs), [SourceCandidateIntegrationTests](../../tests/MAF.Tests/SourceCandidateIntegrationTests.cs) | Only existing scripted NativeHttp / read-tool payload builder made internal to tests for reuse |
| README, Roadmap, this report | Current evidence, bounded acceptance and remaining gates |

No changes to ProtectedExternalRuntime, UnavailableTrust/UnavailableRuntimeAccess, policy,
SQLite, OpenCode protocol/route parser, native agents, source/R1 requirements, public Host
routes or production DI. No package, schema, migration, recovery loop or registry added.

## Actual independent call path

```text
LocalExecutionPreparation (immutable original task/run/recipients/budgets)
→ PersonalLabAdmission.AdmitOffline → PersonalLabOfflineOperation.Begin
→ PersonalLabOfflineRuntime.ForTests (owned LabOpenCodeBoundary → reviewed fake handler)
→ BoundNativeRecipient.BindAdapters (separate owned PCA / Reviewer)
→ unchanged official ControlledHandoffWorkflow
  PCA → Lab IOwnedHandoffDelegation → existing OpenCodeExecutor / official child Workflow
  → unchanged OpenCodeClient → active Lab access + flushed Lab HTTP effect guard
  → original session/structured read metadata → independent VerifiedSourceMode/BoundedSourceReader
  → native Reviewer with actual SDK tool result → HumanGate
→ operation.RecordOutcome → existing LocalSoloOutcomeMapper
```

The main journey constructs neither SyntheticProtectedHandoff nor synthetic production
caller/policy/approval/v4 store. It uses only raw RuntimeHttpFixture as an in-memory
protocol responder; that handler creates no authority. Source metadata builder and existing
scripted native HTTP responder are reused without borrowing their protected fixture setup.
There is **no SQLite database** on the independent journey. The separate historical lazy-
client quality test does construct SyntheticProtectedHandoff, without Initialize/authority
activation, solely to verify that existing fixture's client lifecycle; it is not Lab runtime
permission or proof of the independent journey.

The Lab Access implementation uses the existing internal mechanical effect-entry protocol,
not production authorization interfaces. It checks live original operation, exact task/root/
objective/budget/run/handle and verified session; it denies Abort/Reattach/unknown effects.
Before wire, LabOpenCodeBoundary checks exact method/path/model/session/permissions and
calls Reserve, whose journal Write/Flush(flushToDisk:true) precedes inner transport. No
caller-supplied policy evaluator or Allow callback is exposed. The outer graph's fixed
exact-intent Allow/Deny filter is advisory only; it cannot bypass Lab admission or supply
ProductionProtected authority. Protected runtime is never called by this Lab path.

ForTests accepts reviewed offline handlers only through internal controlled composition;
there is no operational factory or network fallback. A Network target is rejected before
inner transport receives anything. HttpClient owns boundary and inner handler and disposes
them once. The runtime requires awaiting child and journey before Dispose; concurrent
active-use disposal is rejected. This in-process factory is not a sandbox against malicious
trusted-assembly code injecting a socket handler. No real handler/client/server was used.

## Lifecycle, receipt and recipient semantics

Original OperationId/task/run/execution/message/workspace/ReadOnly intention and native
role budgets are retained. Prepared, admitted, durable Intent, original response candidate,
response correlation, FixtureVerifiedOriginalResponse, provider observation, advisory review
and human choice remain separate. One Create and Prompt intent; no retry/refund, replacement
session, implicit Abort or permission/question reply. Journal reopen remains inspection-only.

Structured providerID/modelID selector comes from preparation and is verified on message
observations by the unchanged Lab boundary. Requested labels do **not** attest OpenCode
configuration custody or downstream destination. DemandHarnessRecipient still rejects even
matching metadata, DemandExecution still rejects, and no fake attestation is inserted.
Native PCA/Reviewer use independent exact owned bindings, bounded attempts and fake HTTP
through the real SDK/Agent; no generic provider fallback or scripted prebuilt outcome.

Invalid receipt can persist a candidate session while the client cannot return a verified
session handle. Existing RecordOutcome correctly rejects that mismatched result with
LabProvenanceMismatch; the actual Unknown WorkflowOutcome is displayable through the mapper,
while the journal retains its candidate/correlation/uncertainty without a fabricated terminal
observation or verified receipt. This limitation is preserved, not weakened to make a write
succeed. It is part of the conditional acceptance boundary and needs explicit design if a
future product requires durable rejected-result presentation/rehydration.

## Focused evidence

Main journey variants and raw fake Create / Prompt / GET / Abort:

| Variant | External/review | Counts |
| --- | --- | --- |
| valid | Completed / SufficientEvidence / ReadyForHumanReview | 1 / 1 / 9 / 0 |
| lost-prompt | Completed from original observation / SufficientEvidence; Uncertain retained | 1 / 1 / 9 / 0 |
| lost-create | Unknown / TransportDisconnected / no review | 1 / 0 / 3 / 0 |
| invalid-receipt | Unknown / LabSessionPolicyMismatch / no verified outcome record | 1 / 0 / 3 / 0 |
| wrong-session | Unknown / LabAdmissionRevoked | 1 / 0 / 4 / 0 |
| changed-model / changed-provider | Unknown / LabAdmissionRevoked | 1 / 1 / 9 / 0 each |
| missing source | Completed / InsufficientEvidence | 1 / 1 / 9 / 0 |
| Reviewer unavailable | Completed / ReviewUnavailable | 1 / 1 / 9 / 0 |

Every row executes all four outer executor stages with one output and original identities;
human choice remains NotEstablished. Successful rows have reader-issued source references
and actual native tool result observation. Same-instance repeats return the same journey
without new effects. Journal asserts one Create intent and eligible one Prompt intent;
reopened exact identity and CanResume=false are retained. Duplicate Create is rejected.

Additional cases prove wrong task/run/execution, missing receipt before Submit/Observe,
revocation, deterministic expiry, closed journal write failure and Network target all
block inner effects. Journal write failure is converted by the existing executor into a
non-success result; subsequent Reserve fails and raw handler requests stay zero.
Raw public OpenCodeClient still returns ProtectedRuntimeRequired with an active Lab
journal/admission; Host submit and external entry remain ProductionTrustUnavailable.
Selected historical protected raw/binding tests also pass. This is supported composition
isolation, not proof against malicious same-account code or arbitrary reflection.

Concurrency: eight dedicated threads rendezvous at a Barrier, then read the historical
fixture's lazy Http simultaneously. They obtain the same client; a shared DelegatingHandler
cannot be bound into duplicate chains, no initialization throws, and quiescent repeated
Dispose disposes its owned chain once. No sleeps/stress loop. Concurrent Dispose versus
initialization and post-disposal use are unsupported, not asserted safe.

## Validation — personal Windows, cached dependencies

Build: `dotnet build tests/MAF.Tests/MAF.Tests.csproj --no-restore -m:1 -nr:false -v minimal`.
Final exit 0, **0 warnings / 0 errors**, 1.67s. No restore or new dependencies. An earlier
new-test analyzer warning was corrected before focused execution; production timeouts
unchanged. Ordinary independent integration uses explicit 5s preparation before binding;
existing default fixtures remain 2s; expiry uses a fake clock rather than widening budgets.

All test commands use this common form, independent bounded sessions:

```powershell
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter '<selection below>' --logger 'trx;LogFileName=<name below>.trx' --results-directory TestResults/home-validation -- RunConfiguration.TestSessionTimeout=180000 xUnit.ParallelizeTestCollections=false xUnit.MaxParallelThreads=1
```

| Selection | TRX basename | Discovered / pass / fail / skip | Duration |
| --- | --- | --- | --- |
| FullyQualifiedName~IndependentLabRuntimeTests | s2dd-independent-final | 19 / 19 / 0 / 0 | 1s |
| Exact three S2D-C methods below | s2dd-s2dc-compatibility | 15 / 15 / 0 / 0 | 3s |
| FullyQualifiedName~PersonalLabProvenanceTests OR ~BoundNativeRecipientTests OR ~LabOpenCodeBoundaryTests | s2dd-provenance-recipients | 52 / 52 / 0 / 0 | 774ms |
| Four selected RuntimeEnforcement methods below | s2dd-protected-isolation | 14 / 14 / 0 / 0 | 1s |
| Exact HistoricalLazyClientPublishesOneChainBeforeQuiescentDisposal | s2dd-lazy-client-minimal-root | 1 / 1 / 0 / 0 | 49ms |

The exact compatibility methods (FullyQualifiedName=MAF.Tests.<name>, joined by |):
LabOpenCodeBoundaryTests.OriginalExchangeAndUncertainDeliveryRemainDistinct;
PersonalLabFirstRunBoundaryTests.OriginalSessionModelPinAndOwnedNativeClientsUseOfficialWorkflow;
PersonalLabDurableWorkflowTests.DurableOfflineBoundaryFeedsExistingOfficialWorkflow.
Protected selection uses FullyQualifiedName~RuntimeEnforcementTests.<name>, joined by |:
AllPublicRawEntrypointsDenyWithoutContextBeforeStorageOrTransport;
UnsupportedOrForgedCompositionCannotFallBack;
EvenProtectedRawClientCannotChangeBindingOrElevateEffect;
SubmitNeverImpliesInspectAndInspectNeverImpliesSubmit.

Primary successful groups contain 100 result rows but **97 unique cases**: the three receipt
rows overlap compatibility and LabOpenCodeBoundaryTests. The final single concurrency rerun
also overlaps the 19-case group; it validates a final cleanup improvement removing unnecessary
Lab journal/source allocation from that quality case. Do not sum it as an additional case.

Historical new-test first run s2dd-independent-first: 17 cases, 15 pass / 2 fail, preserved.
The new tests initially expected RecordOutcome to accept an invalid-receipt handle and
expected closed-journal failure to escape as an exception. Static analysis confirmed both
were unsafe/incorrect expectations of existing behavior. Tests now assert rejection/non-
success plus unchanged zero-effect/persistence safety requirements; no existing guard or
historical assertion changed. Corrected run s2dd-independent-corrected passed 18/18; adding
the direct missing-receipt assertion yielded final 19/19. No blind retry or lost evidence.
All TRX artifacts remain ignored; no source test/production failure was concealed.

Selection reviewed before execution; no broad full suite, process-crash, real OpenCode,
model inference or socket traffic was authorized/performed. No endpoint alert or testhost
interruption observed; security logs not probed. Corporate incident/hold unchanged; home
success is not corporate clearance.

## Filesystem ownership

New explicit file-backed cases use semantic `maf-independent-lab/<case>/<run-id>/workspace`
and sibling storage directories. GUID is a separate run component; actual source file
Example.cs and operation-id.lab-journal formats retained. Workspace/source and exclusive
CreateNew/flushed journal are required by source/currentness and lifecycle assertions.
Cleanup closes handles, deletes only flat owned workspace/storage files and directories,
then that exact run root non-recursively. Parent namespaces are not globally cleaned.
Wrong-execution preparation test allocates no directory. Lazy-client quality case creates
only the existing fixture's necessary empty owned root, not a Lab journal/source/SQLite.
Legacy regression fixture naming remains unchanged; no antivirus workaround or global cleanup.

## Acceptance conditions and remaining live blockers

Independent offline Lab ownership is delivered; no synthetic ProductionProtected authority
is needed on that journey. Recommend **Conditional** bounded offline Owner acceptance:
retain invalid-receipt outcome-recording and quiescent disposal limitations above. No claim
of generic recovery, malicious same-principal protection or live trust/recipient proof.

Before a first real ReadOnly run: separately review cooperative risk acceptance, exact
workspace/task/budgets/all three recipients, OpenCode downstream configuration custody/
attestation gap, installed-server compatibility and an owned operational Lab transport
factory with explicit activation scope. Network is currently rejected; production genuine
identity/policy/activation/custody and corporate clearance remain separate unimplemented gates.
No journal resume, --local personal-run, live factory, second harness, registry, IAM or Hybrid.
Next increment must be separately authorized; **STOP after S2D-D**.
