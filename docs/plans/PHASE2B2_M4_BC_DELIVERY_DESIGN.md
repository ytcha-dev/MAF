# M4-B/C — Session correlation, Abort delivery and reconciliation design gate

2026-10-17 (Asia/Taipei), baseline `b00e15cd717ab3be38c2ab69045c173e6d7e4aa7`. **Proposal for owner review; no storage/runtime implementation or migration authorization.** [B0 results](../acceptance/PHASE2B2_M4_B0_RESULTS.md) reproduce a v3 temporal correlation blocker. M3-C remains blocked. The seven [guardrails](../architecture/ARCHITECTURE_GUARDRAILS.md) and [Broker boundary](../architecture/ADR-006_TRUSTED_BROKER_BOUNDARY.md) remain binding.

## 1. Storage decision and missing proof

Keep immutable cancellation authorization separate from current execution state and from authority to send Abort. A historical null session is a precise statement that the session was unknown at authorization, never a wildcard for future sessions. A previously known session cannot change. Task/execution/run/message/fingerprint/provider/scope must remain exact.

Current v3 stores only the latest execution snapshot, an immutable Cancel binding and coarse audit rows. The dispatch audit records task, actor, version, policy tuple and optional approval, but not the dispatched effect, exact pre-dispatch binding, request/response correlation or returned session. Its sequence can prove that an intent preceded cancellation; it cannot prove which session belongs to that intent. Timestamps, phase values, OS ownership and an execution title alone cannot fill this gap. A valid earlier intent plus a phase-valid later owned update can introduce an unrelated session after a lost response. C# visibility is not same-principal security isolation.

**Recommend one explicit offline v3→v4 extension covering session correlation and B/C delivery evidence together**, with exactly two purpose-specific bounded records, not a general event store. Do not mutate the accepted v3 definition, overload Approval, fabricate an Approved row, or reuse AbortRequested as this ledger. Do not ship a delivery-only migration and discover the missing receipt afterward.

The field/transaction contract below is the proposed minimum. **Executable DDL is deliberately withheld at this gate:** receipt provenance and migration handling of old session records need owner acceptance first. This is a storage requirements proposal, not a finalized/validated schema. A subsequent reviewed schema specification must translate these constraints into exact SQL, indexes/triggers and profile manifests before implementation. No SQL from this document was executed.

## 2. Narrow session-correlation receipt

Proposed `execution_session_receipts`: one lifetime row per TaskId, unique ExecutionId and intent ID. Foreign-key linkage to the existing task control, original admission operation and intent audit sequence; optional consumed approval reference for Resume. Required immutable fields:

- Intent ID; TaskId / ExecutionId / WorkflowRunId / MessageId; provider/connector/adapter identity and provider scope; original fingerprint and exact scope digest.
- Session-create effect and exact canonical operation/descriptor binding digest, authority ID, epoch/version/policy digest, caller context reference, control version, pre-intent execution version and committed intent version/audit sequence.
- Receipt format/version, bounded canonical metadata, intent time and receipt CAS version. No prompt, response body, credential, transcript, raw exception or generic payload.
- Initially no session; exactly one permitted finalization sets original returned session, response-observed time and response correlation digest, with a receipt audit reference. No replacement or deletion. Session uniqueness must be `(provider scope, session ID)`, not global across providers.

The source of proof is a constrained trusted writer protocol, not hashing an arbitrary session string. The protected Create dispatch transaction must insert the receipt intent and execution intent/audit atomically, under the actual execution owner and current Submit/consumed approval evidence. Before HTTP, capture an owner-bound, non-serializable, one-use response-registration capability for **this** request. The adapter returns the response through that capability; callers cannot construct a session receipt from persisted metadata or ordinary `UpdateAsync`. Sealing/visibility alone does not protect a malicious same-account process; production protected writer/authenticity remains M3-C work.

Response finalization atomically validates original intent binding, owner, current snapshot/version and unchanged immutable identity; sets the session receipt and execution SessionKnown snapshot in one transaction, plus mandatory bounded audit. Cancel may already be committed. This special action only records the result of an already-sent request: it grants no Create/Submit/Inspect/Abort and does not ignore cancellation at the next effect. No network inside the transaction. Preserve immutable cancellation bytes/digest and current sticky control.

