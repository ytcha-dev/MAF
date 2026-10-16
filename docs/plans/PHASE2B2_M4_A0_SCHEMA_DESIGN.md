# M4-A0 — Cancellation evidence schema and offline migration contract

2026-10-16 · baseline `516b447e6acd2e97ee56fb6e36ef7bc1cb217d7b` · `main` equals fetched `origin/main` before edits.

**Proposed for owner review, not implemented or approved for migration.** This documentation-only gate supplies one recommended design for the [M4-A blocker](../acceptance/PHASE2B2_M4_A_RESULTS.md). M4-A remains incomplete. No DDL was executed, database opened/migrated, runtime changed or model/provider invoked in M4-A0. Pre-existing untracked `.vscode/` is excluded.

## 1. Source baseline and narrow recommendation

Read [B2 results](../acceptance/PHASE2B2_M3_B2_RESULTS.md), [ADR-004](../architecture/ADR-004_DURABLE_EXECUTION_RECOVERY.md), [ADR-005](../architecture/ADR-005_POLICY_APPROVAL_CANCELLATION.md), [ADR-006](../architecture/ADR-006_TRUSTED_BROKER_BOUNDARY.md), [execution plan](PHASE2B2_EXECUTION_PLAN.md), [Roadmap](../ROADMAP.md), existing [maintenance procedure](../setup/SQLITE_APPROVAL_MAINTENANCE.md), and actual source:

| Source / current behavior | Proposed seam |
|---|---|
| [ApprovalSchema](../../src/MAF.Persistence.Sqlite/ApprovalSchema.cs): explicit empty/v1 → v2 only; exclusive schema gate, BEGIN EXCLUSIVE, snapshot-byte comparison | Separate explicit **v2 → v3** migration, no change to old target/version meaning |
| [SqliteApprovalStore](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.cs): Transaction requires exactly v2; authority guard and bounded audit; RequestCancellationAsync uses original approval's CanControl | Explicit approval-store profile; v3 cancel coordinator independently evaluates Cancel. Old approval-based cancellation method rejects in v3 |
| [SqliteExecutionStore](../../src/MAF.Persistence.Sqlite/SqliteExecutionStore.cs): LegacyV1 or ExplicitV2; real OS ownership; admission-use helper demands ExplicitV2 | Add ExplicitV3, exactly v3; retain real owner/path validation for execution operations |
| [PolicyRequest / descriptor](../../src/MAF.Core/Policy/PolicyContracts.cs), [OperationBinding](../../src/MAF.Core/Policy/OperationBinding.cs) | Reuse complete existing contracts and **binding v1 unchanged**, including RequestCancelExecution=3 / ExecutionCancellation. No policy redesign |
| [TaskControl / ControlAudit](../../src/MAF.Core/Policy/ApprovalPersistence.cs) | Same sticky intent projection; add optional CancelRequestId to audit projection, keep original fields and enum numeric values |
| [AuthorizedApprovalMaintenance](../../src/MAF.Persistence.Sqlite/AuthorizedApprovalMaintenance.cs) | Reuse exact-resource authorizer seam with explicit target-3 maintenance method; no production authorizer/CLI |
| [Test helper](../../tests/MAF.ApprovalAcceptance/Program.cs), [persistence tests](../../tests/MAF.Tests/ApprovalPersistenceTests.cs) | Extend existing small helper for migration/cancel checkpoints after separate implementation authorization |

**Recommend:** one immutable `cancellation_requests` table, one nullable FK column on existing `control_audit`, explicit v3 profiles, offline additive migration. Keep all six existing v2 tables and execution/approval/control formats. Retain cancellation operation identity in existing `authorization_operations`; this is an index/identity join, not a substitute for full evidence. No mutable delivery state, new agent loop, Workflow redesign or provider bridge.

M3-C remains deferred/blocked and mandatory before production trust-dependent activation. Hashes, stored context IDs and this schema do not authenticate humans or resist a malicious same-account DB writer. Only controlled synthetic trust is contemplated for the next increment.

## 2. Final proposed DDL

The following is the exact proposed **schema delta**, executed only inside the separately authorized migration's existing exclusive transaction. No IF NOT EXISTS, OR REPLACE or silent repair. Existing v2 `authorization_operations`, `task_controls` and `control_audit` definitions remain except the explicit audit column addition below.

