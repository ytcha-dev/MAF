# M4-B1b — Explicit v4, original Create receipt and positive recovery

2026-10-17 (Asia/Taipei). Baseline `7d488115a08d3cb848dcc00e60d251968f20b614`, `main` equal to fetched origin/main before edits. Owner accepted the [B1a contract](../plans/PHASE2B2_M4_B1A_V4_SCHEMA_CONTRACT.md). Tracked tree initially clean; pre-existing `.vscode/` excluded, unread and unstaged.

**Controlled synthetic B1b implemented: both B0 legitimate-response scenarios now pass positive cancellation/control reads and original-session GET recovery.** Focused discovery/pass **125/125**, full offline **858/858**, failed/skipped **0/0**; solution build **0 warnings/errors**. Baseline 743 plus 115 new cases; the two original B0 fault characterizations became positive v4 tests, with their original identity/immutable-evidence/POST assertions retained and strengthened. The other eight B0 negatives and all earlier regressions remain passing.

No Abort reservation/continuation/authorization/POST/worker, PolicyOperation/PolicyCapability addition, Host routing/DI, production trust, Broker, human authentication, provider permission reply, write/shell, native PCA/Reviewer, registry or Hybrid changes. M3-C remains blocked. This is not full M4/Phase 2B-2 acceptance or corporate endpoint clearance.

## Implemented files and ownership

| Files / components | Implemented boundary |
|---|---|
| [Core receipt contracts](../../src/MAF.Core/Policy/SessionReceipt.cs), [Core assembly visibility](../../src/MAF.Core/AssemblyInfo.cs) | Provider-neutral immutable intent/response/projection; narrow coordinator; issuer-only constructor, nonserializable one-use registration. Protected adapter bridge can use lifecycle internals; visibility is not same-principal isolation |
| [v4 DDL](../../src/MAF.Persistence.Sqlite/ApprovalSchema.V4Ddl.cs), [offline migration](../../src/MAF.Persistence.Sqlite/ApprovalSchema.V4.cs), [authorized maintenance](../../src/MAF.Persistence.Sqlite/AuthorizedApprovalMaintenance.cs) | Exact accepted additive delta, explicit authorization/exclusive maintenance, complete v3 logical-byte preservation, exact manifests, rollback/fresh verification |
| [Approval store](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.cs), [execution store](../../src/MAF.Persistence.Sqlite/SqliteExecutionStore.cs), [admission](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.Admission.cs) | Strict ExplicitV4, explicit profile1 admission/consumption, profile0 legacy preservation, no automatic migration. v4 reads use one consistent read transaction; ordinary updates hold the actual owner-use guard and short CAS transaction |
| [Runtime transaction](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.Runtime.cs), [receipt transactions](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.SessionReceipt.cs), [receipt format](../../src/MAF.Persistence.Sqlite/SessionReceiptFormat.cs) | Current exact admission/effect verification reused; atomic Create intent/audit/placeholder; original-response finalization with snapshot/audit; closed bounded canonical evidence and strict joins |
| [Cancellation reads](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.Cancellation.cs) | Historical evidence remains immutable; later original session requires exact receipt and earlier intent audit. Offline-only legacy mismatch preservation does not relax ordinary v3 reads or provide a runtime fallback |
| [Transport/bridge](../../src/MAF.Workflows.External/ReceiptTransport.cs), [protected runtime](../../src/MAF.Workflows.External/ProtectedExternalRuntime.cs), [OpenCode adapter](../../src/MAF.Workflows.External/OpenCode/OpenCodeClient.cs), [durable service](../../src/MAF.Workflows.External/DurableExecutionService.cs) | Attested actual request/response through owned nonredirecting transport; bounded adapter parsing; committed snapshot adopted once by PersistingClient; official Workflow retained |
| [Delivery storage format](../../src/MAF.Persistence.Sqlite/DeliveryStorageFormat.cs), [read/transition validation](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.DeliveryEvidence.cs) | Inert storage projection, exact joins/canonical digests/CAS transition checks only. No executable reservation or transport permission; reserved future names remain strings, not new Policy enum values |
| [Acceptance helper](../../tests/MAF.ApprovalAcceptance/Program.cs), [synthetic fixture](../../tests/MAF.ApprovalAcceptance/SyntheticCancellation.cs) | Test-only exact trust/profile opt-in, fake transport, bounded checkpoint gates and independent processes; never registered with Host |
| [B0 conversion](../../tests/MAF.Tests/CancellationSessionEvolutionTests.cs), [runtime](../../tests/MAF.Tests/SessionReceiptRuntimeTests.cs), [schema](../../tests/MAF.Tests/SessionReceiptSchemaTests.cs), [migration/process](../../tests/MAF.Tests/SessionReceiptMigrationTests.cs), [compatibility](../../tests/MAF.Tests/SessionReceiptCompatibilityTests.cs), [delivery data](../../tests/MAF.Tests/DeliveryStorageTests.cs) | Deterministic positive/negative, actual process termination/restart, all profiles/lifecycles, byte preservation and no unauthorized provider effects |

