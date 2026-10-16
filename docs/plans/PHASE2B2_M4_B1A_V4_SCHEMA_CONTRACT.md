# M4-B1a — v4 schema and trusted session receipt contract

2026-10-17 (Asia/Taipei). Source baseline and fetched `origin/main`: `6e42c8413d9b80603f7d20e92c7fa29cc6b4c362`. **Design proposal ready for owner acceptance; not an implemented or tested v4 schema.** Only documentation changes are authorized here. The [B0 defect](../acceptance/PHASE2B2_M4_B0_RESULTS.md) remains unfixed. M3-C production trust is blocked, not waived; Host, Abort and real human approval remain disabled.

This contract closes the field-only questions in the earlier [B/C proposal](PHASE2B2_M4_BC_DELIVERY_DESIGN.md). That document and the [A0 contract](PHASE2B2_M4_A0_SCHEMA_DESIGN.md)/[A1 results](../acceptance/PHASE2B2_M4_A1_RESULTS.md) remain historical. The recommendation here is one offline v3→v4 migration: **two narrow tables plus one execution-row provenance-profile discriminator**, no generic queue, event store, worker, IAM or new agent loop. The discriminator is necessary to distinguish migrated unverified rows from a missing mandatory receipt on a new execution; it is not proof of provenance or authorization.

## 1. Verified seams and design decisions

Read the B0/B-C/A0/A1 documents, [ADR-004](../architecture/ADR-004_DURABLE_EXECUTION_RECOVERY.md), [ADR-005](../architecture/ADR-005_POLICY_APPROVAL_CANCELLATION.md), [ADR-006](../architecture/ADR-006_TRUSTED_BROKER_BOUNDARY.md), [guardrails](../architecture/ARCHITECTURE_GUARDRAILS.md), [Roadmap](../ROADMAP.md) and [execution plan](PHASE2B2_EXECUTION_PLAN.md). Concentrated source verification, without repeating the B0 investigation:

| Existing source / function | Required narrow change in the next increment |
|---|---|
| [Runtime SQL](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.Runtime.cs), VerifyEffectTransactionAsync | Create intent transaction must also persist the bound receipt placeholder; return response-registration state through a narrow coordinator, not reconstruct it from a later read |
| [Protected runtime](../../src/MAF.Workflows.External/ProtectedExternalRuntime.cs), IntentStore / BeforeWireAsync | Same protocol for Allow and consumed-approval Resume; arm one request, bind its actual response, recheck all new effects after cancellation |
| [Durable service](../../src/MAF.Workflows.External/DurableExecutionService.cs), PersistingClient.CreateAsync / SaveAsync | Replace protected null→known generic update with atomic receipt finalization; hand the committed snapshot back to PersistingClient so it does not save twice |
| [OpenCode adapter](../../src/MAF.Workflows.External/OpenCode/OpenCodeClient.cs), CreateCoreAsync / SendAsync / VerifySession | Current SendAsync returns only JSON, losing transport provenance. Preserve a scoped actual request/response association through validation; no public arbitrary-string registration |
| [Execution store](../../src/MAF.Persistence.Sqlite/SqliteExecutionStore.cs), UpdateAsync | ExplicitV4 rejects any ordinary null→known session update, even with an owner/valid phase. Only receipt transaction can make that change |
| [Cancellation read](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.Cancellation.cs), ReadCancellation | Separate historical authorization validation from current-session correlation through the finalized exact receipt; retain every non-session join/digest check |
| [Migration](../../src/MAF.Persistence.Sqlite/ApprovalSchema.V3.cs), ValidateOldData / OldBytes / manifest | Do not reuse its v2-specific rejection of kind=3 or its old byte projection that omits cancel_request_id. v4 must validate/preserve all v3 cancellation/audit columns and rows |
| [Policy contracts](../../src/MAF.Core/Policy/PolicyContracts.cs), [OperationBinding](../../src/MAF.Core/Policy/OperationBinding.cs) | No existing enum value/name/encoding changes. Receipt correlation reuses original Submit assessment and consumed evidence; future Abort is a separate additive operation |
| [B0 tests](../../tests/MAF.Tests/CancellationSessionEvolutionTests.cs) | Replace both expected legitimate read failures with successful evidence/control reads and fresh-process GET recovery; retain forged-session/identity negatives |

OpenCode presently preserves a syntactically valid session ID in an exception handle when directory/permissions verification fails. v4 must preserve that diagnostic identity **as quarantined provenance**, not promote it to a verified receipt or discard it to make tests green. The exact protocol below distinguishes this case from an absent/invalid Create response.

Decisions recommended: one receipt per lifetime task/execution; pending→verified OR pending→quarantined exactly once; immutable original intent; no replayable registration token; one reserved Abort attempt per cancellation; independent delivery handle; strict legacy no-backfill. The original DurableExecution state enum, snapshot format, fingerprint, OperationBinding v1, approval CAS/consumption and explicit policy activation semantics remain unchanged.

## 2. Exact proposed additive DDL

The following is the **complete proposed schema delta**, not executed SQL. Existing v3 definitions (including all cancellation triggers and audit codes 0–7) remain verbatim. Run only in the separately authorized exclusive migration transaction after exact source validation. All new tables require SQLite STRICT support; implementation tests must verify the actual bundled runtime supports it and stop otherwise, without dropping constraints as fallback. INTEGER fields are signed 64-bit; UTC ticks mean .NET UTC ticks, not Unix/local time. IDs use the existing metadata ASCII grammar in the writer/parser; SQL additionally bounds their UTF-8 bytes. Hash fields are uppercase SHA256, not signatures.