```sql
CREATE TABLE cancellation_requests (
    cancel_request_id TEXT PRIMARY KEY NOT NULL
        CHECK(length(cancel_request_id) BETWEEN 1 AND 160),
    operation_id TEXT NOT NULL UNIQUE
        REFERENCES authorization_operations(operation_id) ON DELETE RESTRICT,
    task_id TEXT NOT NULL UNIQUE
        REFERENCES task_controls(task_id) ON DELETE RESTRICT,
    execution_id TEXT NOT NULL CHECK(length(execution_id) BETWEEN 1 AND 160),
    target_operation_id TEXT
        REFERENCES authorization_operations(operation_id) ON DELETE RESTRICT,
    caller_subject TEXT NOT NULL CHECK(length(caller_subject) BETWEEN 1 AND 160),
    authentication_context_id TEXT NOT NULL
        CHECK(length(authentication_context_id) BETWEEN 1 AND 160),
    authority_id TEXT NOT NULL CHECK(length(authority_id) BETWEEN 1 AND 160),
    policy_epoch INTEGER NOT NULL CHECK(policy_epoch > 0),
    policy_version INTEGER NOT NULL CHECK(policy_version > 0),
    policy_digest TEXT NOT NULL CHECK(length(policy_digest) = 64
        AND policy_digest NOT GLOB '*[^0-9A-F]*'),
    evidence_format INTEGER NOT NULL CHECK(evidence_format = 1),
    binding_version INTEGER NOT NULL CHECK(binding_version = 1),
    binding_digest TEXT NOT NULL CHECK(length(binding_digest) = 64
        AND binding_digest NOT GLOB '*[^0-9A-F]*'),
    expected_control_version INTEGER NOT NULL CHECK(expected_control_version >= 0
        AND expected_control_version < 9223372036854775807),
    committed_control_version INTEGER NOT NULL
        CHECK(committed_control_version = expected_control_version + 1),
    evaluated_at_ticks INTEGER NOT NULL CHECK(evaluated_at_ticks BETWEEN 0 AND 3155378975999999999),
    expires_at_ticks INTEGER NOT NULL CHECK(expires_at_ticks BETWEEN 0 AND 3155378975999999999),
    requested_at_ticks INTEGER NOT NULL CHECK(requested_at_ticks BETWEEN 0 AND 3155378975999999999),
    evidence_json TEXT NOT NULL
        CHECK(length(CAST(evidence_json AS BLOB)) BETWEEN 1 AND 16384),
    CHECK(evaluated_at_ticks <= requested_at_ticks
        AND requested_at_ticks < expires_at_ticks)
);

ALTER TABLE control_audit ADD COLUMN cancel_request_id TEXT
    REFERENCES cancellation_requests(cancel_request_id) ON DELETE RESTRICT;

CREATE UNIQUE INDEX one_cancellation_requested_audit
    ON control_audit(cancel_request_id)
    WHERE cancel_request_id IS NOT NULL AND code = 6;

CREATE TRIGGER cancellation_requests_no_update
BEFORE UPDATE ON cancellation_requests
BEGIN
    SELECT RAISE(ABORT, 'CancellationEvidenceImmutable');
END;

CREATE TRIGGER cancellation_requests_no_delete
BEFORE DELETE ON cancellation_requests
BEGIN
    SELECT RAISE(ABORT, 'CancellationEvidenceImmutable');
END;
```

`PRAGMA user_version=3` is written **last**, after validation, in the same migration transaction. Numeric audit code 6 is the existing CancellationRequested value; no enum renumbering. SQLite PK/UNIQUE indexes cover cancel ID, operation ID and task winner; no redundant `(task_id,execution_id)` unique index or general list index. `task_id UNIQUE` deliberately permits only **one cancellation winner for the lifetime of a TaskId**, even if a caller supplies another ExecutionId. D12 replacement remains unsupported.

New cancellation operation metadata uses existing `authorization_operations` with kind=3 and generation=1, independent of the original Submit operation. Existing UNIQUE(task_id,kind,generation) also blocks competing cancel-operation identities. Never create an Approval row for direct Allow cancellation. Original target operation is linked if one exists; imported legacy execution may have NULL target_operation_id. A NULL target link does not permit an unknown TaskId.

SQL protects key uniqueness, FK existence, bounded evidence, basic numeric/digest shape and evidence immutability. It does **not** authenticate identity, parse canonical metadata, prove policy rights, require the presence of a child audit row or enforce every cross-table equality. The fixed trusted coordinator and v3 read validation must check those invariants (§3/§6); SQL triggers are consistency protection, not an OS trust boundary. No arbitrary external SQL API is added.

