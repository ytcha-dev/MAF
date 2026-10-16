# Phase 2B-2 M4-A — Cancellation source review and storage gate

Date: 2026-10-16 (Asia/Taipei). Baseline: `7ac28578960ec3a9d4b338d011517b31b861d1f2`. Branch `main`, fetched `origin/main` equals HEAD. Tracked tree initially clean; pre-existing untracked `.vscode/` was not read, changed or staged.

**Result: Blocked at the schema/evidence design gate; M4-A is not implemented or accepted.** The owner explicitly requires stopping if the current schema cannot safely express cancellation semantics. Source review found no existing typed storage location for an independent, exact cancellation authorization binding. This delivery changes documentation only. No runtime, test assertions, schema, migration, production database or provider behavior was changed.

M4 synthetic engineering was authorized before M3-C as a sequencing choice. M3-C production identity, human proof, protected policy publication, writer isolation, revocation and anti-rollback remain mandatory and blocked. This report does not waive them or authorize M4-B/C.

## 1. Reviewed sources and current call graph

Reviewed [Roadmap](../ROADMAP.md), [execution plan](../plans/PHASE2B2_EXECUTION_PLAN.md), [M3 architecture gate](../plans/PHASE2B2_M3_ARCHITECTURE_GATE.md), [ADR-004](../architecture/ADR-004_DURABLE_EXECUTION_RECOVERY.md), [ADR-005](../architecture/ADR-005_POLICY_APPROVAL_CANCELLATION.md), [ADR-006](../architecture/ADR-006_TRUSTED_BROKER_BOUNDARY.md), [guardrails](../architecture/ARCHITECTURE_GUARDRAILS.md), [A2c](PHASE2B2_M3_A2C_RESULTS.md) and [B2](PHASE2B2_M3_B2_RESULTS.md), against the implementation below. Historical ADR/gate tables describe earlier baselines; current source controls this review.