```sql
ALTER TABLE executions ADD COLUMN session_receipt_required INTEGER NOT NULL DEFAULT 0
    CHECK(session_receipt_required IN (0,1));

CREATE TRIGGER executions_v4_new_profile
BEFORE INSERT ON executions
WHEN NEW.session_receipt_required <> 1
BEGIN
    SELECT RAISE(ABORT, 'ReceiptProfileRequired');
END;

CREATE TRIGGER executions_v4_profile_immutable
BEFORE UPDATE OF session_receipt_required ON executions
WHEN NEW.session_receipt_required IS NOT OLD.session_receipt_required
BEGIN
    SELECT RAISE(ABORT, 'ReceiptProfileImmutable');
END;

CREATE TABLE execution_session_receipts (
    receipt_id TEXT PRIMARY KEY NOT NULL
        CHECK(length(CAST(receipt_id AS BLOB)) BETWEEN 1 AND 160),
    task_id TEXT NOT NULL UNIQUE
        REFERENCES executions(task_id) ON DELETE RESTRICT,
    execution_id TEXT NOT NULL UNIQUE
        CHECK(length(CAST(execution_id AS BLOB)) BETWEEN 1 AND 160),
    operation_id TEXT NOT NULL UNIQUE
        REFERENCES authorization_operations(operation_id) ON DELETE RESTRICT,
    approval_id TEXT REFERENCES approval_requests(approval_id) ON DELETE RESTRICT,
    provider_id TEXT NOT NULL
        CHECK(length(CAST(provider_id AS BLOB)) BETWEEN 1 AND 160),
    provider_scope TEXT NOT NULL CHECK(length(provider_scope)=64
        AND provider_scope NOT GLOB '*[^0-9A-F]*'),
    intent_execution_version INTEGER NOT NULL
        CHECK(intent_execution_version BETWEEN 1 AND 9223372036854775806),
    intent_control_version INTEGER NOT NULL CHECK(intent_control_version>=0),
    intent_audit_sequence INTEGER NOT NULL UNIQUE
        REFERENCES control_audit(sequence) ON DELETE RESTRICT,
    intent_at_ticks INTEGER NOT NULL
        CHECK(intent_at_ticks BETWEEN 0 AND 3155378975999999999),
    format_version INTEGER NOT NULL CHECK(format_version=1),
    binding_version INTEGER NOT NULL CHECK(binding_version=1),
    intent_digest TEXT NOT NULL CHECK(length(intent_digest)=64
        AND intent_digest NOT GLOB '*[^0-9A-F]*'),
    intent_json TEXT NOT NULL
        CHECK(length(CAST(intent_json AS BLOB)) BETWEEN 1 AND 16384),
    version INTEGER NOT NULL CHECK(version IN (0,1)),
    status INTEGER NOT NULL CHECK(status IN (0,1,2)),
    provider_session_id TEXT CHECK(provider_session_id IS NULL OR
        length(CAST(provider_session_id AS BLOB)) BETWEEN 1 AND 160),
    response_digest TEXT CHECK(response_digest IS NULL OR
        (length(response_digest)=64 AND response_digest NOT GLOB '*[^0-9A-F]*')),
    response_json TEXT CHECK(response_json IS NULL OR
        length(CAST(response_json AS BLOB)) BETWEEN 1 AND 2048),
    finalized_at_ticks INTEGER CHECK(finalized_at_ticks IS NULL OR
        finalized_at_ticks BETWEEN 0 AND 3155378975999999999),
    response_audit_sequence INTEGER UNIQUE
        REFERENCES control_audit(sequence) ON DELETE RESTRICT,
    CHECK((status=0 AND version=0 AND provider_session_id IS NULL
        AND response_digest IS NULL AND response_json IS NULL
        AND finalized_at_ticks IS NULL AND response_audit_sequence IS NULL)
        OR (status IN (1,2) AND version=1 AND provider_session_id IS NOT NULL
        AND response_digest IS NOT NULL AND response_json IS NOT NULL
        AND finalized_at_ticks IS NOT NULL AND finalized_at_ticks>=intent_at_ticks
        AND response_audit_sequence IS NOT NULL
        AND response_audit_sequence>intent_audit_sequence))
) STRICT;

CREATE UNIQUE INDEX one_original_provider_session
    ON execution_session_receipts(provider_id,provider_scope,provider_session_id)
    WHERE provider_session_id IS NOT NULL;

CREATE TRIGGER session_receipt_profile_required
BEFORE INSERT ON execution_session_receipts
WHEN NEW.status<>0 OR NEW.version<>0 OR NOT EXISTS(SELECT 1 FROM executions
    WHERE task_id=NEW.task_id AND session_receipt_required=1)
BEGIN
    SELECT RAISE(ABORT, 'ReceiptInitialStateOrProfile');
END;

CREATE TRIGGER session_receipt_finalize_once
BEFORE UPDATE ON execution_session_receipts
WHEN NOT (OLD.status=0 AND OLD.version=0 AND NEW.status IN (1,2)
    AND NEW.version=1
    AND (NEW.receipt_id,NEW.task_id,NEW.execution_id,NEW.operation_id,
         NEW.approval_id,NEW.provider_id,NEW.provider_scope,
         NEW.intent_execution_version,NEW.intent_control_version,
         NEW.intent_audit_sequence,NEW.intent_at_ticks,NEW.format_version,
         NEW.binding_version,NEW.intent_digest,NEW.intent_json)
    IS (OLD.receipt_id,OLD.task_id,OLD.execution_id,OLD.operation_id,
        OLD.approval_id,OLD.provider_id,OLD.provider_scope,
        OLD.intent_execution_version,OLD.intent_control_version,
        OLD.intent_audit_sequence,OLD.intent_at_ticks,OLD.format_version,
        OLD.binding_version,OLD.intent_digest,OLD.intent_json))
BEGIN
    SELECT RAISE(ABORT, 'SessionReceiptImmutable');
END;

CREATE TRIGGER session_receipt_no_delete
BEFORE DELETE ON execution_session_receipts
BEGIN
    SELECT RAISE(ABORT, 'SessionReceiptImmutable');
END;

CREATE TABLE cancellation_deliveries (
    delivery_id TEXT PRIMARY KEY NOT NULL
        CHECK(length(CAST(delivery_id AS BLOB)) BETWEEN 1 AND 160),
    cancel_request_id TEXT NOT NULL UNIQUE
        REFERENCES cancellation_requests(cancel_request_id) ON DELETE RESTRICT,
    receipt_id TEXT NOT NULL UNIQUE
        REFERENCES execution_session_receipts(receipt_id) ON DELETE RESTRICT,
    task_id TEXT NOT NULL UNIQUE
        REFERENCES task_controls(task_id) ON DELETE RESTRICT,
    execution_id TEXT NOT NULL
        CHECK(length(CAST(execution_id AS BLOB)) BETWEEN 1 AND 160),
    abort_operation_id TEXT NOT NULL UNIQUE
        CHECK(length(CAST(abort_operation_id AS BLOB)) BETWEEN 1 AND 160),
    captured_control_version INTEGER NOT NULL CHECK(captured_control_version>=1),
    receipt_digest TEXT NOT NULL CHECK(length(receipt_digest)=64
        AND receipt_digest NOT GLOB '*[^0-9A-F]*'),
    intent_audit_sequence INTEGER NOT NULL UNIQUE
        REFERENCES control_audit(sequence) ON DELETE RESTRICT,
    intent_at_ticks INTEGER NOT NULL
        CHECK(intent_at_ticks BETWEEN 0 AND 3155378975999999999),
    budget_ticks INTEGER NOT NULL CHECK(budget_ticks BETWEEN 1 AND 50000000),
    format_version INTEGER NOT NULL CHECK(format_version=1),
    binding_version INTEGER NOT NULL CHECK(binding_version=1),
    intent_digest TEXT NOT NULL CHECK(length(intent_digest)=64
        AND intent_digest NOT GLOB '*[^0-9A-F]*'),
    intent_json TEXT NOT NULL
        CHECK(length(CAST(intent_json AS BLOB)) BETWEEN 1 AND 16384),
    version INTEGER NOT NULL CHECK(version BETWEEN 0 AND 9223372036854775806),
    delivery_stage INTEGER NOT NULL CHECK(delivery_stage IN (0,1,2,3)),
    acknowledgement_at_ticks INTEGER CHECK(acknowledgement_at_ticks IS NULL OR
        acknowledgement_at_ticks BETWEEN 0 AND 3155378975999999999),
    remote_outcome INTEGER NOT NULL CHECK(remote_outcome IN (0,1,2,3,4)),
    last_observed_at_ticks INTEGER CHECK(last_observed_at_ticks IS NULL OR
        last_observed_at_ticks BETWEEN 0 AND 3155378975999999999),
    observation_digest TEXT CHECK(observation_digest IS NULL OR
        (length(observation_digest)=64 AND observation_digest NOT GLOB '*[^0-9A-F]*')),
    observation_json TEXT CHECK(observation_json IS NULL OR
        length(CAST(observation_json AS BLOB)) BETWEEN 1 AND 8192),
    last_audit_sequence INTEGER NOT NULL
        REFERENCES control_audit(sequence) ON DELETE RESTRICT,
    CHECK((delivery_stage=2 AND acknowledgement_at_ticks IS NOT NULL
        AND acknowledgement_at_ticks>=intent_at_ticks)
        OR (delivery_stage<>2 AND acknowledgement_at_ticks IS NULL)),
    CHECK((version=0 AND delivery_stage=0 AND remote_outcome=0
        AND last_observed_at_ticks IS NULL AND observation_digest IS NULL
        AND observation_json IS NULL AND last_audit_sequence=intent_audit_sequence)
        OR (version>0 AND last_observed_at_ticks IS NOT NULL
        AND last_observed_at_ticks>=intent_at_ticks AND observation_digest IS NOT NULL
        AND observation_json IS NOT NULL AND last_audit_sequence>intent_audit_sequence))
) STRICT;

CREATE TRIGGER delivery_requires_verified_receipt
BEFORE INSERT ON cancellation_deliveries
WHEN NEW.version<>0 OR NEW.delivery_stage<>0 OR NEW.remote_outcome<>0
    OR NOT EXISTS(SELECT 1 FROM execution_session_receipts r
    JOIN cancellation_requests c ON c.cancel_request_id=NEW.cancel_request_id
    WHERE r.receipt_id=NEW.receipt_id AND r.status=1 AND r.version=1
      AND r.task_id=NEW.task_id AND r.execution_id=NEW.execution_id
      AND c.task_id=NEW.task_id AND c.execution_id=NEW.execution_id
      AND c.committed_control_version=NEW.captured_control_version)
BEGIN
    SELECT RAISE(ABORT, 'DeliveryReceiptMismatch');
END;

CREATE TRIGGER delivery_cas_and_immutable_binding
BEFORE UPDATE ON cancellation_deliveries
WHEN NOT (NEW.version=OLD.version+1
    AND NEW.last_audit_sequence>OLD.last_audit_sequence
    AND NEW.last_observed_at_ticks>=coalesce(OLD.last_observed_at_ticks,OLD.intent_at_ticks)
    AND (NEW.delivery_id,NEW.cancel_request_id,NEW.receipt_id,NEW.task_id,
         NEW.execution_id,NEW.abort_operation_id,NEW.captured_control_version,
         NEW.receipt_digest,NEW.intent_audit_sequence,NEW.intent_at_ticks,
         NEW.budget_ticks,NEW.format_version,NEW.binding_version,NEW.intent_digest,NEW.intent_json)
    IS (OLD.delivery_id,OLD.cancel_request_id,OLD.receipt_id,OLD.task_id,
        OLD.execution_id,OLD.abort_operation_id,OLD.captured_control_version,
        OLD.receipt_digest,OLD.intent_audit_sequence,OLD.intent_at_ticks,
        OLD.budget_ticks,OLD.format_version,OLD.binding_version,OLD.intent_digest,OLD.intent_json)
    AND (NEW.delivery_stage=OLD.delivery_stage
        OR (OLD.delivery_stage=0 AND NEW.delivery_stage IN (1,2,3)))
    AND (OLD.delivery_stage<>2 OR NEW.acknowledgement_at_ticks IS OLD.acknowledgement_at_ticks)
    AND (OLD.remote_outcome=0 OR NEW.remote_outcome=OLD.remote_outcome))
BEGIN
    SELECT RAISE(ABORT, 'DeliveryVersionOrBindingConflict');
END;

CREATE TRIGGER delivery_no_delete
BEFORE DELETE ON cancellation_deliveries
BEGIN
    SELECT RAISE(ABORT, 'DeliveryEvidenceImmutable');
END;
```