Audit rows linked to a cancellation use `cancel_request_id`; original `approval_id` retains its meaning (original approval if relevant, otherwise NULL). Revocation audits may reference the same cancel ID; only the CancellationRequested audit is unique. Appending a nullable column leaves old row values unchanged; no old audit backfill. Existing Audit inserts already name their columns, so shared v3 approval operations can continue to write NULL for ordinary events. No audit pruning, updating, deletion or new event framework.

## 3. Evidence format, ownership of fields and read validation

`evidence_json` is a closed **format-1 DTO** with exactly two required properties: `Operation` (all existing PolicyRequest fields) and `Capability` (all existing CapabilityDescriptor fields). Their nested schema is the contracts at this baseline, including complete AuthorizationScope, epoch/version Value objects, nullable ProviderSessionId and SupportedOperations. No polymorphism, extension data, arbitrary dictionaries or serialized trusted identity object. Enum values use exact case-sensitive enum **names**, integer enums/unknown names denied. Reject duplicate or unknown JSON properties, absent fields, invalid UTF-8/UTF-16, excessive depth (>8), arrays >3 operations, or payload >16384 UTF-8 bytes **before** allocating/parsing an unbounded object. SQL length check is a second line of defense, not the parser's allocation guard.

Production code must define an explicit DTO/options rather than rely on future default JsonSerializer behavior. Property ordering is irrelevant. Normalize the three hash strings in PolicyRequest/scope to uppercase on write; normalize descriptor SupportedOperations to ordinal enum-name order for stable storage. Validate duplicate operations before sorting. Compare deserialized typed fields/canonical bytes, never raw JSON string equality.

Keep these encodings independent:

| Version / value | Contract |
|---|---|
| SQLite schema 3 | Tables/columns/indexes/triggers and compatibility profile |
| evidence_format=1 | Closed JSON DTO above; bounded parser contract |
| binding_version=1 | Existing `OperationBinding.Encode(Operation, Capability)`: fixed fields, strict UTF-8 byte lengths, big-endian numbers; unchanged Core algorithm |
| binding_digest | Uppercase SHA256 of those canonical bytes, verified on every evidence read |
| policy_digest | Existing PolicySnapshotBinding digest of trusted snapshot at authorization, not raw policy text |
| UTC ticks | Signed SQLite INTEGER containing .NET UTC ticks since 0001-01-01, never Unix milliseconds/local ticks; intervals are [evaluated, expires) |

Writer/read checks, failing closed on any mismatch:

- Operation is RequestCancelExecution, capability ExecutionCancellation, TaskAdmission boundary, read-only scope. Descriptor connector/capability matches operation; supported operations include Cancel and contain only existing supported kinds. Never infer permission from Enabled/Healthy.
- Operation fields exactly equal indexed task/execution/operation/caller/authority/epoch/version columns; metadata IDs use existing store ASCII `[A-Za-z0-9_.:-]`, 1–160 characters, including operation/target IDs. ResourceId remains already verified canonical root, bounded to existing Core 1024-character rule; descriptor adapter/version strings also bounded by existing Core validation. No new prompt field.
- Joined authorization_operations row equals operation TaskId/ExecutionId/kind=3/generation=1. Optional target row has same task/execution, kind=Submit; target need not have same caller as cancelling actor. Cancellation operation's CallerSubject is **the independently verified cancelling subject**.
- Recompute binding from complete Operation + Capability, check stored binding_version/format; verify request fingerprint and run/message/session correlation against current authoritative execution, or pre-bound original authorization when no execution exists. No caller-supplied session or resource overrides.
- Fresh trusted identity resolution proves caller at write/replay; AuthenticationContextId is a bounded non-secret provenance identifier, **not a token or durable proof**. Replay may use a newly verified context for the same subject; original context ID remains unchanged. Subject/operation identity is exact; context renewal does not manufacture human proof.
- At commit, control matches task/execution, cancel ID, actor, requested time (UTC-equivalent to existing task_controls.requested_at format) and committed version. Later read requires same sticky winner/actor/time and control version **at least** committed version, allowing separately authorized future control changes. M4-A1 adds no such later mutation itself.
- Exactly one code-6 audit for this cancel ID, matching task/caller/committed version/requested time/epoch/version; required revocation audit rows exist. Validate optional original approval link if supplied. Missing/orphan/mismatched evidence, operation/control/audit is storage inconsistency, never permission to repair or dispatch.