Cancellation reads with historical session null may accept a later current session **only** through this finalized exact receipt and an intent ordered before the cancellation audit. Known historical sessions must still equal the receipt/current session. All other identity/scope/joins remain mandatory. A mismatched/missing receipt remains fail-closed. Session preservation and permission to GET it are separate decisions.

Crash before receipt finalization leaves Unknown and no repeat Create/Submit; an in-memory registration capability is never rebuilt. Crash after finalization allows current independently authorized GET recovery of that same session. An HTTP response arriving after cancellation is legitimate metadata evolution; an invented response after restart is not. The final implementation must intercept all protected session-setting writes, not merely add a receipt-writing happy path alongside unrestricted protected updates.

## 3. One Abort attempt record for B and C

Proposed `cancellation_deliveries`: unique CancelRequestId (FK to immutable cancellation_requests), unique DeliveryId and task/execution association, FK to finalized session receipt. Lifetime uniqueness deliberately allows **at most one Abort attempt per cancellation**, not exactly-once delivery or an automatically retryable queue. Owner must accept the availability trade-off; replacement attempts/generations need separate authorization/design.

Immutable fields: delivery ID, CancelRequestId, exact original session receipt ID/digest, task/execution/run/message IDs, provider/connector/scope, new exact Abort operation ID/canonical binding, current caller/context, authority tuple/policy digest, captured control version, bounded budget, intent timestamp and audit sequence. Mutable CAS-controlled fields: ledger version; delivery stage; acknowledgement time/result code; bounded reconciliation time/evidence classification/digest/audit reference. Retain terminal evidence summary, not provider content. Freeze all bindings after insert; prevent deletion, unknown stages and terminal evidence replacement.

State semantics:

| Evidence | Meaning | Restart behavior |
|---|---|---|
| Requested (existing cancellation row) | Authorized sticky stop intent; no delivery grant | Recheck current authorization before any new effect |
| DeliveryIntended | One attempt reserved durably before POST | May already have been sent; GET-only, never replay POST |
| SentUncertain | Send may have reached provider; response absent/invalid/unavailable | GET original session under current Inspect rights |
| Acknowledged | Valid Abort HTTP acknowledgement saved | Still nonterminal; inspect original session |
| Remote terminal evidence | Correlated Cancelled/Completed/Failed/Partial evidence | Preserve actual existing execution transition/terminal immutability rules |

The crash window after intent commit and before sending is deliberately indistinguishable from a lost response. Conservatively mark it uncertain and never reconstruct a send continuation. A persisted `Sent` bit cannot remove the HTTP/SQLite gap. A transport failure is not proof that nothing was delivered; no retry even for an apparent pre-connect failure without a separately proven provider idempotency contract.

## 4. Ownership, current authorization and dispatch ordering

Cancellation admission keeps its independent short control transaction; it never waits for execution ownership. Delivery gets a **separate narrow OS ownership handle keyed by database + CancelRequestId**, analogous to existing local handle lifetime, not a lease that survives process death and not a second execution owner. It cannot create or change execution snapshots. A single delivery CAS plus uniqueness prevents two coordinating processes from committing attempts. Do not hold SQLite transactions across HTTP or wait for the long-running execution owner to request cancellation/send authorized Abort.

Use a narrowly distinct provider Abort authorization from `RequestCancelExecution`, Submit and Inspect. Recommend a later additive `AbortExistingExecution` operation/capability plus a versioned delivery binding that includes the existing canonical operation digest, CancelRequestId, delivery ID, exact receipt/session, control version and budget. Preserve existing OperationBinding v1 and Phase 2B-1 fingerprint bytes. Unknown/unsupported capabilities deny by default; policy publication is not activation. A capability descriptor is not itself a grant. Current resolver/source/active revision/rights/freshness and provider read-only session constraints must all validate.

Ordering for the later implementation:

1. Resolve current trusted identity/config outside SQL, acquire delivery handle; load exact historical cancellation and finalized original-session receipt internally without disclosure on denial.
2. Acquire current trusted authority lease outside SQL. In one short transaction revalidate durable active tuple, exact Abort binding/decision, current control/cancel ID, receipt, execution identity and nonterminal status; insert unique delivery intent plus mandatory audit. No implicit activation or approval consumption.
3. Issue a one-use, owner-bound in-memory delivery continuation only to the committing process. Before the single POST, revalidate current rights, tuple, control, original session and intent. A changed authority/control denies dispatch; an already-reserved attempt remains non-replayable.
4. POST only the exact original session `/session/{sessionID}/abort` under an independent bounded budget. Record valid acknowledgement or fixed uncertain code with CAS/audit. An HTTP request already in flight cannot be revoked by a later SQLite write.