Write `PRAGMA user_version=4` **last**, in the same transaction after manifest/data/foreign-key/integrity verification. No IF NOT EXISTS, OR REPLACE, schema repair, new audit columns/codes or changes to old rows. Inventory: one column, two tables, one explicit unique index, eight triggers (two execution-profile, three receipt, three delivery); UNIQUE/PK constraints supply other indexes. No list/retry/scheduler indexes. FK does not establish every cross-table equality; trusted read/write checks below are mandatory.

### SQL responsibilities versus coordinator responsibilities

SQL enforces types/bounds/basic state shapes, lifetime uniqueness, referenced rows, immutable bindings/profile, receipt single finalization and delivery version/stage monotonicity. It does **not** authenticate a caller/response, validate JSON/digests, enforce audit contents/capacity, validate an execution snapshot's embedded IDs, stop all arbitrary SQL writers, or atomically associate independently issued statements. These belong to the fixed coordinator and fail-closed read verification. No externally callable generic SQL/transaction API is introduced.

Keep existing `control_audit` schema and numeric meanings unchanged. Use existing Requested=0 for the receipt's original Create dispatch intent and for new component journal checkpoints, identified by the explicit FK reference plus component/version. Code0 already covers dispatch intent; it is not a new policy approval. Receipt finalization writes a **new** audit row linked by response_audit_sequence; delivery writes one per mutation linked by last_audit_sequence. No existing row is repurposed. Audit version equals intent execution version for receipt intent, receipt version=1 for finalization, delivery version for delivery checkpoints. Actor/task/optional original approval/time/epoch/version must match the corresponding closed evidence. Receipt response audit records the original historical dispatch actor/authority tuple, not whichever revision happens to be active at response arrival. Its meaning is historical provenance, never renewed authorization.

All checkpoint inserts count against the existing hard 10000 audit capacity; no pruning. Reserve/check necessary space inside each transaction, not by a race-prone count outside it. If receipt finalization cannot append its required audit, roll back receipt+snapshot and report Unknown/PersistenceUnavailable with no prompt. Response identity may be lost on crash, which is safer than fabricating evidence. Delivery last observation digest preserves the current observation; earlier audit checkpoints preserve actor/time/revision, not every historical provider observation/body. This deliberately bounded ledger is not complete event sourcing.

On every v4 trusted read/write, deserialize bounded DTOs, recompute domain hashes and compare all indexed fields; join task control, original kind1/generation1 admission, original consumed approval when present, execution immutable IDs/provider/scope, receipt and cancellation winner. Validate each referenced audit code=0, exact component version, actor/task/approval/authority tuple/time and strict sequence order. Receipt intent audit uses the existing original dispatch checkpoint, not admission's version0 audit. Response/delivery checkpoint sequences must not be borrowed from another component/receipt; reject cross-record reuse (except a delivery's initial last_audit_sequence intentionally equals its own intent sequence). For delivery, the cancellation audit is the original exact code6/committed control version and intent must follow it; control must still name the same winner. Audit deletion/tamper/digest mismatch denies, never creates a legacy classification. Historical authority/approval integrity is checked without falsely requiring expired historical Submit authorization to be current; every new external effect separately requires current rights.

