# M4-A1 — Durable cancellation admission and schema v3

2026-10-16 (Asia/Taipei). Baseline `c467f0aef8b1946e94b3aa67ae0e06a799959540`, branch `main`, fetched origin/main equal before edits. Owner accepted [M4-A0](../plans/PHASE2B2_M4_A0_SCHEMA_DESIGN.md); this increment resolves the [M4-A storage blocker](PHASE2B2_M4_A_RESULTS.md) for the controlled synthetic composition. Initially tracked tree was clean; pre-existing `.vscode/` was not read, changed or staged.

**Controlled M4-A1 implemented and validated: 129 focused / 733 offline passed; solution build 0 warnings/errors.** Cancellation is an independently authorized, durable intent, not an Abort grant or remote terminal result. M3-C remains deferred/blocked, not waived. No production trust, Host activation, cancellation CLI, Broker, provider Abort/reply, live model or corporate endpoint execution was enabled.

## Implemented files and boundaries

| Files / components | Change |
|---|---|
| [CancellationAdmission.cs](../../src/MAF.Core/Policy/CancellationAdmission.cs), [ApprovalPersistence.cs](../../src/MAF.Core/Policy/ApprovalPersistence.cs) | Narrow command, controlled inputs, immutable evidence/result and coordinator contracts; optional CancelRequestId audit projection. No SQLite/provider types in Core, no dispatch continuation |
| [ApprovalSchema.cs](../../src/MAF.Persistence.Sqlite/ApprovalSchema.cs), [V3 migration](../../src/MAF.Persistence.Sqlite/ApprovalSchema.V3.cs), [V3 DDL](../../src/MAF.Persistence.Sqlite/ApprovalSchema.V3Ddl.cs), [maintenance boundary](../../src/MAF.Persistence.Sqlite/AuthorizedApprovalMaintenance.cs) | Extract unchanged v2 DDL; separate authorized offline v2→v3 operation, exact manifests/data validation, exclusive maintenance, rollback/fresh-connection verification |
| [SqliteExecutionStore](../../src/MAF.Persistence.Sqlite/SqliteExecutionStore.cs), [SqliteApprovalStore](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.cs) | ExplicitV3 opt-in, exact version/schema verification; default LegacyV1 / approval ExplicitV2 unchanged; old cancellation primitive refuses v3; audit projection includes explicit cancellation FK |
| [Admission](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.Admission.cs), [Resume](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.Resume.cs), [Runtime](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.Runtime.cs) partials | Pass exact required ownership profile to existing real-owner/use guards and one-use continuations. Shared policy/consume/intent/effect algorithms retained |
| [CancellationEvidenceFormat](../../src/MAF.Persistence.Sqlite/CancellationEvidenceFormat.cs), [cancellation coordinator](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.Cancellation.cs) | Closed bounded typed evidence, independent current Cancel evaluation, atomic operation/evidence/control/revocation/audit, mandatory consistency checks on commit and evidence/control reads |
| [CancellationAdmissionApplication](../../src/MAF.Workflows.External/CancellationAdmissionApplication.cs) | Provider-free entry, verified resolver/clock, trusted config dependency. No HttpClient, execution ownership, Workflow or provider callback dependency |
| [SyntheticCancellation](../../tests/MAF.ApprovalAcceptance/SyntheticCancellation.cs), [helper Program](../../tests/MAF.ApprovalAcceptance/Program.cs) | Test-only authority/caller/maintenance adapter; bounded stdin/stdout gates, captured process IDs, migrate/cancel/owner/read/verify helpers, fake transport only |
| [CancellationSchemaTests](../../tests/MAF.Tests/CancellationSchemaTests.cs), [CancellationAdmissionTests](../../tests/MAF.Tests/CancellationAdmissionTests.cs), [CancellationEvidenceTests](../../tests/MAF.Tests/CancellationEvidenceTests.cs) | 129 new deterministic cases, including real OS termination and competing cancel processes; no old assertions changed |

Roadmap and execution plan record this result. Host, ProtectedExternalRuntime routing, Workflow, DurableExecutionService, OpenCode adapter/client, fingerprint algorithm, canonical binding v1 and terminal transitions were not changed. Production external modes still return ProductionTrustUnavailable. Native PCA/Reviewer remain separate, outside this protected external composition.

## Schema and migration evidence