| Component / function | Verified behavior and insertion point |
|---|---|
| [Core contracts](../../src/MAF.Core/Policy/ApprovalPersistence.cs), `IApprovalTransactionCoordinator.RequestCancellationAsync` | Takes ApprovalId, CancelRequestId, expected control version and caller. No independent cancel PolicyRequest/assessment or exact cancel binding parameter |
| [SqliteApprovalStore.RequestCancellationAsync](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.cs) | Authority lease → Candidate → GuardedTransaction / active revision check → load original approval → `CanControl(actor, r.Operation)` → read control → duplicate check or CAS → revoke Pending/Approved → bounded CancellationRequested audit → commit. No execution lock, execution creation, Workflow or HTTP |
| Same store, `GuardedTransaction`, `CompareActive`, `Audit` | Short SQLite IMMEDIATE transaction checks active authority/epoch/version/digest before/after action. Candidate acquisition is outside SQL; callbacks under lease are local. Explicit activation only. Audit exhaustion/fault rolls back the transaction |
| [TrustedPolicyAuthority.CanControl](../../src/MAF.Core/Policy/TrustedAuthority.cs) | Real adapter resolves verified identity, requires subject equals **supplied operation's caller**, exact current authority tuple and injected authorization. It does not independently evaluate a Cancel rule when given the original Submit operation. Missing adapters default to UnavailableTrust |
| [Policy contracts](../../src/MAF.Core/Policy/PolicyContracts.cs), [OperationBinding](../../src/MAF.Core/Policy/OperationBinding.cs) | RequestCancelExecution / ExecutionCancellation already exist, with deterministic exact-scope evaluation and canonical v1 binding. Reuse these; Submit/Inspect cannot substitute |
| [ExternalExecutionApplication.Assess](../../src/MAF.Workflows.External/ExternalExecutionApplication.cs), [ProtectedExternalRuntime.RunCoreAsync](../../src/MAF.Workflows.External/ProtectedExternalRuntime.cs) | No executable Cancel admission. Protected runtime accepts only Submit/Inspect. Future cancel application must be separate from RunCore's long-held owner acquisition |
| [SqliteExecutionStore.TryAcquireAsync](../../src/MAF.Persistence.Sqlite/SqliteExecutionStore.cs) | Same TaskId/path OS FileShare.None ownership spans execution. ExplicitV2 read/update is opt-in; legacy rejects v2. Cancellation must not wait on this lock |
| [AdmitAllowedAsync](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.Admission.cs) / [ConsumeOwnedAsync](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.Resume.cs) | Real execution owner → authority lease → writer transaction. Both verify current control/no cancellation before unique Created. Direct Allow has no approval snapshot; Resume consumes exact original approval and returns only owner-bound one-use handoff |
| [VerifyEffectTransactionAsync](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.Runtime.cs) | Actual owner + authority lease + active binding/decision/control/execution checks; intent CAS and audit in same writer transaction. Captured control version must match. Submit also requires no cancel. Abort is always EffectNotSupported |
| Protected runtime `ProtectedAccess.VerifyAsync` / `BeforeWireAsync` | Each effect and raw HTTP method/path revalidated. Changed control version blocks next effect of current run; a separately authorized fresh Inspect can use current control version. Recovery never grants Create/Submit. No automatic cancel delivery/poll worker |
| [DurableExecutionService.PersistingClient](../../src/MAF.Workflows.External/DurableExecutionService.cs) | Intent before Create/Submit, original-session recovery, terminal preservation. Its legacy AbortAsync saves AbortRequested before delegating. This is not the task-control intent transaction |
| [OpenCodeExecutor](../../src/MAF.Workflows.External/OpenCodeExecutor.cs), cancellation catch | Protected access SuppressImplicitAbort returns Unknown/ObservationTimedOut without Abort. Historical internal test access exercises old abort behavior; it is not new cancellation authorization |
| [OpenCodeClient.AbortAsync / SendAsync](../../src/MAF.Workflows.External/OpenCode/OpenCodeClient.cs) | Abort effect context before POST; protected context rejects it. Raw public construction defaults unavailable. Reattach marks original execution already submitted; cannot re-prompt |
| [DurableExecution](../../src/MAF.Core/DurableExecution.cs) / transitions | AbortRequested is sticky attempt metadata, not authorized cancel intent, acknowledgement or terminal proof. Identity/fingerprint/phase invariants and terminal immutability unchanged |
| [Host ExternalConsole](../../src/MAF.Host/ExternalConsole.cs) | ProductionTrustUnavailable before provider/store construction. No operational cancel CLI. Native PCA/Reviewer remain separate, unprotected by this external policy composition |

Existing cancellation primitive:

```text
trusted library caller (ApprovalId, CancelRequestId, expectedControlVersion, actor)
  -> authority AcquireAsync (outside SQLite)
  -> active-policy guarded IMMEDIATE transaction
     -> original approval / CanControl(original operation)
     -> task_controls CAS: version+1, sticky cancel ID/actor/time
     -> Pending/Approved -> Revoked (Consumed stays Consumed)
     -> bounded audit
  -> commit data only
running owner -> next intent/effect/wire preflight -> control mismatch -> deny
```

This already separates execution ownership from control serialization. The missing boundary is independent Cancel authentication/authorization and durable exact cancel evidence, not a second execution lock. No arbitrary external SQL writer is authorized by this design. In-process visibility and SQLite consistency do not isolate malicious same-principal writers.

## 2. Concrete schema blocker

[ApprovalSchema](../../src/MAF.Persistence.Sqlite/ApprovalSchema.cs) implements the reduced M2-A v2 schema, not every proposed column in ADR-005 §6. ADR-005 §11 explicitly records that cancellation_requests was omitted.

