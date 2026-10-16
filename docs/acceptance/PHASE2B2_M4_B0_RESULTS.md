# M4-B0 — Cancellation session evolution and delivery design gate

2026-10-17 (Asia/Taipei). Branch `main`; HEAD and fetched `origin/main` both `b00e15cd717ab3be38c2ab69045c173e6d7e4aa7` before edits. Tracked tree initially clean; pre-existing `.vscode/` excluded, unread and unstaged.

**Bug reproduced in Direct-Allow and Approved Resume. Safe repair is blocked by missing durable session-response provenance.** No production source, schema, migration, guard or provider behavior changed. Strict cancellation checks remain intact. B0 review/characterization is complete; successful post-cancel session evolution/reconciliation is **not accepted**. This is the owner's explicit design-gate stop condition, not a claim that the requested positive recovery scenario passed.

Validation: **139 focused discovered/passed, 743 offline passed, zero failed/skipped; solution build zero warnings/errors.** Ten new characterization/negative cases plus all historical 733 offline cases. Passing characterization tests assert the existing fault and preserved denial; they are not positive acceptance tests for a repair. No historical assertions changed.

## Reviewed implementation and cause

Read [A1 evidence](PHASE2B2_M4_A1_RESULTS.md), [A0 schema contract](../plans/PHASE2B2_M4_A0_SCHEMA_DESIGN.md), [ADR-004](../architecture/ADR-004_DURABLE_EXECUTION_RECOVERY.md), [ADR-005](../architecture/ADR-005_POLICY_APPROVAL_CANCELLATION.md), [ADR-006](../architecture/ADR-006_TRUSTED_BROKER_BOUNDARY.md), and [all seven guardrails](../architecture/ARCHITECTURE_GUARDRAILS.md). Source is authoritative:

| Component / function | Verified behavior |
|---|---|
| [Cancellation coordinator](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.Cancellation.cs), RequestCancelAsync / ReadCancellation | Captures immutable historical ProviderSessionId (possibly null); checks current execution session with strict equality on reads, alongside canonical digest, operation/control/audit joins |
| [SqliteApprovalStore](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.cs), GetControlAsync | ExplicitV3 reads validate cancellation evidence first; mismatch prevents control disclosure |
| [Runtime transaction](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.Runtime.cs), VerifyEffectTransactionAsync | Current authority/control/owner/binding checks; atomic execution dispatch intent + bounded audit; Abort denied |
| [Allow admission](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.Admission.cs), [Resume/consumed evidence](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.Resume.cs) | Unique Created and owner-bound one-use continuation; Resume retains RequireApproval and consumed proof; no repeat admission/consume for existing execution |
| [ProtectedExternalRuntime](../../src/MAF.Workflows.External/ProtectedExternalRuntime.cs), RunCoreAsync / IntentStore.UpdateAsync | Independently assesses Inspect, then GetControl; guards phase transitions to dispatch intents, delegates ordinary session-response persistence to execution store |
| [DurableExecutionService](../../src/MAF.Workflows.External/DurableExecutionService.cs), PersistingClient.CreateAsync / SaveAsync | Saves SessionCreationIntended, awaits original Create, saves SessionKnown with returned session even if independent cancellation committed while waiting; next prompt intent is blocked by changed control |
| [SqliteExecutionStore](../../src/MAF.Persistence.Sqlite/SqliteExecutionStore.cs), UpdateAsync; [transitions](../../src/MAF.Core/DurableExecution.cs) | Real owner plus complete snapshot CAS and identity/phase validation. Null→known session is a valid phase evolution; no response receipt or linkage to an HTTP request |
| [OpenCodeClient](../../src/MAF.Workflows.External/OpenCode/OpenCodeClient.cs), Create / Reattach / Submit / Abort | Original response supplies session; recovery is original-session GET-only, protected Abort remains unsupported; no permission reply or tool loop change |
| [Schema](../../src/MAF.Persistence.Sqlite/ApprovalSchema.cs), control_audit | Task/actor/version/policy/optional approval + v3 cancellation FK; no effect-specific request-response/session receipt or historical execution snapshot |

The source equality is at cancellation partial line 190; control read at store line 170; response save at durable service line 195; protected IntentStore delegates non-intent updates at lines 116–119. These references are baseline locations, unchanged by B0.

## Deterministic reproduction and evidence

New [CancellationSessionEvolutionTests](../../tests/MAF.Tests/CancellationSessionEvolutionTests.cs) use only disposable synthetic SQLite, existing synthetic trusted identities and fake HttpMessageHandler. Its OnRequest callback runs after the raw `POST /session` is accounted and before the original response is returned. No wall-clock sleep or network synchronization is needed.

For **both** Direct-Allow and Approved Resume:

1. SessionCreationIntended has durably committed, session is null, and Create is in flight.
2. Independent cancellation commits control version 0→1, immutable historical session=null. GetCancellation and GetControl still succeed inside that gate. Cancellation adds no provider request.
3. Original response returns `ses_runtime`; durable snapshot saves SessionKnown. Original Task/Execution/Run/Message remain `runtime-task / runtime-execution / runtime-run / msg_runtime`.
4. Next prompt intent cannot pass control verification. Raw POST counts: **Create=1, Prompt=0, Abort=0**. AbortRequested remains false. Approved path retains original approval status Consumed; no manufactured Allow/reapproval.
5. GetCancellation and GetControl now each throw **CancellationTargetMismatch**. Fresh protected runtime with separately allowed Inspect also fails before any additional HTTP. Snapshot and historical evidence JSON/digest stay byte-for-byte unchanged by those read attempts. SQLite integrity_check returns `ok`.

This demonstrates the fault without changing an old assertion. The two new tests explicitly characterize the failed read/recovery contract; the positive requirement remains blocked. Fresh in-process composition is not represented as a new OS process. No new process test is needed to prove this synchronous read defect; existing actual-process no-resubmit/crash tests were rerun below.

## Why no permissive read fix was made

A third test loses the Create response **after real authorized dispatch intent and independent cancellation**. The genuine intent audit precedes cancellation audit. The original execution remains SessionCreationIntended with no session. A later phase-valid real-owner UpdateAsync can nevertheless persist `ses_unrelated` as SessionKnown; current strict evidence read rejects it. Two other cases show that a missing preceding phase is rejected, but a phase-valid update without any dispatch audit can persist an unproven session (still rejected by evidence read).

These are trusted-store-boundary diagnostics, not an exploit claimed against a production authenticated service. They establish that the available durable rows cannot prove which session was returned by the original request. Checking just a prior intent audit would still accept the lost-response/injected-session case. A null wildcard, current phase/version, actor/epoch or timestamp ordering cannot solve this. Store ownership establishes a local writer, not provider response provenance or human identity.

The corrected invariant is specified, **not implemented**: historical cancellation binding remains immutable; later session identity must be accepted only through a separately validated original request/response receipt ordered against the cancel transaction, with unchanged task/execution/run/message/fingerprint/provider/scope. Otherwise fail closed. Known-session replacement and identity substitution remain forbidden. Existing v3 lacks that receipt, atomic receipt+snapshot finalization and protected response-registration boundary. Repair would require broader reviewed persistence/runtime changes, so B0 stops without weakening equality or adding a schema.

## Coverage and actual validation

| Required coverage | Evidence / outcome |
|---|---|
| In-flight Create / late original session / Resume equivalent | 2 new reproductions; genuine session saved but reads/Inspect blocked; positive acceptance **not demonstrated** |
| Forged post-cancel session / missing intent | 2 new phase cases + real intent/lost response case; strict read denies; phase/audit alone insufficient proof |
| Known session, TaskId, ExecutionId, MessageId, WorkflowRunId substitution | 5 new negative cases; execution-store transition rejects all, original snapshot/evidence preserved |
| Cancel before intent, Allow/Consume/activation races | Existing CancellationAdmissionTests, including CancelFirstPreventsDispatchIntentAndTokenCancellationCreatesNoIntent; no unauthorized POST |
| Exact/conflicting replay, changed caller/config/authority, expiry | Existing cancellation admission/evidence tests and canonical-binding historical suite; no winner refresh or replacement |
| Evidence/audit tampering | Existing missing/mismatched audit, joins, malformed JSON/enum/digest/schema/trigger and capacity tests |
| Independent Inspect after cancel | Temporal bug remains blocked; known-session/historical unaffected recovery behavior retained, no inference of new Submit permission |
| Restart recovery-only and no prompt/Abort | Existing actual cancel/crash reads, RuntimeEnforcementTests and ApprovalResumeTests process tests pass; no new temporal restart success claimed |
| v2/v3, migrations and old M3-A2c/B2 behavior | Existing exact-profile, migration rollback/process kill and full offline suite pass unchanged |

New discovery: 10 cases (2 original response, 2 unproven phase, 5 immutable identity, 1 genuine intent/lost response). Focused discovery: 139 = historical A1 129 + 10. Full offline: 743 = historical 733 + 10. Failed/skipped: 0/0. Initial reproduction run 4/4 passed as characterization; final focused run 139/139 after adding the remaining negative evidence. There is no before/after production fix to claim.

Commands executed:

```powershell
dotnet build MAF.slnx --no-restore
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-restore --filter 'FullyQualifiedName~CancellationSessionEvolutionTests|FullyQualifiedName~CancellationAdmissionTests|FullyQualifiedName~CancellationEvidenceTests|FullyQualifiedName~CancellationSchemaTests' --logger 'trx;LogFileName=phase2b2-m4-b0-focused.trx' --results-directory TestResults/home-validation
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --list-tests --filter 'FullyQualifiedName~CancellationSessionEvolutionTests|FullyQualifiedName~CancellationAdmissionTests|FullyQualifiedName~CancellationEvidenceTests|FullyQualifiedName~CancellationSchemaTests'
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName!~MAF.Tests.OpenCodeLiveTests' --logger 'trx;LogFileName=phase2b2-m4-b0-offline.trx' --results-directory TestResults/home-validation
```

Raw TRX/discovery remain ignored in TestResults/home-validation. Final focused rerun actual process evidence (all test-owned helpers):

| Checkpoint | Terminated PID → fresh-reader PID | Observed durable result |
|---|---|---|
| V3TableCreated | 58120 → 21856 | v2, original bytes preserved, integrity ok |
| V3AuditAltered | 47572 → 41684 | v2, original bytes preserved, integrity ok |
| V3VersionWritten | 12488 → 46980 | v2, original bytes preserved, integrity ok |
| V3Committed | 20588 → 31624 | v3, original bytes preserved, integrity ok |
| CancelControlWritten | 52928 → 51116 | control0/evidence0, POST0, integrity ok |
| CancelCommitted | 16164 → 2624 | original task/execution, control1/evidence1, POST0, integrity ok |

Independent execution-owner process PID 2460 retained its TaskId handle while cancel PIDs 42692/10312 competed; PID 42692 committed the unique winner, control1/evidence1, Create/Submit/Observe/Abort all zero. These are rerun A1 guarantees, not proof of the proposed v4 schema or successful temporal recovery. Captured process start times and kill boundaries are recorded by the unchanged historical tests.

## B/C recommendation, owner decisions and stop boundary

[Combined delivery design](../plans/PHASE2B2_M4_BC_DELIVERY_DESIGN.md) recommends one reviewed offline v3→v4 extension: narrow original-session receipt plus one lifetime cancellation-delivery record shared by B and C. No generic event store, queue, scheduler or new agent loop. Field/atomicity contracts are proposed; final DDL is withheld pending receipt provenance/legacy-row decisions, not falsely presented as tested SQL.

- Delivery ownership is separate from execution ownership. Cancellation never waits for the execution lock. A delivery handle and unique transaction reserve one attempt; a fresh operation-specific Abort authorization is required, distinct from Cancel intent, Submit and Inspect.
- Persist intent/audit before HTTP. At-most-one reserved attempt deliberately means a crash before send can lose delivery. Restart never reconstructs the continuation or retries ambiguous POST. SQLite is not atomic with HTTP.
- Requested, DeliveryIntended, SentUncertain, Acknowledged and correlated remote terminal evidence remain distinct. Ack does not imply Cancelled; genuine Completed evidence and terminal immutability are preserved. Unknown is honest when evidence is insufficient.
- Current independently allowed GET inspection must remain possible without Submit rights, subject to current authority/control and exact original-session proof. Timeout/CTS/shutdown never implies Abort authority.
- Owner must accept receipt writer boundary, no provenance backfill for old rows, one-attempt availability trade-off, separate Abort binding and exact offline v4/profile specification before code. Same-principal writer security, real publisher/operator proof, custody/revocation and anti-rollback remain blocked M3-C prerequisites.

Recommended first **separately authorized M4-B1**: accept/finalize the narrow shared schema contract, then implement receipt + v4 storage/migration/profile and provider-free atomic tests, closing both B0 positive scenarios before delivery. No Abort implementation until that foundation is validated and separately scoped. B delivery/C reconciliation can then reuse the reviewed storage extension. Existing ambiguous v3 rows must not become bearer grants during migration.

Changed files: this report, combined design, new CancellationSessionEvolutionTests, and minimal latest-status notes in Roadmap/execution plan. Production code/DI/Host/Workflow/evaluator/HTTP/fingerprint/canonical algorithms, original schema and historical acceptance/security documents unchanged. No real database, credentials, live OpenCode/model, company endpoint, ACL or security-policy work. No endpoint-security alert observed. Full Phase 2B-2, M4-B/C, production trust and corporate compatibility remain unaccepted.

Final scoped diff reviewed: five files, no production source changes or historical assertion edits; staged whitespace check passed. All 81 relative file links across the four changed documents resolve and fenced blocks balance. Existing untracked `.vscode/` remains excluded.

**STOP after M4-B0.** The design gate is delivered; temporal repair and Abort delivery are blocked pending owner acceptance, not silently enabled.