The implemented v3 delta was compared against the accepted M4-A0 SQL and is **verbatim equal after C# raw-string indentation removal**: immutable cancellation_requests, nullable control_audit.cancel_request_id FK, one partial unique CancellationRequested audit index, two evidence immutability triggers. No additional delivery schema or general event framework. Unique task_id is the lifetime winner; cancel operation metadata additionally uses existing kind=3/generation=1 uniqueness. There is no fabricated Approved row.

`ApprovalSchema.MigrateV2ToV3Async` / `AuthorizedApprovalMaintenance.MigrateV2ToV3Async` require an injected exact-resource maintenance authorizer and all OfflineMaintenance attestations. No production authorizer is supplied. Existing DB only, schema/task lock handles acquired exclusively without deleting leftover files; SQLite Pooling=false, synchronous FULL, foreign_keys, 5-second busy timeout, locking_mode EXCLUSIVE / BEGIN EXCLUSIVE. All Hosts must be stopped and backup/rollback confirmed by the trusted maintenance caller; attestations are not independent proof of shutdown or backup custody.

Before DDL: require exact v2, compare known object definitions (whitespace normalized outside string literals), validate original snapshots/column joins/control fields, integrity and FKs. Pre-existing kind=3 operations are explicitly rejected for review, as specified by A0. Unknown/extra objects, partial v3, future versions, malformed rows and held locks fail closed. No silent repair or v3 re-migration.

Capture all original values/storage types/NULLs, snapshot BLOB bytes and sqlite_sequence; no old-row reserialization or update. DDL + final user_version=3 share one transaction. Recompare all six tables and sequence, integrity/FKs and exact v3 layout before commit; a new connection verifies them while the exclusive schema gate remains held. Original physical DB file hashes naturally change; original serialized snapshot/value bytes do not. All approval lifecycle variants and twelve execution states were exercised, including terminal and uncertain states.

Failure before commit rolls back the complete delta. Lost response/termination at commit is handled by validating actual complete v2/v3, never downgrade/repair. After any new v3 writes, restoring a v2 backup could lose control/authority evidence and requires separately approved reconciliation. Existing [backup/rollback custody rules](../setup/SQLITE_APPROVAL_MAINTENANCE.md) remain applicable; no real personal/company database was migrated or copied.

| Profile | v1 | v2 | v3 |
|---|---|---|---|
| Execution LegacyV1 (default) | Existing behavior, including empty-v0 creation | Reject | Reject |
| Execution ExplicitV2 / approval default ExplicitV2 | Reject | Existing behavior | Reject |
| New execution / approval ExplicitV3 | Reject | Reject | Exact known v3 only |

Explicit profiles never create/migrate a DB; ExplicitV3 direct ExecutionStore.CreateAsync is AtomicAdmissionRequired. Actual owner/path/profile must match the coordinator; no version >= 2 shortcut. Full protected Direct-Allow and Approved Resume each completed through the existing official Workflow on v3, retaining original IDs/consumed evidence, session/prompt POST=1/1, Abort=0 and GET-only recovery.

## Independent cancellation and atomicity

```text
verified caller + trusted cancellation inputs
 -> protected authority acquisition outside SQL
 -> short v3 IMMEDIATE transaction (NO execution TaskId lock)
    -> current active authority/digest + exact target/control
    -> independent Allow for RequestCancelExecution / ExecutionCancellation
    -> kind=3 operation + immutable typed cancellation evidence
    -> sticky control CAS/version+1
    -> Pending/Approved revocation, preserving original operation/decision
    -> linked bounded revocation/CancellationRequested audit
    -> revalidate rights/time/policy and complete durable linkage
 -> commit data only
```

Task/run/execution/message/fingerprint/session are derived from authoritative execution or original Submit authorization, not caller claims. Scope/config/descriptor/authority/operation ID are controlled application dependencies. Trusted resolver verifies caller; authority independently reauthenticates/exact-authorizes the Cancel operation. Submit-only, Inspect-only, RequireApproval, explicit Deny, unavailable/expired/unactivated policy, wrong scope/capability/authority and stale control fail closed. No implicit activation. Before exact Cancel authorization succeeds, missing/mismatched target details are redacted as CancellationNotAuthorized rather than providing an existence oracle; no placeholder binding or fallback is stored.

Format-1 JSON retains complete PolicyRequest + CapabilityDescriptor, with SQL-indexed equality and canonical v1 digest verification. 16384-byte maximum, depth 8, operation-array limit 3; unknown/duplicate/missing fields, numeric/noncanonical/unknown enum names and unsupported formats are rejected. Hash normalization/property ordering does not change canonical equality. AuthenticationContextId is a non-secret historical context identifier, not a token or signed human proof.

