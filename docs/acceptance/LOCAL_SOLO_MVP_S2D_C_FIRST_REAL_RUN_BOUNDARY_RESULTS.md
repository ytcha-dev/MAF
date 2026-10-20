# S2D-C — first real-run boundary, offline only

**HOLD — validation stopped after an endpoint-protection event reported by Owner.**
This commit is an implementation/evidence handoff for static GPT/Owner review, not S2D-C
acceptance. Do not resume affected builds/tests or live work without explicit clearance.

## Scope and implementation

Personal Windows, 2026-10-20. HEAD/main/origin/main matched required baseline
`f96564a6d9c8e8263a62e0dc09a880f75b81b868`. Only pre-existing untracked `.opencode/`
was present and was not inspected. No credentials, private provider settings, company data,
provider probes, model requests, new packages, restore, process-launch tests or live CLI.

Changed code:

- [LabOpenCodeBoundary.cs](../../src/MAF.Host/LabOpenCodeBoundary.cs): internal serial HTTP
  boundary, original-exchange verification, ReadOnly session checks and exact Lab model selection.
  Only a friend-test factory exists; no operational handler/DI/CLI construction.
- [PersonalLabOfflineOperation.cs](../../src/MAF.Host/PersonalLabOfflineOperation.cs): durable
  protocol binding and separate candidate/correlation/fixture-receipt events; ordered inspection.
- [SyntheticProtectedHandoff.cs](../../tests/MAF.ApprovalAcceptance/SyntheticProtectedHandoff.cs):
  test-owned optional handler before first HTTP-client use; fake response/request association.
  Default fixture behavior remains the existing protected composition. Disposal does not
  instantiate an unused HTTP client.
- New LabOpenCodeBoundaryTests and PersonalLabFirstRunBoundaryTests; minimal README/ROADMAP links.

Exact controlled call path:

```text
Prepare (original task/run and three bindings) → AdmitOffline (original execution)
→ PersonalLabOfflineOperation.Begin (original message ID, independent journal)
→ LabOpenCodeBoundary (durably bound protocol before any effect)
→ BoundNativeRecipient owned PCA/Reviewer SDK clients with scripted HTTP handlers
→ existing ControlledHandoffWorkflow: PCA → ExternalDelegation → EvidenceReviewer → HumanGate
→ existing protected v4 fixture child / ReceiptTransport / OpenCodeClient
→ Lab HTTP boundary → ResponseGate → existing RuntimeHttpFixture
→ independent BoundedSourceReader / native evidence tools
→ operation.RecordOutcome → existing LocalSoloOutcomeMapper
```

The protected child still has its **independently issued test-fixture** authority. Lab admission
and journal never satisfy that authority. This is a controlled integration test, not a completed
operational Lab runtime or a synthetic production activation route.

## Original-session rules and v4 compatibility

The original journal identity retains OperationId, TaskId, WorkflowRunId, ExecutionId, workspace,
OpenCode/ReadOnly, original message ID, objective, budgets and three recipient configurations.
The additional protocol event fixes server origin, agent and the same Harness configuration.
The adapter checks Create title/execution/run/agent, exact origin/workspace query, original
Prompt message/agent/objective and original-session-only observation routes. No model supplies
paths, factories, authority or a replacement execution identity.
The final operation-level guard also denies Prompt/Observe for a protocol-bound operation until
all three receipt steps exist, even if an internal caller omits the HTTP adapter.

Every controlled send reserves/flushed Intent through the existing admission/journal first.
Create is single-use even when its response is lost. A successful original POST response must
be associated with the **same captured request object, URI and method**; redirects/substitute
responses fail. Session ID syntax is bounded. Distinct durable events then record:

1. `LabSessionCandidate`: ID from that original returned Create body.
2. `LabResponseCorrelated`: in-memory original exchange checks passed.
3. `LabReceiptVerified`: response workspace and exact requested deny-default ReadOnly permission
   rules match. Flush must succeed before this adapter permits Prompt.

