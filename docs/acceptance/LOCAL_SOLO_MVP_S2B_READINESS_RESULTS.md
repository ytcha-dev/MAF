# Local Solo MVP-S2B — Readiness and Non-executable Application Boundary

Date: 2026-10-20 (Asia/Taipei). Baseline: `278b4de8cca87f142f10767ded938cfd62c38b20`.
**Personal Windows offline validation passed; ready for owner acceptance. Operational execution BLOCKED.**

## Baseline and actual scope

HEAD, main and fetched origin/main matched the pinned baseline before edits. Only unrelated
untracked `.vscode/` existed and was preserved/excluded. Owner authorized personal Windows,
personal disposable fixtures, no live provider/model or corporate execution. Historical
reports/ADRs and corporate security-event evidence remain unchanged.

| Changed file | Responsibility |
| --- | --- |
| [LocalSoloConsole](../../src/MAF.Host/LocalSoloConsole.cs) | Add readiness to existing strict action/option parser and help; invoke existing candidate application boundary |
| [LocalSoloPresentation](../../src/MAF.Host/LocalSoloPresentation.cs) | Non-authoritative readiness categories; bounded limitation display |
| [LocalSoloOutcomeMapper](../../src/MAF.Host/LocalSoloOutcomeMapper.cs) | Fixed semantic limitation priorities; retain valid Workflow limitations |
| [LocalSoloReadinessTests](../../tests/MAF.Tests/LocalSoloReadinessTests.cs) | 23 focused cases; pure tests allocate no fixture directory; one explicit owned workspace case |
| [README](../../README.md), [Roadmap](../ROADMAP.md), this report | Current status, command and verification |

No changes to Program, application Profile/Submit logic, external denial, protected runtime,
Core policy/authority, SQLite, source/Reviewer logic, official Workflow, packages or production DI.
No new schema, trust implementation, operational facade or second Harness.

## CLI and readiness behavior

```text
--local readiness [--workspace <absolute-local-directory>]
```

Inspects only candidate metadata through existing LocalSoloApplication.Profile and
ValidateWorkspace. Explicit workspace overrides the existing optional fallback. Existing
validation rejects unsafe paths before filesystem inspection; only selected local directory
and ancestor attributes are checked. No source file, DB, credential or endpoint is opened.
All existing strict parser rules remain; readiness accepts only optional --workspace,
not objective/diagnostics/output/trust/ready switches or other CLI modes.

Successful inspection returns exit 0 **even though overall execution is Blocked**; this
means inspection succeeded, never authorization granted. Invalid input is exit 2 with
fixed non-disclosing text; other inspection errors exit 1 without arbitrary exception text.

| Category | Actual display/evidence limit |
| --- | --- |
| Candidate configuration | CandidateOnly; Harness identifier, local workspace, ReadOnly, independent PCA/Reviewer metadata availability; model values not printed |
| SQLite | NotEstablished; readiness does not locate/open/create/migrate a DB |
| Caller/intention | Both NotEstablished; no username/SID/token/domain/GUI/environment proof |
| Policy | Genuine issuer/current policy, exact scope, explicit activation/revocation all NotEstablished |
| Custody | Configuration/durable-state custody, restore/anti-rollback, provider credentials/bypass protection all NotEstablished; no credential/ACL/security-configuration inspection |
| PCA → native model | ConfiguredMetadataOnly / NotValidated or Unavailable, independently; recipient/data-flow authorization NotEstablished |
| Harness → model/provider/tools | Candidate Harness name only; downstream recipient/configuration and data-flow authorization NotEstablished; no health/access probe |
| Reviewer → native model | Separate metadata result; recipient/data-flow authorization NotEstablished |
| Deployment | Applicable security clearance required; not established here; corporate hold not lifted by personal validation |
| Overall | Blocked / ProductionTrustUnavailable; advisory display is not PolicyDecision/permit/receipt/ticket |

The allowlisted metadata reads remain MAF_LOCAL_HARNESS, optional MAF_LOCAL_WORKSPACE,
MAF_LOCAL_PCA_MODEL, MAF_LOCAL_REVIEWER_MODEL. No new configuration source, mutable Ready
flag or environment variable can establish authority. Readiness creates no task, execution,
WorkflowOutcome, approval, authority object or provider client.

## Application ownership and preserved protections

The existing internal LocalSoloApplication already supplies the necessary non-executable
boundary; no extra interface/wrapper/factory is justified. Console owns typed ingress;
Profile owns candidate validation; Presentation owns safe advisory output. The new path
is Console.Parse → Application.Profile → Presentation.Readiness. Its only injected function
is the existing metadata reader, not a caller-selected provider/policy/transport factory.

Future trusted composition must supply the approved Harness, genuine caller/intention,
current active policy/custody and exact binding. Existing ProtectedExternalRuntime retains
admission, ownership and provider-effect/receipt checks; existing official four-stage
Workflow retains orchestration. Readiness neither constructs nor invokes these components.
A provider-neutral display is not production isolation against a malicious same principal.

S2A's existing WorkflowOutcome mapper is reused; no second result model or fake outcome
for readiness. OfflineSample / NotExecuted remains distinct from ControlledOfflineFixture /
NotLiveProvider. No operational observation provenance was introduced. Existing local
Submit and three external/recovery modes remain ProductionTrustUnavailable. No fallback
from UnavailableTrust/UnavailableRuntimeAccess, no policy activation, permission replies,
implicit Abort or ambiguous POST replay was added.

## Critical limitation correction

S2A mapper took 16 combined entries, then presenter showed only eight; later Reviewer,
uncertainty or gate restrictions could disappear. The new fixed order starts with:

1. Operational activation/security blocker (fixture grants no rights).
2. Exact external status, with explicit Unknown/no-retry semantics when applicable.
3. Evidence/Reviewer status and semantic-correctness boundary.
4. Review limitations, external limitations, gate/outcome limitations, then general disclaimers.

Each source list is bounded to its established contract (review 16, external 16,
combined outcome 50), deduplicated. A valid controlled graph has at most 50 distinct
specialist/gate entries plus five mapper summaries/disclaimers. Presenter now shows up to
64 limitation entries, each sanitized/redacted then capped at 512 characters with an
explicit truncation marker. Lists over 64 get an omission marker. This retains all valid
controlled limitations within a finite budget, including critical items after eight earlier
informational entries. Arbitrary over-contract lists or long strings can still be truncated;
there is no claim to classify arbitrary text by severity or preserve unlimited data.

Evidence/diagnostic/stage list bounds remain unchanged; optional adapter identifiers still
appear only in diagnostics. Heuristic redaction is not arbitrary-secret detection. Tests
cover late evidence/scope/uncertainty entries and 100 × 10,000-character hostile limitations
with output below 40,000 characters. No generic severity/telemetry framework was added.

## Personal Windows commands and actual results

Selected SDK 10.0.401; existing project.assets.json and dependency cache present. No restore,
new package or external dependency access. Tests were selected/reviewed before execution:
pure readiness/Host cases plus the same known-safe S2A fake HTTP/scripted-native fixture
regressions; two protected process-launch methods and all live/launcher tests excluded.

```powershell
dotnet --version
dotnet build src/MAF.Host/MAF.Host.csproj --no-restore
dotnet build tests/MAF.Tests/MAF.Tests.csproj --no-restore
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~LocalSoloReadinessTests' --logger 'trx;LogFileName=local-solo-s2b-focused.trx' --results-directory TestResults/home-validation
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~LocalSoloHostTests|FullyQualifiedName~LocalSoloWorkflowTests|FullyQualifiedName~ExternalConsoleOutputTests|FullyQualifiedName~RuntimeEnforcementTests.HostDoesNotConstructLegacyRuntimeWithoutProductionTrust' --logger 'trx;LogFileName=local-solo-s2b-host-regression.trx' --results-directory TestResults/home-validation
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter '(FullyQualifiedName~ProtectedHandoffTests&FullyQualifiedName!~RealProcessCrashRecoversOriginalChildWithoutParentResumeOrPost&FullyQualifiedName!~CompetingProcessCannotExecuteOrInspectWhileOriginalOwnerIsActive)|FullyQualifiedName~NativeEvidenceReviewerTests|FullyQualifiedName~SourceCandidateIntegrationTests|FullyQualifiedName~SourceEvidenceCoverageTests' --logger 'trx;LogFileName=local-solo-s2b-protected-regression.trx' --results-directory TestResults/home-validation
```

| Verification | Actual outcome |
| --- | --- |
| Host build | Exit 0; 0 warnings / 0 errors; 6.09s |
| Test-project build | Exit 0; 0 warnings / 0 errors; 4.63s |
| New S2B focused | Exit 0; discovered 23 / passed 23 / failed 0 / skipped 0; 247ms |
| Host/S1/S2A regressions | Exit 0; 100 / 100 / 0 / 0; 2s (56 S1, 12 S2A, 29 output, 3 legacy denial) |
| Protected/source/Reviewer regressions | Exit 0; 201 / 201 / 0 / 0; 21s (34 protected, 66 Reviewer, 59 candidate, 42 coverage) |
| Total final scoped tests | 324 discovered / 324 passed / 0 failed / 0 skipped; not a full-suite result |

No failing test runs in S2B; existing assertions unchanged. Ignored TRX artifacts are under
TestResults/home-validation. No security alert/block/testhost crash was observed in execution
results; security telemetry was not probed. No corporate/security configuration or records
changed. Personal Windows success is not corporate execution clearance.

### Side-effect and architectural evidence limits

Focused tests inject an allowlist-only metadata reader and fail unexpected reads; invalid
syntax is rejected before it is called. The one valid-workspace case checks its newly
owned empty directory has no files and removes it non-recursively. Pure readiness cases
need no temp directory. These assertions do not instrument every possible OS effect.
Static call-path review establishes no Workflow/client/authority/store construction on
readiness; no LocalSolo production source references synthetic helpers. Existing S2A
fixtures execute actual Workflow/Agent code against fake HTTP and scripted models only.
No real provider/model/server, identity probe or live DB was used; disposable SQLite v4
is used solely by historical controlled regression fixtures. No full-suite, process crash,
real model semantics or corporate compatibility claim is made.

## Remaining prerequisites and smallest next operational integration step

Readiness is usable now, but actual Host execution remains Blocked. First real OpenCode
execution requires separately authorized, concrete verified caller/exact-operation intention,
genuine policy issuer/current rights, explicit activation/revocation, approved workspace and
budgets, protected config/state/provider custody and restore/anti-rollback assumptions;
separate approved PCA-native, Harness-second-hop and Reviewer-native recipients/data flows;
and applicable endpoint clearance. Native model outbound enforcement and operational
composition must be implemented and validated before enabling the public command. The
existing controlled graph and v4 guards cannot supply these missing deployment facts.

Smallest next increment: owner selects the **one concrete personal deployment trust and
three-recipient configuration contract**, then separately authorizes its narrow disabled
adapter wiring and negative validation against existing interfaces. Keep submit blocked
until exact rights, current policy, custody and native egress are demonstrable; do not
build a generic Broker/IAM/Registry platform or insert test authority. Real provider/model
compatibility requires its own bounded fixture authorization. No S2C work begun.

**STOP after S2B.**