## 3. Closed evidence and canonical bytes

All `*_json` are closed format-1 DTOs, not polymorphic/general payloads: exact property names, every field required (explicit null where allowed), reject unknown/duplicate fields, integer enum encodings, unknown names, invalid strict UTF-8/UTF-16, depth>8, unsupported format/binding versions and over-limit bytes **before parsing/allocation**. Intent max16KiB, response max2KiB, delivery observation max8KiB. Descriptor SupportedOperations has 1–3 distinct exact enum names, sorted ordinally after duplicate validation; no other arrays/dictionaries. IDs ASCII `[A-Za-z0-9_.:-]`, 1–160 bytes; actual OpenCode SessionId must additionally match its existing `ses_` grammar. All SHA fields uppercase on write, hashes recomputed on every read. Resource/descriptor text retains Core's existing 1024-character validation; no unbounded list/dictionary. Property ordering is irrelevant. JSON byte equality is used only by SQL to freeze an existing column, never as authorization/binding equality.

**Receipt intent DTO, fixed property order for canonical encoding:** `Operation` (complete existing PolicyRequest with session=null), `Capability` (complete existing submission descriptor), `ProviderId`, `ReceiptId`, `ApprovalId` (nullable), `ApprovalVersion` (nullable), `ApprovalBindingDigest` (nullable), `CallerAuthenticationContextId`, `PolicyDigest`, `PriorExecutionVersion`, `IntentExecutionVersion`, `IntentControlVersion`, `IntentAuditSequence`, `IntentAtTicks`, `RequestTimeoutTicks`, `ResponseLimitBytes`. First two use the existing typed encoding contract; approval triple all-null for direct Allow, all-present for the exact consumed approval. Indexed columns must equal the typed values. Operation is the original Submit, not a new privilege. RequestTimeoutTicks is the effective HTTP Create budget (1–60 seconds, capped by trusted task budget), ResponseLimitBytes uses current options bounds 1000–3000000. Body/credentials are never part of stored evidence. Bind MessageId before Create, as today.

**Receipt response DTO:** `ReceiptId`, `IntentDigest`, `ProviderSessionId`, `Verification` (`Verified` or `Quarantined`), `Diagnostic` (`None`, `SessionPolicyMismatch` or `InvalidSessionResponse`), `ObservedAtTicks`, `FinalizedAtTicks`, `ResponseAuditSequence`. Verification comes from the adapter path, never a request parameter. Verified requires Diagnostic=None; Quarantined requires one of the two failure codes. Require intent time<=observed time<=finalized time, each in UTC tick bounds; reject clock rollback rather than invent ordering. A quarantined receipt means the same original response yielded a syntactically valid ID but failed directory/permission shape validation; it does not certify safe provider permissions. Missing/invalid ID, invalid JSON, oversized body, lost response, wrong route/origin or foreign response cannot finalize even quarantine. Response payload contains no raw provider body or human proof.

**Delivery intent DTO:** `Operation`, `Capability` (future exact Abort operation/descriptor), `DeliveryId`, `CancelRequestId`, `CancellationBindingDigest`, `ReceiptId`, `ReceiptDigest`, `ProviderId`, `ProviderSessionId`, `CallerAuthenticationContextId`, `PolicyDigest`, `CapturedControlVersion`, `IntentAuditSequence`, `IntentAtTicks`, `BudgetTicks`. Full task/execution/run/message/fingerprint/scope/authority/caller/connector are in Operation. Request ID is unique only in this ledger, not inserted into authorization_operations: that table's generation/approval rules remain unchanged. Require global operation-ID noncollision in the trusted coordinator against auth operations and the delivery table; no generation replacement.

**Delivery observation DTO:** `DeliveryId`, `IntentDigest`, `ObserverOperation`, `ObserverCapability`, `ObserverAuthenticationContextId`, `ObserverPolicyDigest`, `Stage`, `RemoteOutcome`, `Diagnostic`, `ObservedAtTicks`, `AcknowledgementAtTicks` (nullable), `CorrelatedMessageId`, `TerminalEvidence` (nullable), `TerminalEvidenceDigest` (nullable), `AuditSequence`. Closed diagnostics: `None`, `ResponseLost`, `ProviderUnavailable`, `InvalidAcknowledgement`, `AuthorizationChanged`, `NoSubmissionEvidence`, `Running`, `PermissionPending`, `EvidenceInsufficient`, `TerminalEvidenceChanged`. Remote outcome only from independently authorized original-session GET evidence; a response-only acknowledgement writes outcome=None and terminal fields=null. Remote outcome!=None requires both terminal fields and matching original message. Stage names are `Intended`, `SentUncertain`, `Acknowledged`, `SuppressedBeforeWire`; outcomes are `None`, `Completed`, `Partial`, `Failed`, `Cancelled`, mapped to the SQL integers in §6.

**TerminalEvidence DTO:** `ProviderId`, `ProviderScope`, `SessionId`, `OriginalMessageId`, `ObservedMessageId`, `ParentMessageId`, `FinishReason`, `ErrorCode`, `Outcome`, `ObservedAtTicks`. Message/parent/finish/error may be null only where the existing adapter's terminal criterion permits; preserve its exact original-message/parent correlation requirement, never infer completion from idle. IDs use the 160-byte grammar; finish/error are bounded sanitized fixed adapter codes (ASCII alphanumeric/underscore, at most80), not exception strings. Outcome must match the nonzero ledger outcome. This nested typed metadata has domain `MAF.TerminalObservationBinding`, version1 and the field order just listed; use the same canonical primitives below and verify TerminalEvidenceDigest on reads. No response text/model output/transcript. Once terminal evidence commits, later observations preserve this original nested object/digest/time even if their outer observation time advances; contradictions are only diagnostics/Unknown.

New domain-separated canonical hashes are defined without changing Core OperationBinding v1: `ReceiptIntentBinding/1`, `ReceiptResponseBinding/1`, `ReceiptLink/1`, `CancellationDeliveryBinding/1`, `CancellationObservationBinding/1`, each prefixed with literal `MAF.` + domain. Encode version as signed big-endian Int64; strict UTF-8 text with unsigned big-endian byte length (`FFFFFFFF` null); nullable numbers use a 0/1 Int64 presence followed by value when present; fixed DTO field order as above. Complete Operation+Capability is encoded as a length-prefixed byte array of **existing** OperationBinding.Encode; nested TerminalEvidence uses a nullable length-prefixed canonical byte array. Hashes use uppercase text; exact enum names ordinal, never raw JSON. No locale/timezone/serialization ordering dependence. ReceiptLink encodes ReceiptId, intent_digest, response_digest in that order. Indexed receipt_digest on delivery is this ReceiptLink, not a hash of mutable execution metadata. Response/observation digest includes its assigned audit sequence, allocated before finalizing the payload in the same transaction. Parser rejects a mismatch instead of recalculating stored evidence to match arbitrary input.