SQL index columns intentionally duplicate only query/join/CAS/provenance fields; complete scope/descriptor stays in one typed snapshot. Tuple equality and canonical recomputation prevent drift. No whole policy snapshot, prompt/response, model reasoning, token, password, credential, transcript or free-form exception is stored. Policy metadata and roots remain sensitive operational metadata and require normal protected custody. A historical digest cannot prove policy authenticity after its source is gone; current trusted source is always required for authorization.

## 4. Uniqueness and replay truth table

All outcomes below require preliminary authentication; do not disclose target/winner data before independent current policy authorization. Proposed stable codes are part of M4-A1, not existing APIs.

| Situation | Proposed result / persistent effect |
|---|---|
| New exact target, active current Cancel Allow, expected control matches, no winner | CancellationRecorded; one immutable row + operation + control v+1 + audit, no execution/provider effect |
| Same CancelRequestId, same original operation/descriptor/canonical digest/target/subject and original expected version; recorded deadline still valid; current authority/policy/rights unchanged | CancellationAlreadyRecorded; return original row/version/time, no write or extra audit. Re-evaluation timestamps are fresh and are not compared as request identity |
| Same ID, any binding/operation/scope/fingerprint/target/caller/expected-version difference | CancellationIdConflict; no write |
| Same ID with caller passing **current committed** version instead of original expected version | CancellationIdConflict; replay must retain original input CAS version |
| Different ID for task with existing winner | CancellationAlreadyRequested; never replace/merge; do not expose winning subject/IDs without separate read authorization |
| Same TaskId, different ExecutionId | TargetBindingMismatch, including before replay success; no second generation/replacement |
| Same subject with new valid authentication context | Exact replay permitted subject to all current policy/deadline checks; stored original context not rewritten |
| Replay expired, caller rights revoked, policy unavailable/unactivated/changed | CancellationNotAuthorized (or existing stable authority failure); no write/refresh. Existing intent remains sticky. Separately authorized status read may report historical intent |
| Target terminal, no existing winner | AlreadyTerminal; no new row/control bump/audit, preserve terminal truth. Check independent Cancel authorization before disclosing this outcome |
| Target terminal, existing exact replay | Normal exact-replay validation may return original recorded result; does not cancel terminal or reopen it. An expired/revoked replay still fails |
| Pending/Approved operation, no execution | Exact target operation required; can record intent and revoke Pending/Approved in same transaction, block later admission/consume |
| Consumed approval + Created | Keep Consumed/original decision; record cancel control only. No new handoff, Create or Submit |
| Direct-Allow execution, no approval | Supported via existing execution identity/current trusted scope; optional original target operation link, never manufacture approval |
| Unknown task, missing control, inconsistent target/control | TargetNotFound / TargetBindingMismatch; no tombstone, no new execution/control row |
| Existing v2 legacy cancel, no independent evidence | LegacyCancellationIntent; cannot replay as verified, replace, backfill evidence or grant Abort. Status disclosure remains authorized |

Only an active **Allow for RequestCancelExecution** admits intent in M4-A1. RequireApproval does not become Allow; return CancelApprovalNotSupported without creating a Pending cancel approval in this increment. Current policy and original record expiry are both checked on replay; do not extend expiry because a caller repeated the command. Binding v1 remains unchanged: CancelRequestId and expected control version are envelope fields compared explicitly alongside the operation digest, not silently added to its canonical algorithm.

If an in-flight effect has refined session identity or trusted configuration since the original cancel, do not rewrite the recorded operation or silently bind the same ID to the new values. Exact replay requires the original binding still validate against the target; otherwise return TargetBindingMismatch and allow only separately authorized historical status inspection. This conservative replay limitation does not clear the original sticky intent or prohibit genuine terminal reconciliation.

## 5. Offline v2 → v3 migration and profiles

### Procedure and validation

