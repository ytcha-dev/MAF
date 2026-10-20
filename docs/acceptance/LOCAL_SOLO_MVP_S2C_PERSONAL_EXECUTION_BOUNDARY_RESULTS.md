# Local Solo MVP-S2C — Personal execution preparation boundary

Date: 2026-10-20. Baseline: `433423db6ec4670ea9e67c016fb3d6b98e5523d7`,
`main`; baseline tracking ref matched `origin/main`. The existing untracked `.opencode/`
was neither inspected nor changed. Scope: Owner-authorized personal Windows implementation
and offline validation only. No real model/OpenCode activation is authorized by this report.

## Delivered code and execution status

[LocalExecutionPreparation](../../src/MAF.Host/LocalExecutionPreparation.cs) is an internal,
application-owned preparation component, not a CLI action or trusted capability. It:

- Validates a bounded objective, canonical selected local directory using the existing
  ancestor/reparse checks, fixed `OpenCode`, fixed `ReadOnly`, and 10ms–120s stage/task-request
  timeout. The timeout is the existing `HandoffTask.StageTimeout` / external request budget,
  not a new total parent deadline or monetary/token limit.
- Requires explicit PersonalLab or ProductionProtected profile and three independent
  recipient configuration values: PCA, Reviewer, Harness. Native bindings contain role,
  configuration revision identity, endpoint, provider and model. Harness configuration
  contains revision identity, provider and model; it does not claim downstream attestation.
- Allocates a correlation operation identity and existing immutable `HandoffTask`.
  A private constructor/get-only state preserves the complete immutable value snapshot.
  `DemandExact` rejects changed objective, workspace, access, Harness, profile, timeout or
  any recipient field. Changed intent requires a newly prepared operation; no in-place update.
  The workspace is rechecked before adapter binding. This is not filesystem snapshot isolation.
- Captures existing evidence budget metadata: eight sources, twenty lines per source,
  eight Reviewer model calls and 32768 Reviewer tool-output characters. Those limits remain
  enforced by existing source/Reviewer implementations, not by the preparation object.
  PCA total inference/tool-loop costs and Harness downstream costs are not newly bounded here.
- Distinguishes invalid/missing configuration (`ArgumentException` codes such as
  `PcaBindingMissing`, `ReviewerBindingMissing`, `HarnessRecipientBindingMissing`) from
  valid-but-disabled execution (`PersonalLabNotApproved` / `ProductionTrustUnavailable`).

`BindNativeAdapters` is concrete reusable wiring to the existing
[TaskBriefingAdapter](../../src/MAF.Agents.Comprehension/TaskBriefingAdapter.cs) and
[NativeEvidenceReviewer](../../src/MAF.Agents.Review/NativeEvidenceReviewer.cs).
It requires distinct explicitly supplied clients and separately matches each client's
`ChatClientMetadata` endpoint/provider/default model to its selected role. The two roles
may intentionally use the same recipient, but need distinct role configuration identities
and client instances. There is no generic environment-variable fallback or client factory.
This helper constructs adapters and `ProjectTools`, reads directory metadata, and makes
no model request. Calling an adapter later would be an effect requiring separate approval;
the returned adapters are not registered in Host and are not execution capabilities.

Client metadata is a compatibility assertion from that client, **not authenticated egress
evidence**. Middleware, redirects, retries, proxy, credential custody and model overrides
are not proven by metadata matching. No operational native factory or outbound guard was
invented. `DemandHarnessRecipient` rejects missing/mismatched values; even equal values
raise `HarnessRecipientVerificationUnavailable`. A self-described recipient cannot enable a run.

Preparation creates no provider client/session, process, HTTP transport, source snapshot,
execution database, schema migration, policy source or authority. It does not accept an
executable, transport, provider factory or policy callback. No public CLI input reaches it.
`DemandExecution` denies both profiles unconditionally; no executable research route exists.
No second Workflow, manual Agent loop, generic hierarchy or plugin framework was added.

The existing [Host result view](../../src/MAF.Host/LocalSoloPresentation.cs) and
[S2A outcome mapper](../../src/MAF.Host/LocalSoloOutcomeMapper.cs) remain the output boundary;
their actual Workflow regression cases are retained. There is no new preparation-result
presenter that duplicates readiness. Controlled outcomes retain their honest offline labels.

## Deployment-profile decision and proposed risk boundary