## 4. Trusted response writer protocol

### Fixed responsibility boundaries

| Component | Responsibility / prohibited authority |
|---|---|
| Original execution owner | Live exact database/task/profile OS handle; serializes execution transitions until response capture/finalization ends. Not cancellation or delivery ownership |
| Protected adapter | Validates one actual HTTP request/response association and existing OpenCode ID/directory/permission schema. Does not choose Policy, modify cancellation, create human consent or run its own agent loop |
| Response-registration capability | Opaque, owner-bound, one request, single-use, no serialization/public constructor/string token. Holds original trusted context, immutable receipt/intent identity and armed transport association; not a Submit/Inspect/Abort grant |
| Narrow persistence coordinator | Checks durable evidence, snapshots, capability use and ownership; atomically finalizes receipt+snapshot+audit. No network, Workflow launch or general-purpose transaction callbacks |
| Immutable cancellation evidence | Historical exact Cancel authorization, canonical bytes/digest and current sticky control linkage; never rewritten to insert a later session |

Recommend one narrow `ISessionReceiptCoordinator` contract in Core (provider-neutral metadata, no HTTP/SQLite types) implemented by the existing SQLite coordinator, plus a sealed response-registration bridge in the protected runtime/adapter assembly. Conceptual operations are `CommitCreateIntent`, `ArmOriginalCreate`, `FinalizeOriginalCreate`; signatures must pass opaque capability/verified response, not public `Register(task, sessionString)` or caller-supplied snapshot. Receipt projections are read-only metadata. Arm uses the existing effect guard; Core does not inspect HTTP. The bridge binds the actual request instance and transport response to the capability; persistence verifies durable exact fields as well as the sealed response context. Reference checks alone, a session-shaped string, prior audit or a digest of untrusted data do not satisfy the protocol.

### Ordering and one-use lifecycle

1. **Intent:** actual owner, verified caller/trusted configuration, fresh active policy and current control; reuse Allow or exact consumed approval proof through the existing guard. In one short transaction validate Created, null session, required-profile=1, no receipt, control not cancelled, all original IDs; CAS execution to SessionCreating/SessionCreationIntended; append original intent audit; insert pending receipt with complete intent binding; validate all joins and commit. Generate opaque registration capability **only for the committing owner**; never generate one on an existing receipt read. Failure leaves no half receipt/intent.
2. **Arm:** before the single actual Create send, existing final pre-wire policy/control/ownership verification plus receipt binding. Unarmed or consumed/disposed contexts deny. Capture method POST, exact trusted origin, `/session` route and approved-root query, provider/adapter/config identity, the actual request instance and original prepared IDs. Require trusted transport configuration that rejects redirects/origin changes and custom untrusted handlers. Existing synthetic transport is test-owned. No new HTTP endpoint/authentication service. There is no atomic SQLite+HTTP guarantee: cancellation after the final preflight can race a send; document that window rather than claiming intent sequence proves wall-clock delivery order.
3. **Capture:** only that awaited SendAsync response may produce an opaque verified response. Validate response belongs to the original request (including final effective origin/method/route), success status, byte budget, strict JSON and existing session ID/directory/permissions rules. OpenCode need not echo MAF metadata: exact Task/Run/Execution/Message are correlated by the original request/context, not an invented provider signature/echo guarantee. Valid ID + policy rejection produces quarantine. Wrong transport/request/route, absent ID or lost response burns the capability with no receipt finalization. No retry/new Create.
4. **Finalize:** retain actual owner; consume capability atomically in memory once, then one short SQLite transaction. Compare complete current snapshot + version, require original immutable identity/provider/root/fingerprint; current session must be null, phase must be SessionCreationIntended, state SessionCreating or Unknown, and no terminal record. Re-read pending receipt and original admission/consumed approval binding; compare audit, intent digest/versions and actual response context. Current control may have advanced only through the exact validated cancellation winner; never erase it. Original policy digest/authority is historical evidence, not implicitly activated/refreshed. Receipt finalization is metadata capture of already-sent work and therefore does **not** require current Submit/approval freshness or an uncancelled control; it grants no effect. Validate cancellation structurally without recursively demanding current receipt finalization first.
5. In that same transaction allocate response audit, write closed response/digest, receipt version0→1/status Verified or Quarantined and execution CAS version+1/SessionKnown with exact returned ID. Verified uses ReadyToSubmit if before state SessionCreating, or Unknown if already Unknown; quarantined always Unknown with fixed diagnostic. Pass existing DurableExecutionTransitions.Validate. Validate receipt/execution/control/audit joins before commit. Return committed snapshot to PersistingClient, preventing a duplicate ordinary SaveAsync. Use a bounded five-second local persistence budget independent of Workflow CTS; retain owner until it ends. Cancellation does not kill this registration merely because its HTTP response arrived late. Audit/lock/DB failure rolls back completely and consumes the in-memory capability; no retry or reconstructed capability, Unknown/no-resubmit on recovery.
6. Dispose/burn capability at success, failure, timeout, invalid response or owner release. Original HTTP has its existing effective bounded request budget; registration has no restart lifetime. A still-live matching capability may recognize its own exact already-committed result after a local commit-response ambiguity as metadata only, without second mutation/audit. No public replay API or capability resurrection. New prompt always goes through current guard, and cancelled control denies it. Authority change may block further effects but does not falsify an already received response.

Release the intent authority lease before HTTP; the capability is not an authority lock held across a network request. Finalization needs its fixed historical-evidence transaction, not a call to existing GuardedTransaction that would demand the original revision remain current. It still requires actual live ownership, original sealed response context and all durable joins; unavailable current policy does not erase an already observed result. It cannot perform any provider effect or implicitly pin/activate a revision.

Under controlled trusted composition this proves that the recorded session came through the configured adapter's one original Create response path, with durable identity/intent ordering and an atomic snapshot receipt. It does not prove provider honesty, remote cryptographic identity, absence of provider-side mutation, human action, malicious same-account isolation, exactly-once HTTP or power-loss safety. Trusted adapter/writer/transport compromise can lie. M3-C must supply production isolation, custody/authenticity/anti-rollback; hashes, C# visibility, SID and localhost alone do not do so.

## 5. Protected transition/read rules and cancellation ordering

ExplicitV4 ordinary UpdateAsync validates the immutable profile and existing identities as well as the old state machine; **every** null→known session change is rejected with `SessionReceiptRequired`, even if a caller holds an owner, supplies a matching session-shaped string, knows all receipt metadata, or points at a finalized receipt. Only the fixed finalization transaction may set it. Any ordinary update on a required-profile row at phase>=SessionCreationIntended requires an exact receipt; phase>=SessionKnown requires finalized matching session/digest. Created/phase=None must have no receipt. A missing/bad receipt is corruption, never legacy. Protected creation-intent writes must go through CommitCreateIntent; no raw UpdateAsync fallback. Before wire, missing receipt denies. Legacy/profile0 cannot enter new dispatch phases. Known→different session always denied in every profile.