Exact replay retains original expected control version, bound operation/descriptor/caller/target, policy digest and original validity window. It rechecks current policy/rights/expiry and writes nothing. Renewed verified context for the same subject is allowed without rewriting original context metadata. Different actor/operation/ID/control input cannot replace the winner. Expiration/revocation does not clear sticky intent or refresh the old record.

Commit and subsequent GetCancellation/GetControl checks cover exact indexed identities, operation join, immutable target, sticky control and mandatory code-6 audit. For revoked original approval, the linked revocation/version audit is also mandatory. Missing/unlinked/mismatched audit or missing evidence with v3 operation/audit is rejected; no read repairs the record. Evidence is not an executable grant even when all joins validate.

Legacy controls/audit remain byte-preserved, with no new evidence/backfill/audit FK. A migrated legacy intent returns LegacyIntentUnverified, remains sticky and never grants Abort. Supported cancellation targets follow the existing M3 model: one original Submit approval per TaskId (Pending/Approved or Consumed) or direct-Allow execution. Other/multiple associated approval kinds fail closed rather than introducing new approval-generation semantics. Already expired/rejected/revoked no-execution authorization is not resurrected. Imported execution without a pre-bound control is not silently given a new control/tombstone.

Cancellation leaves execution state, AbortRequested and terminal evidence unchanged. Created/Running/Unknown/SubmissionUncertain preserve original handle; terminal/no-winner returns AlreadyTerminal without intent/audit mutation. Consumed stays Consumed; cancellation never consumes approval or creates a replacement execution/continuation.

Writer ordering is explicit: cancel-first blocks later consume/dispatch; admission/consume/intent-first may remain committed, then cancel changes control and prevents subsequent guard-authorizable effects. Real concurrent writer gates covered admission, consumption, dispatch and policy activation; authority lease freezes the candidate while durable SQLite order governs activation. There is **no transaction spanning HTTP**. A fake Create already inside transport could not be revoked; cancellation added no HTTP, and the next prompt/Abort POST counts remained zero. Caller token cancellation creates no durable intent; protected timeout/observation suppression of implicit Abort remains covered by historical regressions.

## Actual validation

Windows / .NET 10 / Debug. Focused discovery **129**; focused **129 executed / 129 passed / 0 failed / 0 skipped**. CancellationAdmissionTests=50, CancellationSchemaTests=49, CancellationEvidenceTests=30. Full offline **733 executed / 733 passed / 0 failed / 0 skipped**: historical 604 + new 129, no double counting. Solution build **0 warnings / 0 errors**. Old acceptance assertions were not modified. No restore/dependency changes.

| Verification group | Actual coverage |
|---|---|
| Schema/migration | Exact profiles, immutable/unique/FK/size/digest/version constraints; altered index/trigger/column/future version rejected; all original table bytes/sequence retained; legacy preserved |
| Authorization/replay | Verified independent actor, exact replay, current-rights/expiry, identity forgery/missing context, Submit/Inspect-only, policy Deny/RequireApproval/failure, stale versions and conflict protection |
| Audit/rollback | Faults after evidence, control, revocation and audit; capacity exhaustion; deleted/mismatched/unlinked required audit, changed operation/control/target, missing evidence; no partial state |
| Lifecycle/runtime | Pending/Approved revoke, Consumed preserve, running/uncertain/terminal truth; v3 direct-Allow and approved-Resume official Workflow; current control blocks later effects; no replacement IDs |
| Concurrency/process | Independent OS owner + two cancel contenders; actual migration/cancel termination; independent fresh reader processes and existing recovery-only behavior |

Final focused process evidence (UTC; bounded gates, captured PID/StartTime, kill only captured process):