Candidate/correlation alone do not verify a receipt. Invalid policy revokes admission; failed
journal writes do not permit later effects. Tests close the actual journal before response
recording to exercise persistence failure; no simulated successful receipt is substituted.
Prompt reserves once before dispatch; a lost acknowledgement cannot cause a second POST.
Reconciliation uses only original-session GETs; no Abort, permission reply, shell or replacement.
GET session identity/workspace/permissions are rechecked. Provider terminal state is determined
by the existing OpenCode observation logic, separately from Lab receipt, review and HumanGate.

Inspection reports `FixtureVerifiedOriginalResponse / NotProductionAuthenticity` only after
the ordered receipt chain. Older S2D-B journals remain `NotEstablished`. Pending/uncertain
delivery stays recorded; `CanResume=false`, human acceptance stays `NotEstablished`. Inspection
never restores admission. Writable journal/hash/response labels are not protected custody,
anti-rollback, authenticated server evidence or protected human consent.

Reused v4 invariants: intent-before-wire, single-use original response association, independent
session policy checking, original handle/message correlation and uncertain delivery without POST
replay. Existing v4 SQLite ownership/admission/receipt mechanisms still run in the fixture.
No SQLite schema or production scope/receipt format changed. `ISessionReceiptCoordinator`
requires genuine policy/caller/ownership and its issued `SessionReceiptRegistration`;
`IOriginalCreateAccess` and `IControlledDelegation` are internal to External. The Host Lab boundary
cannot and does not manufacture those interfaces or receive a new InternalsVisibleTo grant.

## Model selection and recipient limits