Both Allow and Resume use that same bridge after their existing distinct admission/consume paths. Resume retains original RequireApproval assessment and consumed evidence at each new provider effect; receipt finalization is not approval renewal. No generic bool IsApproved. Do not alter old v1/v2/v3 profiles/behavior or original canonical bytes to add v4.

Cancellation validators split **historical evidence validity** (unchanged canonical binding/actor/authority/control/audit/IDs) from **current execution provenance** (v4 receipt). For null historical session and later known current session, require exact finalized receipt (Verified or Quarantined for structural evidence reads), original binding/provider/task/execution/run/message/fingerprint/scope equality, and original Create intent audit sequence strictly less than CancellationRequested audit sequence. Null is not sufficient. Quarantined evidence keeps control readable but does not permit provider inspection/Abort. Known historical session must equal current and finalized receipt session. Original cancellation RequestCancel replay semantics stay exact: do not reconstruct a new operation with changed session and call it the old operation; no evidence/digest rewrite, second winner or expiry refresh. A replay whose current operation no longer equals original binding may still conflict; GetCancellation/GetControl can succeed without granting replay or any effect.

Ordering outcomes:

| Durable order / event | Required result |
|---|---|
| Cancel before Create intent/pre-wire check | No authorized Create or receipt finalization; zero Create/Prompt/Abort |
| Intent commits, then Cancel before pre-wire | Pending receipt remains, arm denied; Unknown/no session, no POST/restart retry |
| Final pre-wire completes, send races Cancel | Delivery may already occur; only actual matching response may finalize, no promise that Cancel revoked in-flight HTTP |
| Original Create response in flight, Cancel commits, then response finalizes | Immutable cancel session=null; current original session saved; GetControl/GetCancellation positive; Prompt/Abort zero; independently allowed verified GET possible |
| Response finalizes, then Cancel commits | Cancellation captures known original session, both joins remain exact |
| Lost response or crash before finalization commit | Pending receipt/no known session; recovery Unknown, no Create/Submit/rebuilt capability |
| Crash after finalization commit | Finalized receipt and same session survive together; current independent Inspect may GET, never Create/Submit |
| Response invalid/quarantined | Original diagnostic session preserved if syntactically valid; no new effect, Unknown; control/evidence metadata remain readable |

## 6. Future delivery state and authorization contract (not B1b dispatch)

Existing cancellation row means Requested only. `delivery_stage`: 0=Intended, 1=SentUncertain, 2=Acknowledged, 3=SuppressedBeforeWire. No separate durable Sent fact is claimed: intent already means may have sent. Exactly one 0→1/2/3 transition; later independently authorized GET observations may advance version and retain stage, never reopen it. Suppressed means final preflight denied while live code knows no send occurred; it still consumes the one reservation. A restarted Intended row cannot be relabelled Suppressed based on guesswork; treat uncertain, GET-only. Late acknowledgement after stage1 is conservatively not promoted to Ack; record diagnostic observation, do not reopen/retry. These conservative availability choices avoid another attempt generation.

Acknowledgement capture is historical response metadata bound to the original live delivery context, even if rights change while that POST is in flight; it creates no fresh effect authority. Its ObserverOperation/Capability are the originally authorized Abort pair. A GET observation instead carries its own freshly authorized Inspect pair/current policy digest; a suppressed-before-wire checkpoint records the attempted Abort pair and fixed denial diagnostic without claiming a new Allow. Validate the corresponding actor/authority/audit fields by checkpoint kind. Storage of a response is distinct from permission to make another request.

Separate `remote_outcome`: 0=None/Unconfirmed, 1=Completed, 2=Partial, 3=Failed, 4=Cancelled. Ack writes outcome0. Only original-session/message GET with existing adapter terminal criteria may set 1–4. Once nonzero it cannot change; contradictory later evidence yields TerminalEvidenceChanged/Unknown and preserves original outcome/durable terminal execution. Same terminal outcome later observations must preserve the original TerminalEvidenceDigest (coordinator check). A genuine Completed race stays Completed, not Cancelled. Idle, empty session, CTS, local intent, HTTP true or missing messages are insufficient terminal proof.

Future append-only enum additions: `PolicyOperation.AbortExistingExecution=8` (existing 0–7 untouched), `PolicyCapability.ExecutionAbort=4` (existing 0–3 untouched), TaskAdmission boundary retained. Existing ProviderEffect.Abort numeric value already exists but remains denied until separately authorized B2. Existing binding encoder algorithm/domain/version and enum names stay unchanged; old operations' bytes remain byte-identical. B2 must extend supported-operation/capability validation and exact rules explicitly; unrecognized descriptors/op8 before that deny by default. B1b does not enable the evaluator/guard for Abort merely because v4 has storage.

Abort request uses original task/execution/run/message/fingerprint/provider/session, current independently authorized subject/context, exact receipt/cancel/link, trusted cancellation-delivery scope and budget (1 tick–5 seconds) through the new domain-separated delivery binding. Submit/Inspect/RequestCancelExecution rights, Approved/Consumed rows and receipt status are never substitutes for current Abort Allow. RequireApproval for Abort is NotSupported in this controlled design; no implicit approval generation/CLI. Authority acquisition/identity/proof fetch occurs outside SQL; local bounded checks inside. Current rights/active epoch+version/digest/freshness/config/connector/read-only session/control are checked during intent and final pre-wire. No automatic policy activation or caller-supplied authority.

Delivery coordinator uses its own actual OS handle keyed by exact database path + CancelRequestId, not execution ownership. It may read target internally and write only the delivery ledger under exact Cancel/receipt/control joins. It never steals the running TaskId handle or modifies execution state; execution reconciliation writes still require the execution owner. In one short transaction insert unique delivery intent + bounded audit, then return one-use in-memory dispatch continuation only to the committing delivery owner. Missing/invalid context denies raw Abort routes. POST only original `/session/{sessionID}/abort`, under independent trusted budget/CTS; observation timeout/shutdown never invokes it. No network in transaction.

SQLite is not atomic with HTTP. Intent then crash can reserve an attempt that sends nothing; after restart never retry, even if transport seems to have failed before connect. No exactly-once claim. Response lost, unavailable/unsupported provider and absent acknowledgement retain Requested/uncertain, never imply stopped. Ack write is CAS/audit only. Reconciliation under current independent Inspect authorization rechecks active policy, identity, scope/config, original receipt/session and control on every protected wire; does not need Submit rights, consume approval again or dispatch Abort. Future B2/C tests must cover this without live providers.

## 7. Legacy/profile compatibility and explicit migration