| Profile | Assumptions and status |
| --- | --- |
| PersonalLab | Separate experimental candidate; disabled. Personally controlled Windows, personal repositories/test data and credentials only, one cooperative foreground operator, one exact workspace, OpenCode ReadOnly only. No company resources, background/unattended jobs or shared service. |
| ProductionProtected | Existing protected path; disabled until genuine deployment trust exists. No fallback to PersonalLab after failed production authentication/admission. |

PersonalLab is technically viable as an **explicit separate research profile**, without
changing ProductionProtected guarantees. This does not mean today's protected runtime can
execute using a profile flag. Its independent caller/policy prerequisites remain mandatory.
A future lab-specific admission/effect composition needs separate review and honest lab
provenance; it must not implement a fake production resolver/authority or call a test fixture.
Do not change UnavailableRuntimeAccess or register a substitute on the production path.

Proposed PersonalLab acceptance explicitly excludes independent caller authentication,
protected human-consent evidence, hostile same-Windows-principal isolation, corporate
approval, production anti-rollback and guaranteed provider sandboxing. Local path checks
and ReadOnly tool permissions do not contain a malicious local process or compromised
Harness. Source acquisition verifies review-time observations, not semantic correctness.
Operator confirmation would be cooperative research consent, never protected human proof.
Personal credentials/configuration and source data can be disclosed to each separately
selected recipient and Harness downstream tool. No real private configuration is recorded here.

ProductionProtected retains genuine verified caller/human intention, current policy/exact
operation binding, protected configuration/storage custody, explicit activation/revocation,
provider effect/transport enforcement, credential/recipient protection, independent evidence,
no ambiguous POST resubmission and original-session-only recovery requirements. The
implementation of those existing guards is unchanged; missing deployment guarantees are
still missing, not newly certified. Corporate security reports and execution holds are intact.

## Source audit and exact future first-run path

Audited Host Program/LocalSolo classes, mapper/presenter, S2A/S2B tests/results,
[Safe Host design](../plans/SAFE_HOST_COMPOSITION_USER_FLOW_DESIGN.md) and
[architecture guardrails](../architecture/ARCHITECTURE_GUARDRAILS.md). Also traced these seams:

1. Future separately approved foreground ingress prepares exact intent, then obtains profile-
   appropriate authorization **before any PCA context/model request**. S2C stops here.
2. Independently configured PCA client → `TaskBriefingAdapter` → existing
   `ComprehensionAgentFactory` / official `ChatClientAgent`. Model output remains an exact
   ReadOnly proposal; it cannot select objective, root or authority.
3. Existing internal [ControlledHandoffWorkflow](../../src/MAF.Workflows.External/ControlledHandoffWorkflow.cs)
   constructs the official PCA → ExternalDelegation → EvidenceReviewer → HumanGate graph.
   Its advisory assessment callback is not a grant. It currently labels outcomes offline.
4. [ProtectedHandoffDelegation](../../src/MAF.Workflows.External/ProtectedHandoffDelegation.cs)
   checks original request/run/handle →
   [ProtectedExternalRuntime.RunAsync](../../src/MAF.Workflows.External/ProtectedExternalRuntime.cs).
   Runtime independently resolves caller, acquires TaskId ownership, rereads records,
   assesses exact current policy and performs atomic admission/approval consumption.
5. Exact ExplicitV4 execution/approval stores and receipt coordinator → protected
   `OpenCodeClient` and `DurableExecutionService.LaunchOwnedAsync` → existing official
   child `OpenCodeExecutor` Workflow. Per-effect checks and pre-wire verification precede
   health/schema/agent GETs, Create session POST, original receipt finalization and prompt POST.
   Use `ReceiptTransport.Create()`, not `ForTests` or arbitrary HTTP injection.
   Existing v4 ownership, session receipts and no-resubmit semantics stay in place.
6. Completed correlated provider observations supply candidates → mandatory
   [VerifiedSourceMode](../../src/MAF.Workflows.External/VerifiedSourceMode.cs) provider-candidate
   constructor → [BoundedSourceReader](../../src/MAF.Core/Tools/BoundedSourceReader.cs) independent
   acquire → independently bound `NativeEvidenceReviewer` actual evidence-tool observation
   → source currentness recheck → existing HumanGate → Host outcome mapping/result view.
   HumanGate ReadyForHumanReview is not human acceptance.