1. New explicit `MigrateV2ToV3Async` maintenance primitive/wrapper (names proposed) accepts exact canonical database resource, trusted maintenance authorization and existing three OfflineMaintenance attestations. Default/missing authorizer denies before DB access. No caller snapshot/epoch, repo setting, startup migration, CLI, Broker or production authorizer. Owner approval to implement synthetic tests is not approval to migrate a real database.
2. Stop **all** old/new Hosts, workers and maintenance clients and prevent restart for the maintenance window. Capture managed PID/StartTime inventory; never kill by broad process name. Resolve consistent backup/journal state per existing maintenance procedure, verify backup read/integrity and rollback plan in protected non-repo custody. Attestations/lock availability are not proof all old binaries are stopped.
3. Obtain exclusive `.schema.lock` and all existing TaskId lock handles; fail on any owner, do not delete locks. Open existing DB ReadWrite (never create), Pooling=false, foreign_keys=ON, busy timeout 5s, synchronous=FULL, locking_mode EXCLUSIVE; BEGIN EXCLUSIVE. No network or human interaction inside transaction.
4. Require **exact user_version=2** and complete known v2 schema manifest: all table/index definitions, columns/types/nullability/keys/checks/FKs; no unknown views/triggers/indexes or extra objects except expected sqlite internal objects. No proposed v3 object/column may already exist. Reject nonempty version-0, v1, v3, future/partial variants; never repair or treat already-v3 as migration success. Separate read-only validation can report a valid v3.
5. `integrity_check=ok`, empty foreign_key_check; validate row/JSON vs indexed-column consistency using existing approval/execution contracts. Check control version/field coherence, operation joins and audit bounds. Existing revoked/expired/Consumed are valid data, not migration errors; incomplete legacy cancel fields require rejection/manual review, not guessed values. The old generic approval primitive can technically persist kind=3 operations: reject any such pre-existing operation as `PreexistingCancellationOperation` for explicit review rather than silently appropriating it as v3 evidence. The accepted B2 application uses kind=1 for approvals; do not assume arbitrary v2 library data follows that restriction.
6. Capture ordered original values (including storage type/NULL and UTF-8/BLOB bytes) for **all six** v2 tables and sqlite_sequence values, not only execution snapshots. Record exact executions/approval_requests snapshot BLOB bytes; do not deserialize/reserialize stored values. Apply §2 DDL; cancellation table starts empty, every old audit's new column is NULL. No INSERT/UPDATE/DELETE on old data or sqlite_sequence.
7. Recompare all original-column values/bytes/row counts, snapshot bytes and audit sequence; exact new manifest/integrity/FKs and zero evidence rows; legacy classification counts match pre-migration controls. Set user_version=3 last and commit atomically. No AuthorityAdvanced/business audit is fabricated by migration; sanitized maintenance manifest belongs to controlled maintenance evidence, not a provider ledger.
8. New connection validates schema3/manifest/integrity/FKs/old-byte baseline before any separately authorized client opens it. Preserve backup and maintenance evidence. Migration never activates policy or starts Host/provider.

Byte-for-byte preservation means **logical existing column/snapshot bytes**, not identical DB-file pages/hash after DDL. ALTER may change schema representation; physical file hash is a backup identifier, not the expected post-migration hash. Existing approval/control/audit tuples and immutable execution identities must not change. The new nullable audit field is the only additional value on historical rows.

### Failure windows and rollback

| Failure window | Required result / recovery |
|---|---|
| Before BEGIN or schema validation failure | No schema/data changes; report failure, preserve backup |
| After CREATE/ALTER/index/trigger or user_version write, before commit | Entire transaction rolls back on fault/process death; fresh SQLite recovery yields exact v2 and old logical bytes, no partial v3 objects/column |
| Commit response lost / process killed around commit | Fresh connection may find complete v2 **or** complete v3. Validate actual manifest/data; no blind rerun or downgrade. Half-v3 is rejection, not repair |
| Successful v3, before any new writes | Only explicit owner-approved offline restore of the complete consistent backup can abandon migration; first preserve diagnostic DB. Never set user_version back or mix main DB with stale WAL/journal |
| Any new v3 control/evidence/authority write | v2 backup restore can lose cancellation/revocation/high-water; not an automatic rollback option. Stop and require separately authorized authority/control reconciliation |

Do not delete hot journal/WAL to force progress. Preserve native SQLite recovery artifacts; security alerts stop affected execution. Power loss, hostile DB writer and backup rollback resistance remain outside process-crash guarantees.

### Legacy cancel treatment

Migration creates **no** cancellation_requests rows from old task_controls.cancel_id or old CancellationRequested audit. Classification is derived: non-NULL control cancel ID without corresponding valid evidence = `LegacyIntentUnverified` (new rows with missing expected evidence are instead corruption). Existing v2 control/audit values, IDs, actor/time and sticky stop behavior remain. No signature, operation, Cancel binding, freshness or Abort grant is inferred from the old Submit approval.

At initial migration the expected legacy set/count is recorded in the offline validation evidence; while v3 runs, the old approval-based cancel API is disabled, so no new unverified intent can be created by supported writers. Under trusted-writer assumptions, these are the migrated legacy rows. A missing row for a cancel ID that has linked v3 audit or a kind=3 operation is always corruption. No claim is made that external hostile deletion can be distinguished cryptographically from legacy data; it remains denied for delivery in either case.