| Data / profile | Information/GET strategy | New session/Abort eligibility |
|---|---|---|
| Exact v1 with LegacyV1; exact v2/v3 with own explicit profile | Unchanged historical behavior/limitations | No new v4 permission or trust |
| Any old profile opens v4 | Reject version, never fallback | None |
| ExplicitV4 new required-profile1 Created | Normal admission metadata; receipt required from intent onward | Original armed Create only; no recreated continuation |
| ExplicitV4 new verified receipt | Current independently authorized exact-session GET, including after Cancel/restart | Future Abort requires fresh independent rights and reservation; no resubmit |
| ExplicitV4 new quarantined receipt | Structural control/cancel reads; explicit quarantine metadata | Provider effects denied; no promotion/repair |
| Migrated profile0, known session, no cancel or exact historical known-session cancel | `LegacySessionUnverified` projection. Separately authorized legacy Inspect is supportable using unchanged exact stored identities/scope and existing session policy validation, GET only | Never receipt/delivery eligible, no Create/Submit/Resume |
| Migrated profile0, cancel captured null but snapshot later known (B0 fault) | Expose historical/control metadata only after structural and current Inspect authorization, explicitly `LegacyCorrelationUnverified`; no provider GET because original-session relationship cannot be proven | No repair/backfill/Abort |
| Migrated profile0, null session / Created / preintent crash | Metadata/Unknown only; no provider GET | No new dispatch from that historical execution |
| Required-profile1 missing/invalid receipt at intent/known phase | Corruption/fail-closed, never downgrade to legacy | None |

Legacy metadata projection is an explicitly authorized application operation, not permission to weaken the shared trusted evidence validator or expose raw rows. It can report immutable historical evidence validity separately from unverified current-session correlation; do not return a misleading fully verified cancellation result for the mismatched old row. Existing strict APIs may keep rejecting that case; B1b need not add a broad metadata listing/UI. Legacy read-only support never writes a receipt, changes profile0→1 or restores authorization. An old valid session does not prove human approval or permit delivery. No automatic legacy graduation/import API; future remediation needs separate review.

Migration is explicit `MigrateV3ToV4Async` via the existing protected exact-resource maintenance seam, with maintenance/all-old-Hosts-stopped/backup-and-rollback confirmation. Reuse actual exclusive schema+task locks; define delivery lock directory with the same fixed lock ordering (schema maintenance gate, then sorted task/delivery handles, then SQLite EXCLUSIVE transaction). Normal delivery never acquires a task handle; no process holds task then waits for delivery, avoiding circular lock order. Connection is existing local ReadWrite only, Pooling=false, foreign_keys=ON before the transaction, synchronous=FULL, bounded five-second busy timeout, exclusive locking/transaction as in existing maintenance. All Hosts must actually stop; boolean attestations alone are not proof. No production authorizer/CLI/Host profile routing is supplied.

Validate exact v3 manifest, integrity/FKs, every v3 cancellation operation/evidence/control/audit join and structurally valid snapshot before DDL. The known B0 null→known temporal mismatch is allowed **only as preserved legacy-unverified data** if every non-session identity/digest/audit matches; do not call it verified or accept other corruption. Existing v3 kind3 operations are expected. Capture **all seven v3 application tables plus sqlite_sequence**, all columns including control_audit.cancel_request_id, exact SQLite storage types/NULLs/BLOB bytes, original serialized snapshot text and audit sequence. Do not deserialize/reserialize old snapshots or use v3 migration's v2-only projection.

One transaction: add profile column (all old rows default0), create two empty tables/index/triggers, revalidate original value bytes and counts with the one intentional column addition excluded, exact expected v4 manifest, integrity/FKs, write user_version4 last, commit; then fresh connection under maintenance gate verifies all again. No new audit/authority rows or sqlite_sequence increments during migration. New tables are empty; no receipt/consent/delivery backfill. Strict ExplicitV4 admission explicitly writes profile1; INSERT triggers prevent default0 on future execution rows. Preserve actual existing v1→v2 and v2→v3 operations; no meaning changes or automatic chain migration.

Any exception/fault before commit rolls back the whole DDL/version change. On lost commit response, a fresh process may see only complete exact v3 or exact v4; do not repair/downgrade. Failed post-commit verification leaves maintenance blocked and evidence preserved. Document existing backup/rollback custody and logical-byte verification in the [maintenance procedure](../setup/SQLITE_APPROVAL_MAINTENANCE.md) during implementation. Restoring a pre-v4 backup after new writes can lose control/authority/receipts; no automatic restore, reauthorization or resubmission. Unknown/newer/partial/extra-object schemas reject.

## 8. Next implementation acceptance matrix

These are proposed test obligations, **not tests run in B1a**. Synthetic trust, disposable SQLite, fake HTTP only; real process termination where stated. Keep all historical security/recovery assertions. B1b must turn the two B0 legitimate-response failures into positive assertions, not add permanent expected-failure characterization or skip them.

| ID | Deterministic fixture / gate | Required assertion |
|---|---|---|
| R01 | Allow, real intent; fake Create request gated, independent Cancel, then genuine response | Receipt Verified + original session atomically; historical cancel JSON/digest unchanged; GetControl/GetCancellation succeed; Create1/Prompt0/Abort0 |
| R02 | Approved Resume same sequence | Same protocol/evidence; consumed approval unchanged, RequireApproval never fake Allow; same positive reads |
| R03 | Kill captured original owner after finalization commit; new process with current Inspect-only rights | Original Task/Execution/Run/Message/session retained, receipt verified, original GET only, no new Create/Submit/consume; honest Unknown for never-submitted task |
| R04 | Unarmed/forged/serialized/replayed registration; wrong owner/task/path/profile/transport/request/route/origin; raw session string | No receipt/snapshot mutation, no new effect; no fallback |
| R05 | Real earlier dispatch audit then lost response; phase-valid UpdateAsync injects unrelated ID | ExplicitV4 UpdateAsync denies; pending receipt remains; known session replacement and ID substitutions denied |
| R06 | Cancel wins before intent or before arm | No HTTP; no fabricated response; intent rollback or pending/Unknown; restart never retries |
| R07 | Response finalization versus Cancel ordered both ways | Both commits obey exact ordering, same original session/cancel winner; no deadlock on execution owner; immutable historical evidence |
| R08 | Invalid ID/JSON/body size/route, lost Create response and quarantined policy rejection | No forged Verified receipt; diagnostic identity retained only through quarantine; Unknown/no Submit/Abort |
| R09 | Kill real process after placeholder insert/execution write/audit before commit, after intent commit, after response receipt/snapshot writes before commit, after finalization commit | Whole old/new transaction only; same IDs/integrity; receipt and snapshot never split; restart no new capability/Create/Submit |
| R10 | Audit full, CAS stale, policy changes after send, owner disposed, finalization timeout/fault | Rollback atomically; historical response capture allowed after authority change only with genuine context; next effect denied; no privilege renewal |
| R11 | One-use capability concurrent finalize/replay and duplicate session/task/intent identities | At most one transaction/final audit; no replacement or unrelated receipt; source-guard context cannot be reconstructed |
| R12 | Populated v3 including B0 legacy mismatch, terminal/uncertain/approval variants, kind3, cancellation audit, legacy sticky intent | Exact original values/types/bytes/sequence preserved, profile0/new tables empty; no backfilled receipt, unverified never Abort eligible |
| R13 | Real migration kills after ALTER, each table/trigger group, version write before commit and after commit | Fresh process exact v3 or v4, old bytes/integrity/FKs preserved; no partial repair |
| R14 | Every profile/version cell, unknown schema/object/format/enum; default0/new flag tamper | Exact rejection; no >=version or legacy downgrade; no Host auto-migration |
| R15 | Uncancelled v4 full Allow and Resume, existing recovery, independent Inspect denial/expiry | Official Workflow unaffected; expected one original Create/Submit only; zero Abort; no plain/raw bypass |
| R16 | Data-only delivery foundation in B1b: test-owned synthetic SQL fixtures exercise duplicate reservations, missing receipt/FK, malformed binding, stale CAS and fault rollback | SQL/read/CAS invariants only, Abort POST0; no fake executable Abort authorization/coordinator. Actual authorized reservation concurrency belongs to B2 |
| R17 | Future B2/C, independent delivery owner while execution owner held; kill at intent/send/ack; lost ack; provider unavailable | Exact one reserved attempt, no automatic retry, honest uncertainty; no TaskId ownership starvation, original session only |
| R18 | Future B2/C, Ack-only versus running/pending/cancelled/completed; policy/control/Inspect revocation race | Ack never terminal; actual correlated terminal/immutable execution rules; current operation-specific rights; zero unauthorized Abort/Submit |