| Existing durable location | Missing cancellation evidence |
|---|---|
| task_controls(task_id, execution_id, version, cancel_id, actor, requested_at) | No cancel OperationId, canonical binding/version, expected input control version, resource/config/connector binding, authority identity or decision deadline |
| authorization_operations(operation_id, task_id, execution_id, kind, generation) | Index/identity metadata only; no PolicyRequest snapshot or binding digest. Inserting kind=Cancel does not retain the authorized scope |
| control_audit(code, actor, task_id, approval_id, version, time, epoch, policy_version) | No cancel ID/operation FK, scope or canonical digest; approval_id refers to original approval in current semantics. policy_authority stores only the latest digest, not policy history |
| approval_requests.snapshot | Full evidence exists for a RequireApproval operation, not independent direct-Allow cancellation. Original Submit approval does not authorize Cancel, and direct-Allow executions have no such row |
| executions.snapshot | Execution identity/root/provider/fingerprint only; no cancellation caller, descriptor/config/scope revision or cancel decision. Rewriting it for authorization would change the wrong lifecycle and conflict with terminal immutability |

The current duplicate branch accepts the same cancel ID and subject before checking expectedControlVersion; it cannot compare a cancel scope/operation/digest because these were never supplied or stored. Distinct cancel requests differing in configuration revision, scope revision or operation identity have no field in which to retain that distinction while preserving the current cancel ID/actor/time semantics. After restart, the latest active policy plus execution metadata cannot reconstruct the historical exact Cancel decision. The original approval snapshot is the wrong operation and may be absent.

This is a limitation of the **existing defined storage protocol**, not a claim that SQLite or TEXT cannot encode arbitrary bytes. Packing JSON/digests into actor/cancel_id, using approval_id as a new payload slot, treating an execution ErrorCode as a receipt, or silently extending the v2 layout would introduce an unreviewed durable protocol. A sidecar would lose same-transaction guarantees. Fabricating an Approved row would conflate the two lifecycles. None was implemented.

**Stop decision:** do not ship an application wrapper which looks independently authorized but leaves restart/idempotency evidence incomplete; do not upgrade schema within this authorization. Existing low-level RequestCancellationAsync remains a trusted library primitive with the above limitations, not an accepted M4-A ingress.

## 3. Proposed smallest next increment (requires owner approval)

Review a bounded, immutable cancellation-request evidence record in the same database, with an explicitly versioned, additive offline migration. Candidate fields: CancelRequestId PK, unique task/execution winner, cancellation OperationId, caller subject, complete bounded provider-neutral PolicyRequest + capability descriptor or equivalent reconstructible canonical fields, binding encoding version/digest, evaluated/expiry times, authority/policy digest/epoch/version, expected/resulting control versions and requested time. Validate SQL identity columns against typed evidence. Store metadata only, never input text, tokens, credentials or provider bodies. Final DDL/version/receipt format needs review; this report creates none.

No delivery/ack/retry state is needed in this first storage increment. Link bounded audit to cancellation evidence without reinterpreting original ApprovalId. Preserve original approval snapshot/binding and execution bytes; revoke only unconsumed approval status through existing CAS. Existing v2 legacy cancel rows must be explicitly classified **legacy intent / independent authorization not demonstrated**, not backfilled as verified evidence or upgraded into delivery grants.

Then implement one small provider-neutral cancellation application + fixed coordinator:

1. Resolve caller through trusted resolver before metadata disclosure. Trusted configuration supplies cancel descriptor/scope/authority; caller supplies target IDs, request ID and expected control version, not authority claims.
2. Acquire authority outside SQL; independently evaluate exact RequestCancelExecution / ExecutionCancellation. Only current Allow can commit; Deny/RequireApproval/unsupported fail closed until separately supported. Different authorized cancelling subject is evaluated as that subject, not as original submitter.
3. **No execution owner acquisition.** In one short writer transaction re-read authoritative execution or original authorization, exact target/resource/current control, active policy/digest and cancel evidence. Revalidate at both transaction ends; no network callback.
4. Exact replay under current verified caller/policy requires identical persisted evidence and original expected version, returns original result without another audit/version bump. Any differing ID/binding/actor/target/version conflicts. No winner replacement. Define replay after expiry/revocation as denied; status inspection is a separate authorization.
5. Atomic immutable cancel evidence + sticky control CAS + revoke Pending/Approved + required bounded audit. Roll back all on fault/capacity exhaustion. Never consume approval or create execution/continuation; no Workflow/provider dependency.

## 4. Required lifecycle and ordering (proposed, not executed M4-A behavior)