This is an exact integration map of existing software, **not an operational factory added
by S2C**. The External assembly exposes internals only to test assemblies; Host cannot
construct the controlled graph. Adding friend access or exposing callback-based composition
would not resolve trust. Its optional source mode and hardcoded controlled provenance also
prevent simply promoting the test graph to a live route. A future narrow same-assembly
composition must require verified sources, preserve all original identity checks and make
real versus fixture provenance explicit. No arbitrary `IExternalHarnessClient` injection
has been made operational.

### Exact authorization and outbound gaps

| Boundary | Existing consumer / missing integration |
| --- | --- |
| Production trust | `ProtectedExternalRuntime` requires a genuine `ITrustedIdentityResolver`, `ITrustedCallerContext`, current `IPolicySource` and admission/enforcement stores. `UnavailableTrust` resolves nobody. There is no genuine Host ingress/acquisition/custody implementation. S2C supplies none. |
| SQLite v4 | `SqliteExecutionStore` ExplicitV4 and `SqliteApprovalStore` ExplicitV4 preserve exact ownership, admission and receipt semantics; trusted maintenance/activation is separate. No lab provenance/storage authorization contract is installed. Never seed synthetic authority to make a personal DB operational. |
| Native PCA | `TaskBriefingAdapter(IChatClient, ProjectTools)` uses project tools and can disclose context. New binding checks explicit client metadata. No per-call recipient/data permission, redirect/failover/retry or total cost enforcement exists here. |
| Harness | `OpenCodeOptions` validates loopback origin/root/agent/HTTP opt-in and response/request limits. `OpenCodeClient.Scope` covers origin/root/agent/read rules, **not downstream provider/model**. `prompt_async` body selects agent but no explicit provider/model. Loopback transport cannot prove downstream recipients. A server configuration snapshot/model pin and actual enforcement/observation are missing; equal candidate labels fail closed. |
| Native Reviewer | `NativeEvidenceReviewer(IChatClient)` enforces bounded evidence tools and actual tool observation, not outbound authority. Separate metadata binding is implemented; independent recipient/data approval and transport enforcement are missing. |
| Legacy native CLI | `Program.cs` still constructs one generic `AI_BASE_URL` / `AI_MODEL` client for standalone PCA/Review. This is not the Local Solo execution composition and must not be reused implicitly for both roles. S2C leaves those unrelated commands unchanged. |

## Personal Windows validation

Only owner-authorized personal, non-sensitive fixtures were used. New preparation tests
own an empty `maf-s2c-*` directory and delete it non-recursively. Existing reviewed
Workflow/evidence fixtures own isolated synthetic SQLite/files and fake HTTP/scripted
clients; their existing migration/cleanup executes only inside those fixtures. Preparation
itself has no DB dependency and tests assert its workspace remains empty. These assertions
and static dependency review do not claim comprehensive OS side-effect instrumentation.

No new package or external network restore was performed. No live model, OpenCode, provider/server launcher, company-data operation,
corporate identity/network probe, or endpoint-security reconfiguration is part of this run.

Successful build/restore/test commands (PowerShell interactive CLI; no `.ps1`). The
owner-specific absolute NuGet cache path is redacted below; all other arguments are exact:

```text
dotnet restore tests/MAF.Tests/MAF.Tests.csproj --source <personal-NuGet-cache> --disable-parallel -m:1 -nr:false -p:NuGetAudit=false -v minimal
dotnet build src/MAF.Host/MAF.Host.csproj --no-restore -m:1 -nr:false -v minimal
dotnet build tests/MAF.Tests/MAF.Tests.csproj --no-restore -m:1 -nr:false -v minimal
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --list-tests --filter 'FullyQualifiedName~LocalExecutionPreparationTests|FullyQualifiedName~LocalSoloHostTests|FullyQualifiedName~LocalSoloWorkflowTests|FullyQualifiedName~LocalSoloReadinessTests|FullyQualifiedName~ExternalConsoleOutputTests|FullyQualifiedName~RuntimeEnforcementTests.HostDoesNotConstructLegacyRuntimeWithoutProductionTrust|(FullyQualifiedName~ProtectedHandoffTests&FullyQualifiedName!~RealProcessCrashRecoversOriginalChildWithoutParentResumeOrPost&FullyQualifiedName!~CompetingProcessCannotExecuteOrInspectWhileOriginalOwnerIsActive)|FullyQualifiedName~NativeEvidenceReviewerTests|FullyQualifiedName~SourceCandidateIntegrationTests|FullyQualifiedName~SourceEvidenceCoverageTests'
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~LocalExecutionPreparationTests' --logger 'trx;LogFileName=local-solo-s2c-focused.trx' --results-directory TestResults/home-validation
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~LocalSoloHostTests|FullyQualifiedName~LocalSoloWorkflowTests|FullyQualifiedName~LocalSoloReadinessTests|FullyQualifiedName~ExternalConsoleOutputTests|FullyQualifiedName~RuntimeEnforcementTests.HostDoesNotConstructLegacyRuntimeWithoutProductionTrust' --logger 'trx;LogFileName=local-solo-s2c-host-regression.trx' --results-directory TestResults/home-validation
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter '(FullyQualifiedName~ProtectedHandoffTests&FullyQualifiedName!~RealProcessCrashRecoversOriginalChildWithoutParentResumeOrPost&FullyQualifiedName!~CompetingProcessCannotExecuteOrInspectWhileOriginalOwnerIsActive)|FullyQualifiedName~NativeEvidenceReviewerTests|FullyQualifiedName~SourceCandidateIntegrationTests|FullyQualifiedName~SourceEvidenceCoverageTests' --logger 'trx;LogFileName=local-solo-s2c-protected-regression.trx' --results-directory TestResults/home-validation
```