### Compatibility matrix (proposed additions only)

| Store / profile | DB v1 | DB v2 | DB v3 | Empty / unknown / future |
|---|---|---|---|---|
| Execution default LegacyV1 | Existing behavior | Reject | Reject | Empty v0 → v1 unchanged; unknown/future reject |
| Execution ExplicitV2 | Reject | Existing controlled read/update/admission ownership | Reject | Reject; no create/migrate |
| Execution ExplicitV3 (new opt-in) | Reject | Reject | Controlled read/update/admission ownership | Reject; no create/migrate |
| Approval default ExplicitV2 (new name for unchanged default) | Reject | Existing API/semantics, including historical primitive | Reject | Reject; no create/migrate |
| Approval ExplicitV3 (new opt-in) | Reject | Reject | Shared activation/approval/admission/resume/effect algorithms + new cancellation path; old RequestCancellationAsync rejects | Reject; no create/migrate |
| Existing empty/v1 → v2 maintenance API | Explicit known v1 → v2 | Reject as today | Reject | Empty known v0 → v2 unchanged, others reject |
| New v2 → v3 maintenance API | Reject; perform separately approved v1→v2 first | Exact offline migration | Reject; validate separately | Reject |

Do not broaden any check to `version >= 2` or make existing ExplicitV2 mean v2/v3. Fresh v3 fixture setup explicitly creates v2 through existing maintenance, then explicitly migrates to v3; no implicit chain on startup. All participating execution/approval/admission/effect stores must share exact path **and profile**; mismatches fail before mutation/disclosure, no fallback.

Shared v3 algorithms must be reviewed for SQL positional INSERTs: existing writes to authorization_operations/approval_requests/executions remain valid because their shape is unchanged; audit uses column lists. Profile-specific open/transaction/schema validation and real-owner helpers must support exact v3 consciously. ExplicitV3 must also reject direct `ExecutionStore.CreateAsync` as AtomicAdmissionRequired, just as ExplicitV2 does; adding an enum without updating that existing profile-specific guard would create a bypass. No duplicate durable state machine. Old binaries still reject v3 based on version; old profile behavior/tests remain unchanged. Host stays ProductionTrustUnavailable and receives no profile-routing or DI change.

## 6. Future M4-A1 transaction and race contract

Execution admission keeps owner → authority lease → short writer transaction. **Cancellation has no execution ownership acquisition**: verified identity/target intent → authority acquisition outside SQL → shared schema lease → short SQLite IMMEDIATE writer transaction. No transaction waits for an execution lock; authority callbacks are bounded local verification. A running owner's long OS lock must not prevent cancel commit. Standalone Abort coordination is outside A1.

Within one fixed transaction:

1. Exact v3/profile check and active durable authority/epoch/version/digest check; independently authenticate cancelling subject and evaluate exact Cancel operation from trusted config/current authoritative target. Recheck execution or original authorization, no invented session/identity. Only current Allow + CanControl(cancel operation), never CanControl(original Submit).
2. Validate existing winner/replay first using §4; otherwise require expected control/execution and no cancel, target exists and is not terminal; no arbitrary new task-control creation. Check CancelRequestId globally against both cancellation_requests and task_controls (including migrated legacy IDs); an ID used for a different target is a conflict, never a new request.
3. Insert kind=3 operation metadata and immutable cancellation_requests evidence as one logical evidence step. No approval creation/consumption. Any operation-ID collision rolls back.
4. CAS task_controls by task/execution/expected version/cancel IS NULL; set original cancel ID/subject/time and version+1. Do not touch execution snapshot, AbortRequested, phase or terminal.
5. Revoke only Pending/Approved associated approvals through existing CAS; preserve original operation/binding/DecisionId, keep Consumed unchanged. Insert their bounded revocation audits with cancel FK.
6. Insert bounded CancellationRequested audit, code=6 with cancel FK, original approval link if applicable. Read back/verify all evidence/control/operation/audit invariants; revalidate identity/rights/time/active policy at both ends. Commit all or rollback all. Return data only.

No HTTP, Workflow call, CTS notification or delivery is required for commit. Missing caller, unavailable source, Deny/RequireApproval, audit capacity exhaustion, clock invalidity/overflow, CAS conflict or inconsistent row leaves zero partial effects. Exact replay does not bypass current authorization or evidence validation merely because a control row contains the same ID.