Roadmap/execution plan record current status; [maintenance procedure](../setup/SQLITE_APPROVAL_MAINTENANCE.md) now documents explicit v3→v4 compatibility/backup handling. Historical acceptance/security reports and accepted B1a SQL were not rewritten. Original fingerprint, OperationBinding v1 implementation/bytes, policy enum values, explicit-only activation, consumed approval mechanism and DurableExecution state/terminal rules are unchanged.

Supported v4 composition requires actual ExplicitV4 execution/approval stores on the same canonical database, the existing trusted authority/identity/configuration dependencies and owned receipt transport. An arbitrary replacement DI implementation is not an authenticated production boundary. Ephemeral/batch/listing/default raw entrypoint restrictions remain; native PCA/Reviewer are not represented as protected by this external policy. No production fake identity/source was registered.

## Exact schema and migration evidence

The C# V4Delta equals the accepted B1a SQL after raw-string indentation removal: **two STRICT tables** (`execution_session_receipts`, `cancellation_deliveries`), immutable `executions.session_receipt_required`, one explicit provider/scope/session unique index, eight triggers. Existing v3 objects/code ranges unchanged. Actual bundled SQLite observed **3.53.4**; STRICT creation, trigger execution, integrity and FK tests pass. No DDL constraint was weakened. No migration SQL touches original snapshot/evidence/audit values, policy authority or sqlite_sequence.

`AuthorizedApprovalMaintenance.MigrateV3ToV4Async` and the underlying primitive require exact-path trusted authorization plus all maintenance/stop/backup attestations. Exclusive schema gate and sorted task/delivery handles, no linked locks, ReadWrite existing DB only, Pooling=false/FK/synchronous FULL/5-second busy timeout/SQLite EXCLUSIVE transaction. Exact v3 manifest and structural snapshot/control/approval/cancellation/audit checks run before DDL. The source validator permits the known B0 temporal difference only as preserved unverified legacy data; it does not certify that session or invent a receipt. Other identity/session/audit conflicts deny.

Capture all seven original application tables **including cancellation_requests and control_audit.cancel_request_id**, plus sqlite_sequence: storage types/NULLs/serialized bytes and values. Preserve all twelve execution states and six approval statuses. Existing rows receive profile0 only; both new tables remain empty. New v4 admission/consume explicitly inserts profile1, which triggers prohibit changing. Exact v1/v2/v3 profiles reject v4 and v4 rejects other versions; default constructors remain unchanged. No >=version acceptance, downgrade, automatic chain migration or Host startup upgrade.

DDL, logical-byte verification, manifest/FK/integrity checks and final user_version4 share one transaction; fresh connection verifies again under schema gate. Actual killed migration processes and independent reader PIDs are recorded below. Unknown/future/partial/extra-object, bad audit, missing authorization/attestation and held owner cases reject without repair. Backup custody/stopped-Host attestations are operational prerequisites, not proof of human identity or actual shutdown. No real personal/company application database was migrated.

## Trusted original-response protocol and invariant closure