| Checkpoint | Killed PID / startUTC | Fresh reader PID | Actual durable result |
|---|---|---|---|
| V3TableCreated | 59156 / `2026-10-16T15:30:25.9811179Z` | 57940 | v2; original bytes unchanged; integrity=ok |
| V3AuditAltered | 56808 / `2026-10-16T15:30:25.6153656Z` | 45808 | v2; original bytes unchanged; integrity=ok |
| V3VersionWritten (precommit) | 51504 / `2026-10-16T15:30:25.2623024Z` | 58724 | v2; original bytes unchanged; integrity=ok |
| V3Committed | 52752 / `2026-10-16T15:30:24.8919005Z` | 46736 | complete v3; original bytes unchanged; integrity=ok |
| CancelControlWritten (precommit) | 56020 / `2026-10-16T15:30:27.6416464Z` | 56440 | control=0, evidence=0, rollback; integrity=ok |
| CancelCommitted | 9568 / `2026-10-16T15:30:26.9707427Z` | 36796 | control=1, evidence=1; original runtime-task/runtime-execution/runtime-run/msg_runtime recovered; Unknown, provider calls=0 |

Independent execution-owner process **18376** kept its OS TaskId lock while cancel processes **25608 / 50784** competed. Winner **50784**; one evidence row, one code-6 audit, control=1, owner still held (another TryAcquire returned null). No cancellation-induced Create/Submit/Observe/Abort calls: **0/0/0/0**. Owner was subsequently released normally. This proves independent cancellation coordination with a held execution lock, not a live provider's terminal cancellation.

Final focused canonical digest example (synthetic root varies by run): `F49F50E8A528B169F1A8A98A47DDAF9A70274AA1048C93C3ACCBAC417C4EFA03`; original/recovered TaskId=`runtime-task`, ExecutionId=`runtime-execution`, CancelRequestId=`cancel-one`. No new session/message is created by cancellation or committed-cancel recovery. Aggregate full-suite provider POST is **not zero**: successful historical/v3 fake-workflow tests deliberately submit authorized tasks; cancellation-induced activity is measured separately.

```powershell
dotnet build MAF.slnx --no-restore
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-restore --filter 'FullyQualifiedName~CancellationSchemaTests|FullyQualifiedName~CancellationAdmissionTests|FullyQualifiedName~CancellationEvidenceTests' --logger 'trx;LogFileName=phase2b2-m4-a1-focused.trx' --results-directory TestResults/home-validation
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName!~MAF.Tests.OpenCodeLiveTests' --logger 'trx;LogFileName=phase2b2-m4-a1-offline.trx' --results-directory TestResults/home-validation
```

Discovery and raw TRX remain ignored under TestResults/home-validation. Fixtures use only synthetic temporary databases, injected test identities and fake HTTP. No API key, transcript or task text was added to durable evidence; no real DB or company data transfer, ACL/security-policy change or live OpenCode call. No endpoint-security alert was observed; corporate event records and historical results are unchanged.

Final source/document diff reviewed: `git diff --check` passed; 77 relative file links and fenced blocks across the results, Roadmap and execution plan validated. The accepted A0 DDL comparison passed. Scoped files exclude `.vscode/`, generated test outputs and historical acceptance/security records.

## Remaining limitations and M4-B prerequisites

- M3-C remains blocked: real publisher/operator authentication, protected writer/credential custody, human proof where required, revocation and external anti-rollback anchor are not implemented. Same-principal DB/binary/DI tampering and power loss are not proven safe by these tests. Store/maintenance APIs remain trusted-code boundaries, not authenticated production services.
- The v2 approval-based cancellation primitive remains available only on v2 with its historical limitations. ExplicitV3 rejects it and requires the independent coordinator. Raw store metadata reads are not a production ingress. No new Host/profile routing was enabled.
- If an already-sent operation refines session identity after cancel, exact historical target checks may fail closed for subsequent evidence read/replay. Do not rewrite immutable evidence or create a replacement cancellation winner; further correlated reconciliation needs its separately reviewed boundary. Already-sent HTTP cannot be rolled back.
- Intent is durable, but there is no Abort attempt, acknowledgement, retry, notification worker or assertion that the harness stopped. Original owner sees control at its next guardable effect; observation cancellation/timeout does not grant Cancel or Abort.
- Bounded audit still stops at capacity, with no pruning/archive/reset. No automatic migration, backup restore, policy activation or generation replacement.

**Next separately authorized increment:** M4-B bounded provider Abort delivery, only after specifying fresh operation-specific authorization, original-session binding, exclusive delivery ownership and durable dispatch/ack evidence. Immutable intent alone is insufficient; if delivery persistence needs a schema addition, review it explicitly. Ack must not become Cancelled. M4-C restart reconciliation and M3-C production activation remain separate gates.

**STOP after M4-A1.** This is controlled synthetic acceptance of cancellation admission/control, not acceptance of full M4, full Phase 2B-2 or corporate endpoint compatibility.