| Race | Linearization / guarantee |
|---|---|
| Cancel vs new admission | Existing operation/control target only. Cancel writer first blocks new admission; admission first creates one execution, then cancel can update its control without waiting for owner. Unknown task rejected, not preemptively reserved |
| Cancel vs approval consume | Cancel first revokes/blocks consumption. Consume first keeps exact Consumed + Created; cancel later blocks next guardable effect, not a rollback of consumption |
| Cancel vs dispatch intent | Both writers verify same current control. Cancel first denies intent; intent first remains historical. Control change before wire preflight denies next HTTP; change after preflight may not prevent already sent HTTP |
| Cancel vs active policy revision | Authority acquired outside SQL, durable writer serializes activation/cancel. New active tuple first rejects stale candidate. Cancel first is historical intent; not a perpetual right to Abort under a later policy |
| Two cancel processes while owner runs | Unique task/operation constraints + control CAS yield one winner, one conflict or exact replay. Neither needs owner's lock. No writer waits for another process's human approval |
| Cancel vs terminal update | If terminal visible to transaction, AlreadyTerminal no intent; if cancel commits first and owner later records genuine terminal, retain both intent and terminal truth. No overwrite as Cancelled |

Caller tokens, inspection/HTTP timeout, Host shutdown/crash or policy revocation remain distinct; none generates cancel intent. Existing guard sees control changes before subsequent effects; no remote-stop SLA or HTTP/SQLite atomicity claim. M4-B must independently authorize and coordinate Abort; no schema row here is a bearer grant.

## 7. Alternatives and trade-offs

| Option | Evidence / atomicity / compatibility | Decision |
|---|---|---|
| Existing v2 untouched | Short CAS/audit works, but no independent complete Cancel binding or exact replay history, particularly direct-Allow without approval | Insufficient, confirmed M4-A blocker |
| Add bounded cancel snapshot directly to task_controls + audit FK | Could preserve complete evidence atomically with a versioned migration; technically viable if immutable snapshot/identity/replay invariants specified. Changes hot mutable control row, mixes full historical authorization with current control projection; needs no fewer compatibility/legacy decisions | Smaller table count but poorer lifecycle separation; reject as recommendation, not as logically impossible |
| Expand authorization_operations with generic snapshot and audit operation FK | Could be correct, but widens all operation kinds, duplicates/conflicts with existing approval snapshot authority, increases validation/migration surface | Defer generalization; no generic operation framework |
| Dedicated cancellation_requests + nullable audit FK (selected) | One bounded immutable evidence row, existing short transaction, narrow additive schema; two deliberate changes, explicit profiles; indexed fields duplicated with mandatory equality checks | Best narrow ownership boundary, no delivery state yet |
| Synthetic Approved row, actor/cancel ID payload packing, sidecar | Conflates lifecycle/column meaning or loses same-DB atomicity | Rejected |

Audit linkage is an actual additive column (not an overloaded ApprovalId), so all maintenance schema manifests and audit projections need deliberate updating. SQL alone does not guarantee a mandatory child audit; trusted coordinator checks it before commit and evidence readers check it afterward. This is the same trusted-writer model as existing approval snapshots, not tamper-proof storage. No retention/pruning workaround is added; full audit capacity blocks mutation.

## 8. Required implementation tests and exact next boundary

After owner acceptance, M4-A1 may implement **this reviewed v3 migration/profile delta plus provider-free independent cancellation admission**, using the existing Core evaluator/trusted resolver/authority lease, stores and smallest test helper. Likely files: ApprovalSchema + AuthorizedApprovalMaintenance; SqliteExecutionStore profile/owner validation; SqliteApprovalStore open/profile/audit and new cancellation partial; narrow Core cancellation record/coordinator/audit projection; one application cancellation component; synthetic helper and focused tests. Review SqliteApprovalStore.Admission/Resume/Runtime solely for exact-profile integration and existing shared semantics. No changes to policy binding/fingerprint, Host routing/DI, official Workflow launch, OpenCode APIs or native PCA/Reviewer. Implementation must not broaden beyond these seams without reporting a blocker.