Capture actual PIDs/start times/checkpoints, original/recovered IDs, receipt/control/execution versions, audit sequence ordering, method/path POST counts, raw discovery/pass/fail/skip and build warnings/errors. Full offline baseline is 743, not an assumed constant after implementation. No live/provider/company/security-policy tests. No new anti-malicious-writer or power-loss claim from synthetic tests.

## 9. Exact B1b file boundary and quality assessment

Expected source files (names of new small partials/contracts are proposals, not files created now):

- Core: a narrow `Policy/SessionReceipt.cs` DTO/coordinator/capability-facing contract; reuse DurableExecution, IExecutionOwnership, PolicyRequest/descriptor, authority and consumed approval contracts. No new Workflow engine. Existing state/fingerprint/binding algorithms unchanged.
- Persistence: new `ApprovalSchema.V4.cs` / `ApprovalSchema.V4Ddl.cs`, explicit authorized maintenance entry; exact profiles in SqliteExecutionStore/SqliteApprovalStore; existing Admission/Resume inserts explicitly set profile1; Runtime partial plus new `SqliteApprovalStore.SessionReceipt.cs` for fixed transactions/read validation and `SqliteApprovalStore.DeliveryEvidence.cs` for provider-free foundation. Update Cancellation reads with strict receipt correlation, not wildcard. Extend exact ownership profile handling; old profiles remain intact.
- External workflow assembly: small sealed receipt bridge; ProtectedExternalRuntime / IntentStore, DurableExecutionService / PersistingClient and OpenCodeClient scoped response capture/finalization only. Reuse official Workflow/executor; protect raw/public entrypoints and preserve default-deny. No Host routing/DI/trust, Abort enablement or new harness loop.
- Tests: replace legitimate-response expectations in CancellationSessionEvolutionTests with R01/R02, preserve and strengthen its injection negatives; extend existing acceptance helper minimally for receipt/migration kill gates and authorized fresh-process Inspect. Add focused v4 schema/receipt/delivery-storage tests; rerun all historical A2c/B2/A1/offline tests/build. Synthetic trusted adapters remain test-only.
- Docs: new B1b results, accepted exact DDL/migration maintenance procedure, minimal latest Roadmap/plan status; no rewriting historical reports or corporate event files.

**B1b authorized boundary to request next:** implement only owner-accepted v4 offline migration/profiles, receipt writer/transition/read protocol, data-only delivery evidence and deterministic/fresh-process tests. Use explicitly test-owned synthetic SQL fixtures for delivery schema/read/CAS validation; do not add a fake-authorized production reservation API or mint a transport-usable Abort token. Actual authorization/intent reservation coordinator remains unavailable until B2. Do not implement op8 evaluator/runtime enablement, actual Abort, retry worker, M4-C runtime reconciliation feature, Broker/authentication, Host enablement, approval CLI or PCA→OpenCode→Reviewer workflow changes. Existing GET recovery is used to prove receipt closure, not a new general reconciliation engine.

Quality review: all seven guardrails preserved; harness still owns its tool loop, Workflow coordinates, deterministic policy guards effects and persistence only commits evidence. Existing authority/Policy/approval/ownership/CAS/audit contracts are reused. One narrow receipt coordinator/response bridge is justified by the missing atomic trusted-writer seam; no general extensibility platform. v4 covers the known receipt, original-session, one-attempt B/C acknowledgement/terminal metadata needs; retries/generation replacement/archive/distributed ownership and future production proof storage are explicitly out of scope and might require separately reviewed changes. Do not promise no future migration for new requirements.

## 10. Owner acceptance, remaining risks and validation status

Owner acceptance is still required for this **recommended exact contract**, not inferred from B1a documentation authorization:

1. Two tables plus immutable execution profile discriminator; no legacy receipt backfill. Legacy exact known-session GET may be independently authorized without upgrading provenance; legacy B0 mismatch stays metadata-only/unverified.
2. One-time Verified/Quarantined finalization, bounded historical response capture even after cancellation/policy change, and no automatic receipt recovery on failed finalization. Receipt never authorizes effects.
3. One lifetime Abort reservation, independent delivery ownership, conservative unretryable crash window, Suppressed/uncertain/ack distinction and terminal evidence rules; availability loss is accepted explicitly before delivery code.
4. Existing Requested audit checkpoints with FK/component interpretation and hard capacity, rather than changing old audit code ranges/tables. No full provider-observation history or pruning.
5. Proposed op8/capability4 added only during separately authorized B2, not by B1b migration. Protected production identity/writer/transport custody, genuine human proof where required, revocation and external anti-rollback remain unresolved M3-C blockers.

Security-critical implementation stop conditions: inability to retain the actual original response association through the adapter; an ordinary ExplicitV4 write that can set session without the receipt transaction; receipt/control validation recursion that would accept missing evidence; any proposed fallback from required profile to legacy; unbounded/network callbacks inside SQL; needing to weaken consumed approval or current effect authorization. Report the specific blocker, do not broaden privileges. A malicious same-principal writer remains outside the controlled proof, not solved by this schema.

B1a validation is source/contract/document review only. SQL was **not executed**, no DB migrated, runtime/test source changed, or provider contacted. Build/tests were not rerun for documentation-only changes: historical B0 evidence is 139 focused / 743 offline, zero failed/skipped and build zero warnings/errors at the same source baseline. Those numbers do not validate v4 DDL/triggers or the proposed protocol. Exact SQLite syntax/constraint/CAS/process-crash tests in R01–R18 are mandatory before implementation acceptance. Text-only inventory confirms two tables/one added column/one explicit unique index/eight triggers; all 82 relative file links across this contract, Roadmap and execution plan resolve and fenced blocks balance. Final staged diff/whitespace review passed and scope contains only those three documents; historical reports and pre-existing untracked `.vscode/` are excluded.

**STOP after M4-B1a. Await owner acceptance and separate B1b implementation authorization.**