Official [server documentation](https://opencode.ai/docs/server/) states prompt_async uses the
message request body. The pinned official [v1.2.27 generated SDK contract](https://github.com/anomalyco/opencode/blob/v1.2.27/packages/sdk/js/src/gen/types.gen.ts)
(`SessionPromptAsyncData`) defines optional `model: { providerID: string, modelID: string }`.
The moving dev SDK/source URLs returned 404 during documentation lookup; no provider was probed.

The separate Lab handler adds that structured field from immutable preparation to the existing
OpenCode Prompt, requires exact provider/model if already present, and rejects extra selector
fields. MAF never dispatches an omitted/default model through this seam. Existing
ProductionProtected SubmitAsync behavior and authorization scope remain unchanged.
Assistant messages for observation must match original session/parent message and both selected
model/provider labels. Missing or changed labels fail closed before successful observation.
This is request selection and response-label compatibility, **not downstream attestation**.

Native PCA/Reviewer reuse BoundNativeRecipient without a second factory: role/operation/endpoint/
model checks, owned SDK/transport, retries/redirects/proxy disabled, reservation before fake send.
The integration actually uses these clients and native adapters, including evidence-tool calls.
Metadata equality alone never authorizes outbound effects. All Network reservations remain denied.

Server configuration custody, custom provider baseURL, server-side failover, proxies, downstream
tools/egress and whether the server obeys its requested label are not established. Loopback and
matching model labels cannot prove them. A future cooperative Owner must explicitly trust and
approve those limits and the exact three data flows. Actual installed server schema/version
compatibility must be checked in a separately authorized phase; no live compatibility is claimed.
`DemandHarnessRecipient` still reports unavailable attestation, and no effect is enabled by pinning.

## Validation evidence

Reviewed effects: fake HTTP handlers only; native SDK responses scripted in memory; disposable
source/SQLite fixtures plus the separate journal. No sockets or external programs in selections.
Default operation budget remains 2s; new ordinary integration cases explicitly bind 5s before
task/policy/admission construction. Existing intentional short deadline/cancellation cases unchanged.

Build command (Host and all necessary dependencies included), no restore:

```powershell
dotnet build tests/MAF.Tests/MAF.Tests.csproj --no-restore -m:1 -nr:false -v minimal
```

Builds: initial 74.08s succeeded with 1 CS8602 warning/0 errors. Nullable disposal corrected;
18.08s, 9.11s and 11.41s builds succeeded with 0 warnings/0 errors. The 18.08s build also added
bounded failure output. The 9.11s build contains the query correction described below;
11.41s adds two receipt-policy rejection cases without further source changes.
Final static review then added the operation-level receipt prerequisite and corresponding
assertions, plus a per-fixture lock around lazy HTTP construction to preserve single-client
ownership. Final build succeeded in 42.48s with 0 warnings/0 errors. The following final combined
test attempt was interrupted; **these last changes have build evidence but no completed test evidence**.

Test command template; exact selections and filenames follow:

```powershell
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter '<selection>' --logger 'trx;LogFileName=<file>' --results-directory TestResults/home-validation -- RunConfiguration.TestSessionTimeout=<milliseconds> xUnit.ParallelizeTestCollections=false
```

| Selection / TRX | Discovered / passed / failed / skipped | Runner / session seconds |
| --- | --- | --- |
| New, `s2dc-focused.trx` | 28 / 24 / 4 / 0 | 8 / 11.387 |
| Query fix, `s2dc-focused-query-fix.trx` | 28 / 28 / 0 / 0 | 9 / 12.817 |
| Added receipt-policy cases, `s2dc-focused-final.trx` | 30 / 30 / 0 / 0 | 9 / 13.092 |
| S2D-B/Lab/preparation/Host, `s2dc-lab-host.trx` | 236 / 236 / 0 / 0 | 29 / 33.417 |
| Protected/source/Reviewer, `s2dc-protected-source.trx` | 94 / 94 / 0 / 0 | 70 / 73.442 |
| Final post-review attempt, `s2dc-postreview-final.trx` | 0 recorded / 0 / 0 / 0; **session aborted** | no completed cases / 3.301 |

New selection (180000ms): `FullyQualifiedName~LabOpenCodeBoundaryTests|FullyQualifiedName~PersonalLabFirstRunBoundaryTests`.
236 selection (180000ms): union of `FullyQualifiedName~` PersonalLabProvenanceTests,
BoundNativeRecipientTests, PersonalLabDurableWorkflowTests, PersonalLabAdmissionTests,
PersonalLabWorkflowTests, LocalExecutionPreparationTests, LocalSoloHostTests, LocalSoloWorkflowTests,
LocalSoloReadinessTests, ExternalConsoleOutputTests. This includes all 34 S2D-B cases.
The 236 run precedes the final two added tests; its source binaries already contain the final query fix.
The 94 selection (240000ms) is the union of `FullyQualifiedName~`:
IndependentReaderAndPostReviewValidationRemainAuthoritative,
InFlightObservationPreservesChildOwnershipAndNeverAborts, CompletedDoesNotImplySuccessfulReview,
InvalidAgentBehaviorCannotChangeCompletedFactsOrGrantApproval, AdvisoryNegativeResultsRemainNonExecutable,
IndependentSourceValidationSurroundsNativeAgent, InvalidMetadataCannotUpgradeCompletedExecutionToVerifiedEvidence,
FullNestedOfficialGraphUsesRealV4AdmissionReceiptAndEffects,
SubmitAloneCannotObserveAndRecoveryRequiresIndependentInspect.
It retains the R3 short-deadline cases and historical traversal scenario. Counts overlap across
runs (including four Host cases in the 94); never add them into a unique/full-suite count.

Final attempted command (not a successful 64-case result):

```powershell
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~LabOpenCodeBoundaryTests|FullyQualifiedName~PersonalLabFirstRunBoundaryTests|FullyQualifiedName~PersonalLabProvenanceTests|FullyQualifiedName~BoundNativeRecipientTests|FullyQualifiedName~PersonalLabDurableWorkflowTests' --logger 'trx;LogFileName=s2dc-postreview-final.trx' --results-directory TestResults/home-validation -- RunConfiguration.TestSessionTimeout=180000 xUnit.ParallelizeTestCollections=false
```

## Endpoint-protection stop and handoff

At approximately 2026-10-20 16:41 (UTC+08), VSTest reported that the test-host child process
crashed and the session was aborted. The local TRX records adapter discovery/start messages,
zero individual test results and no exception stack identifying a source defect. Command wall
time was about 9s; TRX session duration was 3.301s, not a 180s timeout. Owner subsequently
reported that testing hit antivirus and explicitly ordered tests stopped.

Confirmed: test-host termination/session abort, no completed cases in this attempt, and Owner's
security-event report. Not established: antivirus product/detection rule, quarantined resource,
specific affected test or responsible code path, false-positive status, or whether the last
code changes contributed. No OS/security-log or private-configuration collection was performed.
Do not classify this as the earlier query failure, a passed run, skipped cases, or a product
defect established by evidence. No retry, alternative execution path, exclusion, security-setting
change or cleanup of potentially affected resources was attempted after the event.

All affected execution is paused. Raw TRX remains locally ignored under TestResults/home-validation;
it contains machine/user metadata and is intentionally not published. This sanitized report
preserves the unsuccessful results. Remaining work is static GPT review, security-event review
and explicit clearance before any affected build/test rerun. Do not infer clearance from a push.

Initial failures: `valid`, `lost-prompt`, `missing`, `reviewer-unavailable` expected Completed but
returned Unknown. Static inspection identified the new allowlist omitted the existing
ReadMessagesAsync `&limit=100` query. Corrected only the Lab seam to permit exactly that limit
and a bounded `msg_...` before cursor; no assertion, production timeout or provider status mapping
changed. The unsuccessful TRX is retained. Passing later runs do not erase it. No timeout-based
retry or repeated green-run strategy was used.

Seven official-Workflow cases (all ControlledOfflineFixture / NotLiveProvider):

| Case | Provider / review | Create / Prompt / GET / Abort |
| --- | --- | --- |
| valid | Completed / SufficientEvidence, real evidence tool results delivered | 1 / 1 / 9 / 0 |
| lost-prompt | Completed from original observation / SufficientEvidence; durable uncertainty retained | 1 / 1 / 9 / 0 |
| lost-create | Unknown / Reviewer not reached | 1 / 0 / 3 / 0 |
| missing source | Completed / InsufficientEvidence | 1 / 1 / 9 / 0 |
| Reviewer unavailable | Completed / ReviewUnavailable | 1 / 1 / 9 / 0 |
| changed or missing observation model | Unknown / Reviewer not reached | 1 / 1 / 9 / 0 each |

Every case retains original task/run/execution identity, all four executor stages and human
choice NotEstablished. Positive cases independently read source twice and receive actual native
Reviewer tool results; no prebuilt success outcome. Model rejections revoke Lab admission;
the child's final error is LabAdmissionRevoked. Lost Create error is TransportDisconnected.
These are the completed pre-final-review runs; the final combined attempt supplies no new
Workflow evidence. The final operation guard and lazy-client lock remain unvalidated by tests.

## Filesystem and remaining gate

Four pure selector cases allocate no directories. File-backed lifecycle cases explicitly invoke
the existing owned journal fixture; no xUnit constructor/field allocates one. Workflow cases
explicitly request existing SQLite/source fixtures and one journal directory outside the ReadOnly
workspace. New cleanup deletes only exact owned flat files/empty directories; existing reviewed
SQLite/source cleanup remains unchanged. No global cleanup, directory relocation or new eager setup.

Recommendation: **HOLD — implementation handed off; final validation and Owner acceptance pending.**
No further testing/build/executable workflow after the security event without clearance.
ProductionTrustUnavailable, PersonalLabNotApproved, protected approval/ownership,
v4 receipt verification, independent evidence, native tool observation and HumanGate distinctions
remain intact. No public command/fallback, protected authority registration or production runtime
change. Historical R1/R2 failures remain untouched; no full-suite/corporate clearance is claimed.

Completed: Lab HTTP/original-response seam, ordered durable fixture receipt, exact model selection,
owned native-client/official-Workflow fixture integration, lifecycle/negative test sources, and
the successful scoped runs above. Incomplete: final-snapshot test validation, security-event
root cause/clearance, Owner/GPT review and any operational/live integration.

Remaining software blockers: no Lab-authorized child delegation/runtime interface replacing the
independent protected test fixture; no operational owned Lab transport; no installed-server schema
validation or downstream-recipient attestation/custody enforcement. The smallest next action is
static GPT/Owner review of this commit and the explicit cooperative routing assumptions. Review
the endpoint-protection event before authorizing any affected execution. No next milestone is started.
Any first live task still needs a separate exact workspace/objective/budgets/three-recipient approval
after software blockers are resolved. **STOP after S2D-C; no live run is authorized here.**