| Required group | Concrete proof before A1 acceptance |
|---|---|
| DDL and fresh setup | Explicit v2 setup→v3; exact tables/column/index/triggers; invalid sizes/digests/versions/FKs/duplicate task+IDs rejected; evidence UPDATE/DELETE rejected; audit code6 uniqueness |
| Migration preservation | Nonempty synthetic v2 with Direct-Allow, Pending/Approved/Consumed, all terminal/nonterminal states, legacy cancel, nonzero sqlite_sequence; every old value/storage type/snapshot byte preserved, new evidence empty, audit link NULL |
| Unsafe inputs | Missing maintenance authority/attestations; active owner/schema lease; extra/changed objects, partial v3 with version2, future version, missing table, inconsistent snapshots/legacy fields; all fail without repair |
| Migration process crashes | Kill captured helper after CREATE, ALTER, final version write before commit, and after commit; fresh process obtains complete v2 or v3, old bytes and integrity/FKs correct; no fake exception-only crash claim |
| Profiles/coexistence | Every matrix cell; old profile future rejection unchanged; mismatched store/path/profile denies; v3 shared Request/Decide/Consume/Activate/Resume/effect guard regressions; old cancel API denied on v3 |
| Independent authorization | Missing/forged caller, Submit-only, Inspect-only, expired/unavailable/unactivated/mismatched policy, unknown capability, scope/fingerprint/config/target mismatch, RequireApproval; zero control/evidence/Workflow/HTTP |
| Evidence/replay | Reordered JSON same canonical binding; every bound field/control/ID difference conflicts; parser duplicate/unknown/oversize/enum/version cases; renewed trusted context same subject; expired/revoked replay denied; no raw JSON equality |
| Legacy/terminal | No fabricated evidence or grant, preserved sticky stop; malformed new links fail; terminal AlreadyTerminal unchanged; Pending/Approved revocation versus Consumed preservation; direct-Allow without approval works |
| Transaction/races | Inject fault after each evidence/control/revocation/audit write and before commit; complete rollback. Deterministic admission/consume/intent/activation races in both orders; bounded busy failure |
| Independent owner + restart | Real running test-owned process holds execution lock; second authorized process commits cancel without acquiring it; exact duplicate/conflicting third request; captured PIDs/control versions. Kill cancel process immediately after commit, fresh process reads exact evidence+audit/winner, no new continuation |
| Side-effect accounting | Explicit callback counters and fake raw method/path ledger: new cancellation path Workflow/Create/Submit/Observe/Abort all zero, no replacement IDs. Full historical recovery/no-resubmit suite and build remain passing |

M4-B delivery and M4-C acknowledgement/terminal reconciliation remain separately authorized. M3-C is still blocked on authenticated policy provenance, real human proof where required, writer/credential isolation, revocation and external rollback anchor. Metadata evidence is not signed consent; production remains disabled.

## 9. Owner decisions and validation status

Recommended decisions requiring owner acceptance before code:

1. Schema3 additive delta, 16KiB format-1 evidence bound and nullable audit FK; keep original fields byte-preserved and old results historical.
2. One lifetime cancellation winner per TaskId (stronger than task/execution pair); no generation replacement/reset, no cancellation evidence update/delete/expiry cleanup.
3. Exact replay uses original CAS version + bound operation; renewed trusted context allowed for same subject, but current rights/authority/deadline required. Terminal/no-winner returns AlreadyTerminal without audit write.
4. Legacy intent remains unverified/sticky and never auto-upgraded; old cancellation primitive denied on ExplicitV3. Profiles remain exact; migration alone enables no execution/trust.
5. Authorize only the A1 migration/profile + provider-free admission/test boundary in §8. Actual personal/company DB maintenance and production trust still need independent approval.

No undecided DDL placeholder remains in this recommendation. Main trade-offs are lifetime uniqueness, unavailable replay after policy change/expiry, metadata duplication checked by coordinator, and application-enforced mandatory audit linkage. If owner chooses different semantics, update this design before implementation; do not improvise a new migration mid-increment.

M4-A0 validation is source/document consistency and relative-link/diff review only. Proposed SQL is **not executed or acceptance-tested**. No build/test rerun is needed for documentation-only edits: the immediately preceding M4-A review recorded 259/259 focused, 604/604 offline and build 0 warnings/errors at the identical source baseline. Those historical results do not prove v3 migration, profile compatibility or cancellation admission. No corporate endpoint/security-policy changes, provider POST or live session occurred.

Actual document checks: `git diff --check` passed; 70 relative file links across this design, Roadmap and execution plan resolve, and fenced blocks balance. Text-only DDL inventory matches one new table, one nullable audit column, one partial unique index and two immutability triggers; no SQL was executed for this check. Final commit scope is exactly those three Markdown files; no ADR/history/source/test/helper changes or unrelated files.

**M4-A0 design delivered for review. STOP; await owner approval before schema v3, M4-A1, M4-B/C, M3-C or M5 implementation.**