| State / race | Required behavior |
|---|---|
| Existing associated operation before execution | Exact pre-bound authorization target only; cancel commit increments control, revokes Pending/Approved, blocks later consume/admission. Unknown arbitrary TaskId must not create a tombstone without trusted target evidence |
| Direct-Allow Created/Running | Use persisted execution identity plus trusted exact scope, independent Cancel caller; no approval row required. Commit while original owner holds its TaskId lock |
| Approved, not Consumed | Cancel-first makes consume fail. Consume-first may create exactly one execution; later cancel changes control and prevents subsequent guard-authorizable effects |
| Consumed + Created | Keep Consumed, original approver evidence and Created identity. No new handoff, no Submit during cancel or restart |
| SessionCreationIntended / PromptIntended | Intent-first can remain committed. Cancel before last wire preflight blocks next wire call; cancel after preflight cannot revoke an already sent HTTP request |
| Unknown / SubmissionUncertain | Keep original session/message/run and uncertain truth; no replacement or prompt retry |
| Terminal | Proposed explicit result AlreadyTerminal, no new intent/version/audit mutation. Do not overwrite Completed/Failed/Partial/Cancelled or infer Cancelled. Review this API choice before implementation |
| Policy activation race | SQLite writer order determines active tuple; newer active revision first rejects stale cancel. Cancel first remains historical authorized intent, not perpetual Abort permission |
| Transaction/crash | Precommit fault rolls back evidence/control/revocations/audit together; committed intent survives process death without reconstructing a dispatch ticket. M4-B needs fresh delivery authorization |

Admission/dispatch holds execution ownership then authority lease then writer transaction. Cancel holds authority lease then writer transaction, **never waits for execution ownership**, so no inverse dependency on a long-running owner. SQLite busy timeout remains bounded; failure must be explicit, not lock-file deletion. Owner-side existing guard sees changed control before next protected effect; this is not prompt remote cancellation or proof the provider stopped.

Only an explicit independently authorized command may add intent. Caller CancellationToken, observation budget, provider timeout, Host shutdown/Ctrl+C, crash and policy activation/revocation do not imply Cancel authority. Existing protected suppression of implicit Abort remains. A future notification/owner CTS must not be required for committing intent.

## 5. Actual verification

Documentation-only gate review; **0 new M4-A tests and 0 implementation changes**. Existing focused regressions rerun: **259 executed / 259 passed / 0 failed / 0 skipped**. Classes: ApprovalPersistenceTests 51, TrustedAuthorityTests 67, AtomicAdmissionTests 60, RuntimeEnforcementTests 37, ApprovalResumeTests 44. Full offline: **604 executed / 604 passed / 0 failed / 0 skipped**, unchanged from B2. Focused is a subset of full. Solution build: **0 warnings / 0 errors**, .NET 10 Debug. No restore/package changes required.

```powershell
dotnet build MAF.slnx --no-restore
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~ApprovalPersistenceTests|FullyQualifiedName~TrustedAuthorityTests|FullyQualifiedName~AtomicAdmissionTests|FullyQualifiedName~RuntimeEnforcementTests|FullyQualifiedName~ApprovalResumeTests' --logger 'trx;LogFileName=phase2b2-m4-a-review-focused.trx' --results-directory TestResults/home-validation
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName!~MAF.Tests.OpenCodeLiveTests' --logger 'trx;LogFileName=phase2b2-m4-a-review-offline.trx' --results-directory TestResults/home-validation
```

Ignored TRX files contain actual results; no historical result files were rewritten. These tests use synthetic trust, test-owned temporary databases and fake transport; no live OpenCode/model, company endpoint or personal credential was used. Existing fixture migration tests operate only disposable databases, not a real application database. No endpoint-security alert was observed; no ACL/security-policy changes. Corporate event records remain untouched.

Reviewed the final documentation diff; `git diff --check` passed. All 77 relative file links across this report, Roadmap and execution plan resolve. Only these three documents are included in the commit; pre-existing `.vscode/` and ignored test outputs are excluded.

Selected **existing regression** evidence from this focused run (UTC start times captured by tests; these are not new M4-A cancellation crash tests):