1. Protected Allow or exact consumed-approval Submit reuses current policy/authority/control/owner checks. Within the existing short intent transaction, CAS Created→SessionCreationIntended, append dispatch audit and insert the pending receipt with exact original caller/task/execution/run/message/fingerprint/provider/connector/scope/policy/binding. Only the committing live owner receives registration; existing metadata cannot reissue it.
2. `ReceiptTransport` owns the HttpClient construction profile. Native construction disables redirects, cookies and ambient proxy; test-owned injection is internal and admitted explicitly. An opaque existing HttpClient is rejected for v4 **before admission/HTTP**, rather than labelled trusted. Native v2/v3 composition remains compatible. The transport records the actual awaited request/response association; the adapter checks exact provider/method/origin/route/approved-root query, original request instance, successful response and bounded JSON. A fabricated response object, changed origin/route/method or another transport cannot register provenance.
3. OpenCode parsing stays in its adapter. Valid original session/directory/read-only permissions yield Verified. A syntactically valid original session with failed directory/permission validation yields Quarantined and Unknown, preserving the diagnostic session. Lost/malformed/oversized/foreign response or invalid session ID cannot finalize. The protocol proves source association under trusted composition, not provider honesty, echoed cryptographic correlation or human consent.
4. Registration is armed/consumed once, bound to the actual owner and denied after owner disposal; serialization/restoration fail. Finalization uses an independent bounded five-second local persistence budget, no network, and no authority lease held over HTTP. It checks pending receipt, exact immutable execution identity/CAS, original intent/audit/approval chain and current sticky control. Cancellation or a later policy revision does not invalidate this historical response; neither is bypassed for the next provider effect.
5. One transaction writes the finalized receipt/response audit and original SessionKnown snapshot. PersistingClient adopts that committed snapshot, avoiding a second ordinary save. Verified becomes ReadyToSubmit or preserves Unknown as applicable; quarantine stays Unknown. Required audit/canonical/CAS/ownership failure rolls back both sides, never fabricates SessionKnown. Commit-response ambiguity is handled by existing-record GET recovery, not a new registration or Create retry.
6. ExplicitV4 ordinary UpdateAsync rejects null→known session even with genuine owner/phase/earlier intent/known metadata, and cannot write dispatch intent through an ordinary update. Required-profile missing/bad evidence fails on metadata reads and effects, not merely schema checks. Structural reads verify bounded sizes **before fetching/parsing JSON**, closed fields/enums, canonical digest, original admission/approval/control/snapshot and linked audits. Read recursion is explicitly broken only at the internal receipt/cancellation correlation boundary; every historical cancellation check still runs.

Cancellation historical JSON/digest/session=null remains untouched. A later current session is accepted only with the exact finalized receipt and Create intent audit preceding the original CancellationRequested audit. Known-session replacement and all other exact identity/scope/control/audit checks remain. Receipt is source evidence, **not** permission: current independently authorized Inspect can GET a Verified original session after cancellation/restart without Submit rights. Quarantine allows control/evidence structure reads but no new provider effect. No-prompt history remains Unknown, not fabricated Cancelled.

## Corrected B0 and full execution results

| Controlled case | Original / recovered identity | Raw POST counts / outcome |
|---|---|---|
| Direct-Allow Create in flight → independent Cancel → original response | `runtime-task / runtime-execution / runtime-run / msg_runtime / ses_runtime`; historical session remains null | Create/Prompt/Abort **1/0/0**; Verified, GetCancellation/GetControl succeed, original JSON/digest unchanged, GET-only recovery Unknown |
| Approved Resume equivalent | Same original identities, original approval still Consumed, RequireApproval not converted to Allow | **1/0/0**; same positive reads/recovery, no second consumption or continuation |
| Uncancelled full official Workflow, both paths | Original correlation retained through completion and receipt version1 | **1/1/0**, Completed, correct original consumed evidence where applicable |
| Independent current Inspect-only epoch2 after owner process death | Original receipt still binds historical epoch1; same original session/message | GET-only, no Create/Prompt/Abort replay, Unknown when never submitted |

The eight historical B0 forgery/identity negatives remain. New v4 tests additionally prove that a phase-valid real-owner write after a genuine lost-response intent cannot inject a session. Invalid context, raw/opaque transport, changed route/origin/method, forged response, duplicate/reissued registration, policy/control races, audit capacity, malformed receipt and missing required receipt reject without unauthorized effects. Corruption tests restore the exact schema manifest after controlled data tampering so they verify data checks rather than merely missing-trigger rejection.