| Validation | Discovered / passed / failed / skipped | Result |
| --- | --- | --- |
| Host build | N/A | Exit 0; 0 warnings/errors; 14.42s |
| Test-project build | N/A | Exit 0; 0 warnings/errors; 15.17s |
| S2C focused | 36 / 36 / 0 / 0 | Exit 0; 246ms |
| S1/S2A/S2B and Host regression | 123 / 123 / 0 / 0 | Exit 0; 7s |
| Protected/source/Reviewer regression | 201 / 201 / 0 / 0 | Exit 0; 57s |
| Total scoped execution | 360 / 360 / 0 / 0 | Not a full-suite or live acceptance result |

Selection was inspected before execution; excludes both protected-handoff process crash/
competition methods, all live/provider/model launchers and unreviewed broad suites. Excluded
tests are not counted as executed/skipped. TRX counters were checked; artifacts are ignored
under TestResults/home-validation. Existing assertions and fixtures were not modified.

Initial `dotnet build src/MAF.Host/MAF.Host.csproj --no-restore -m:1 -v minimal`
failed NETSDK1004: SQLite `project.assets.json` was absent (0 warnings/1 error).
First cache-only restore, same command above without `-m:1 -nr:false`, exited 1 after
the restore-discovery message without diagnostic text; cause not established. The bounded
single-node cache-only restore succeeded for all eight referenced projects. No package
version/source configuration changed, no external restore attempted. There was no observed
endpoint alert/block or testhost crash in tool output; security-product telemetry was not
probed. These results do not establish corporate execution clearance.

## Owner decision and smallest next increment

Proposed syntax, **NOT registered**:

```text
--local personal-run --workspace <personal-directory> --objective <bounded-task>
```

Fixed PersonalLab/OpenCode/ReadOnly, foreground and one prepared operation only. Real
recipient configurations must come from separately selected private role-specific config,
not flags selecting factories/transports/authority. Preview the exact three recipients,
workspace, objective and budget before any data leaves the machine. No auto-activation,
production fallback, permission reply, implicit Abort, write/shell, ambiguous retry or
background worker. Proposed disclosure:

> Experimental PersonalLab: cooperative local operator only. No independent caller or
> protected consent proof, malicious same-user isolation, corporate clearance, production
> anti-rollback or guaranteed provider sandbox. Personal project context may be sent to
> the separately selected PCA, Harness/downstream and Reviewer recipients. ReadOnly does
> not prove containment; HumanGate readiness does not accept the result.

The single Owner decision is whether to **accept this limited PersonalLab risk boundary
and separately authorize S2D to implement and validate one foreground ReadOnly personal
fixture run bound to the Owner's privately selected workspace and three recipient configs**.
The exact values must be reviewed before that first run; current S2C approval does not
approve any recipient or network request.

Smallest S2D scope: honest separate lab admission/effect/storage provenance compatible
with original-session/receipt protections, exact Harness downstream model configuration,
independent native client outbound guards/budgets, mandatory-source existing four-stage
composition with truthful provenance and existing Host mapper semantics, then one bounded
explicitly approved personal run. ProductionProtected stays blocked. If the required
receipt/storage or recipient guarantees cannot be supplied, stop at that exact gap;
do not substitute production test authority. No corporate IAM, Broker or Registry is needed.

**STOP after S2C. No S2D implementation or actual provider activation.**