| Case | Actual evidence |
|---|---|
| Old cancellation/consumption race | admitted=True, controlVersion=1, cancelId=cancel, executionRows=1; provider dispatch not implemented by this storage test. It does not prove independent Cancel authorization |
| Atomic admission competing processes | Owner PID 55072, contender 34200, reader 56444; unique Created/None/v0; original/recovered task-admit/run-admit/execution-admit/message-admit; POST=0, integrity=ok |
| Resume consumed commit crash | Killed PID 45896, start `2026-10-16T11:51:44.2055891Z`, restart 47560; ConsumptionCommitted, original runtime-task/runtime-execution/runtime-run/msg_runtime, restart POST=0, integrity=ok |
| Runtime intent commit crash | Killed PID 55436, start `2026-10-16T11:51:45.8810634Z`, restart 22936; SessionCreationIntended, session=null, Create/Prompt/Abort=0/0/0, restart POST delta=0, integrity=ok |
| Prompt intent commit crash | Killed PID 12024, start `2026-10-16T11:51:46.3657985Z`, restart 50728; PromptIntended, ses_runtime, original runtime IDs; Create/Prompt/Abort=1/0/0, restart POST delta=0, integrity=ok |
| Resume owner competition | PIDs 36072/436, winner 36072; unique Created, Consumed v2, authorized baseline Create/Prompt/Abort=1/1/0 |

The full regression intentionally includes successful historical fake submissions. Aggregate POST activity is **not zero**. No new M4-A cancellation operation exists to measure; its Create/Submit/Abort=0 acceptance is **Not Demonstrated**, not inferred from passing old tests.

## 6. M4-A requirement traceability and remaining gates

| Requested cases | Status at stop |
|---|---|
| 1–8: independently authorized exact cancel, identities/permissions, freshness/activation, CAS/replay/conflict | Not Demonstrated for M4-A; contracts/evaluator and old storage primitives exist, but no new cancel ingress/evidence protocol |
| 9–11: cancel vs new admission / consume / dispatch | Existing control/consumption/runtime regression passes; independent M4-A cancellation and running-owner cross-process coordination still unimplemented |
| 12: terminal preserved | Existing immutable terminal transition tests pass; proposed AlreadyTerminal cancellation behavior not implemented |
| 13: atomic failure rollback | Existing CancellationWritten rollback test passes for old primitive; new cancellation evidence rollback unimplemented |
| 14: committed cancel survives crash | Dedicated independently authorized M4-A process/crash test not added or run; historical execution/approval crash tests are not substitutes |
| 15–16: no replacement / cancellation POST=0 | Existing no-resubmit and protected Abort-denial regressions pass; new M4-A cancellation method/path accounting not demonstrated |
| 17–18: A2c / B2 / durable recovery regression | Passed in 604-case full offline suite; no assertions weakened |

Remaining reachable gaps: public trusted library RequestCancellationAsync can mutate control based on original approval CanControl rather than an independent Cancel policy; it cannot cancel a direct-Allow execution without an approval, and its replay does not compare exact cancel binding. Store read/maintenance/activation primitives are not authenticated ingress. Malicious same-principal DB/DI/binary writers and native PCA/Reviewer remain outside protected-composition guarantees. No new access path was exposed by this review.

**Recommended next narrow owner decision:** authorize the cancellation-evidence storage design and explicit offline additive migration scope, including legacy-intent compatibility and exact idempotency contract. After that, complete M4-A independent admission + running-owner/process/race tests. Do not proceed directly to Abort delivery.

M4-B separately requires fresh operation-specific Abort authorization, exclusive delivery coordination, durable intent/evidence and bounded transport accounting; saved cancel intent alone must not be a dispatch grant. M4-C separately requires original-session restart reconciliation and delivery/ack/remote terminal evidence distinctions. M3-C remains blocked on genuine trust and protected deployment; no production activation, fake human fallback, Broker or cancel CLI was enabled. Seven architecture guardrails and corporate clearance boundaries remain unchanged.

**Stop at M4-A schema gate. No M4-B/C, M3-C, M5 or multi-agent workflow implementation.**