## Actual validation

| Suite | Discovered / passed | Failed / skipped |
|---|---|---|
| CancellationSessionEvolutionTests | 10 / 10 (two now positive v4) | 0 / 0 |
| SessionReceiptSchemaTests | 1 / 1 | 0 / 0 |
| SessionReceiptRuntimeTests | 28 / 28 | 0 / 0 |
| SessionReceiptMigrationTests | 46 / 46 | 0 / 0 |
| SessionReceiptCompatibilityTests | 31 / 31 | 0 / 0 |
| DeliveryStorageTests | 9 / 9 | 0 / 0 |
| Focused total | **125 / 125** | **0 / 0** |
| Full offline | **858 / 858** | **0 / 0** |

Build: solution **0 warnings, 0 errors**. Offline filter excludes only OpenCodeLiveTests; no live model/provider/company endpoint tests executed. Baseline comparison maps the two renamed B0 methods to their positive counterparts; all other historical cases remain. New cases=115, so 743→858, not a presumed unchanged count. Existing Phase 2B-1 durability/no-resubmit, M3-A2c/B2 and all M4-A1 migration/control regressions remain green. Raw TRX/discovery under ignored TestResults/home-validation; no generated outputs or test databases committed.

```powershell
dotnet build MAF.slnx --no-restore
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~SessionReceipt|FullyQualifiedName~DeliveryStorageTests|FullyQualifiedName~CancellationSessionEvolutionTests' --logger 'trx;LogFileName=phase2b2-m4-b1b-focused.trx' --results-directory TestResults/home-validation
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --list-tests --filter 'FullyQualifiedName~SessionReceipt|FullyQualifiedName~DeliveryStorageTests|FullyQualifiedName~CancellationSessionEvolutionTests'
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName!~MAF.Tests.OpenCodeLiveTests' --logger 'trx;LogFileName=phase2b2-m4-b1b-offline.trx' --results-directory TestResults/home-validation
```

### Captured process evidence

These are dedicated `MAF.ApprovalAcceptance` test processes, not production Host enablement. Checkpoint stdout gates are bounded; kill only the captured process after PID/StartTime capture; fresh reader/Inspect processes open the same synthetic database. Final focused run PID/checkpoint evidence is recorded in the following tables.

| Migration checkpoint | Killed PID → independent reader PID | Start UTC | Result |
|---|---|---|---|
| V4ProfileAdded | 37304 → 49632 | 2026-10-16T17:52:16.5700536Z | exact v3 |
| V4ReceiptCreated | 57396 → 59516 | 2026-10-16T17:52:18.0844548Z | exact v3 |
| V4DeliveryCreated | 55376 → 47828 | 2026-10-16T17:52:17.0861749Z | exact v3 |
| V4VersionWritten, before commit | 58392 → 40580 | 2026-10-16T17:52:17.5821962Z | exact v3 |
| V4Committed | 38820 → 50048 | 2026-10-16T17:52:16.0597574Z | exact v4 |

Every migration row: original v3 logical-byte digest unchanged, integrity=ok, fresh reader verifies the exact source/target manifest. PIDs may be reused across tests; captured StartTime/checkpoint and dedicated process handle identify the actual target.

| Receipt checkpoint / path | Killed owner PID → new Inspect PID | Start UTC | Durable receipt/session | Create/Prompt/Abort |
|---|---|---|---|---|
| ReceiptIntentWritten, precommit | 47888 → 27580 | 2026-10-16T17:52:10.4070929Z | none / null | 0/0/0 |
| ReceiptIntentCommitted | 57688 → 16376 | 2026-10-16T17:52:11.2414695Z | Pending / null | 0/0/0 |
| ReceiptSnapshotWritten, precommit | 46980 → 51900 | 2026-10-16T17:52:14.9511493Z | Pending / null | 1/0/0 |
| ReceiptResponseWritten, precommit | 59516 → 52088 | 2026-10-16T17:52:14.0753096Z | Pending / null | 1/0/0 |
| ReceiptResponseCommitted, Allow | 42152 → 20748 | 2026-10-16T17:52:12.0917372Z | Verified / ses_runtime | 1/0/0 |
| ReceiptResponseCommitted, Approved Resume | 29196 → 58668 | 2026-10-16T17:52:13.0674215Z | Verified / ses_runtime | 1/0/0 |