No cancel-row bearer ticket, reconstructed continuation, token cancellation, observation timeout or shutdown callback may authorize Abort. If provider Abort is unsupported/unavailable, retain requested/nonterminal evidence and report NotSupported/Unknown with zero unauthorized POST. Delivery budget comes from trusted config (proposed maximum five seconds), independent of the running task/Inspect CTS; timeout cannot recursively trigger Abort. No background retries/scheduler in B1.

## 5. Reconciliation without execution-owner theft

The delivery coordinator writes only its ledger. The existing execution owner, or a fresh owner after restart, reconciles the original session through the protected GET path under **current Inspect** authorization and exact scope/config/session. Submit permission is not needed and does not imply Inspect; cancellation proof alone is insufficient. After restart recheck identity validity, active authority revision, current operation-specific policy, control, receipt and original provider/session constraints. Unavailable trust denies the corresponding new effect, without forging a remote outcome.

GET evidence must be correlated to original session/message and satisfy the adapter's existing terminal criteria. Abort HTTP success, idle session, task timeout, process exit, local intent or lack of further messages is never proof of Cancelled. A genuine completed answer racing Abort stays Completed. Existing terminal immutability wins over later contradictory evidence; report that observation as Unknown and preserve the durable terminal record. Nonterminal provider running/pending permission remains running/pending, with stop intent visible. No permission/question reply is introduced.

If no prompt was ever submitted, the newly created empty session is not evidence that a task was cancelled; keep honest Unknown/no-submission metadata. GET after cancellation must not rely on fresh Submit rights or consume approval again. Lost session identity, outage, missing/contradictory provider evidence or deleted messages yield Unknown; do not create a replacement session or re-prompt.

## 6. Migration, backward compatibility and acceptance gate

Proposed explicit offline v3→v4 only: protected exact-resource maintenance authorization, all old Hosts stopped, coordinated schema/task/delivery locks, backup/rollback custody, exact known v3 manifest/data/integrity/FK validation, exclusive atomic DDL/version write, byte-preservation of all original snapshots/rows/audit sequences, fault injection and fresh-process verification. Unknown/newer/partial schemas reject. No Host auto-migration, audit pruning, downgrade or silent repair. New ExplicitV4 profiles must be exact; legacy v1 and ExplicitV2/V3 continue rejecting v4.

Old v3 snapshots and null-session cancellations lack receipts; **do not manufacture provenance during migration**. Preserve old rows unchanged and report correlation unverified; no delivery from ambiguous data. Even old known-session rows need an explicitly accepted legacy reconciliation rule before v4 delivery. Migration does not enable execution or trust. User-visible limitation and owner acceptance are prerequisites; retaining v3 reads cannot be advertised as repaired v4 recovery.

Before code, owner decisions required: receipt writer/proof contract; old-row no-backfill behavior; one-attempt lifetime uniqueness and lost-send availability trade-off; separate Abort capability/binding; exact offline v4 schema/profile contract and bounded audit participation. Final DDL needs explicit length/UTF-8 limits, NULL/state combinations, uniqueness/FKs, immutability triggers, closed canonical format versions, same-transaction audit linkage and no bypassing raw protected session updates. SQL constraints alone cannot authenticate receipts.

First separately authorized **M4-B1** should implement only the accepted receipt + combined v4 storage/migration/profile foundation and provider-free transactional tests. Reproduce both B0 failures as positive successful evidence/control reads and independent GET recovery, while rejecting injected sessions with and without a genuine earlier intent. Do not enable Abort until those tests pass. Subsequent bounded B delivery and C reconciliation/crash acceptance can reuse the same ledger without another planned migration.

Required future crash tests: before/after intent and response-receipt commit, cancellation in-flight in Allow/Resume, kill before/after Abort intent and HTTP response, lost acknowledgement, competing delivery processes, original owner held, authority/control race at each commit, terminal completion race, Inspect denial/expiry and provider outage. Account raw methods/paths; zero replacement Create/Submit and zero unauthorized Abort. No implementation or process guarantee for this proposed schema has been demonstrated in B0.