All recovered IDs remain `runtime-task / runtime-execution / runtime-run / msg_runtime`, integrity=ok. Fresh process uses independently activated **Inspect-only epoch/version2**, while receipt keeps original epoch/version1; no fresh Submit grant or original operator context is used to GET. Every recovery is honest Unknown because no prompt was submitted. The two committed cases GET the original session; earlier crash cases cannot discover/reconstruct a lost response or registration. Raw `.http` accounting contains no replacement Create, Prompt or Abort. The original execution owner is killed/OS-released before the independent process acquires that same TaskId; cancellation itself did not wait for that owner to release.

## Delivery storage, safe implementation clarifications and limits

There is **no DDL deviation** from B1a. Safe implementation details: existing Runtime transaction has a fixed private receipt-insert hook, with no SQLite types in Core or public generic transaction API; v4 consistent read/CAS ownership protection; owned transport required to close redirect/foreign-response ambiguity; effective HTTP receipt budget is the smaller trusted task/request budget, positive and <=60 seconds. No production configuration/environment/ACL changes were needed.

Delivery format/read/transition validation is inert. Reserved `AbortExistingExecution` / `ExecutionAbort` strings describe future evidence only, while today's enum values 8/4 remain undefined. A closed storage encoder handles those names without changing Core binding v1; its supported Inspect layout is golden-compared against Core. Single-operation Abort/Inspect descriptors are conservatively accepted; other storage operations deny. No evaluator rule, authority grant, executable reservation or HTTP continuation is installed. Test-owned SQL fixtures validate exact original receipt/cancel/scope/audit joins, bounded canonical evidence, lifetime uniqueness, stale CAS, rollback, monotonic stage/terminal rules and that Ack alone has outcome=None. These tests are not evidence of actual Abort or remote cancellation. Future B2 must obtain genuine current operation-specific authorization before creating an intent, irrespective of a row's contents.

Migrated v3 rows remain profile0/no receipt. Known exact original-session legacy GET is independently authorized and does not become Verified. B0 legacy mismatch raises LegacyCorrelationUnverified with no provider GET. Required profile1 never downgrades to that path. Migration/legacy reads do not recreate continuations or restore expired permissions. API metadata null for a legacy receipt is not a bearer grant.

Remaining limits: a crash after Create intent but before receipt commit can lose the remote session identity; preserve Unknown and never retry Create/Prompt. Already-sent HTTP cannot be revoked atomically with SQLite. One lifetime receipt/task and future single Abort reservation intentionally trade availability for non-replay; no generation replacement, retry worker or archive/prune/reset. Audit remains hard bounded; receipt finalization may fail safely at capacity. OS process-crash tests do not prove power-loss, distributed ownership or hostile same-principal DB/binary/transport integrity. Synthetic identities/context IDs/hashes do not authenticate a real human or policy publisher. M3-C protected identity/writer/transport custody, provenance, revocation and anti-rollback remain mandatory before production.

**Next separately authorized narrow increment: M4-B2** current exact Abort authorization + independent delivery owner + atomic one-attempt reservation + guarded bounded original-session POST/ack, reusing this storage. Ack must remain distinct from terminal execution; M4-C restart reconciliation needs its separate runtime/crash acceptance. Do not enable either, M3-C, M5 or PCA→OpenCode→Reviewer automatically.

Final review: 30 scoped files; staged whitespace check and 94 relative file links/fences passed. Accepted B1a DDL and compiled raw-string delta compare equal after indentation removal. Historical TRX case **multiset** comparison preserves all 743 cases after the two authorized method conversions, with exactly 115 additions and no missing case. No generated test output/database, key, corporate event record, ACL/security-policy change or unrelated `.vscode/` content is staged. No endpoint restriction surfaced in the command/test outputs.

**STOP after M4-B1b.** Controlled receipt/storage and positive recovery acceptance passed; no unresolved B1b contract blocker required a permissive fallback. Full Phase 2B-2 and corporate compatibility remain unaccepted.
