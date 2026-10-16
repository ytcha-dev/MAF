# Phase 2B-2 — M3-0 Architecture & Trust Gate

> Date: 2026-10-16 (Asia/Taipei)  
> Status: **M3-0 source review delivered; owner decisions and M3 runtime authorization pending.**
> Base: `8d6ae99569d06edae46b6e5a6dc132a997a3d32b` · [M2-B evidence](../acceptance/PHASE2B2_M2B_RESULTS.md) · [Execution plan](PHASE2B2_EXECUTION_PLAN.md) · [Acceptance matrix](../acceptance/PHASE2B2_ACCEPTANCE_PLAN.md) · [ADR-005](../architecture/ADR-005_POLICY_APPROVAL_CANCELLATION.md) · [ADR-006](../architecture/ADR-006_TRUSTED_BROKER_BOUNDARY.md).

## 1. Purpose and stop conditions

M3 integrates previously isolated deterministic policy, approval/control storage and existing durable execution **without** replacing the official MAF Workflow, reimplementing a harness loop or relaxing the M2-B security boundary. The authorized M3-0 source review is recorded in [M3-0 results and implementation proposal](../acceptance/PHASE2B2_M3_0_REVIEW.md), against `f37e222`. No runtime work was performed; the proposed M3-A substeps require separate owner authorization.

Source clarification from that review: normal recovery does not Create/Submit, but executor cancellation can still call Abort. Public execution/client APIs have no unified MAF policy gate, Consume has no owner continuation, and the execution store rejects v2. The target invariants below do not describe completed enforcement. Explicit-only activation, compatibility changes and approval-generation limitations remain owner decisions.

The following remain disabled: production policy activation, human approval, v2 production Host path, trusted operator CLI, permission/question reply, write/shell, and any unsafe fallback. Schema v2 is a test/storage foundation, not a declaration that real DB migration/credentials/writer isolation are approved. The corporate endpoint execution hold is independent.

## 2. Confirmed source-level seams at M2-B

| Seam | Verified current behavior | Question for M3-0 |
|---|---|---|
| `src/MAF.Host/ExternalConsole.cs` | `--external-task` routes to ephemeral `ExternalWorkflow.RunAsync` if no DB, else `DurableExecutionService.RunAsync`; `--recover` and `--recover-pending` use durable | One application admission policy for every Host mode? Cannot treat missing DB as authorized fallback |
| `src/MAF.Workflows.External/DurableExecutionService.cs` | New TaskId creates `DurableExecutionState.Created` before `ExecuteAsync`; existing TaskId uses original recovery identity | Introduce an authorized **new-admission-only** transition without allowing existing rows to resubmit |
| `src/MAF.Workflows.External/OpenCodeExecutor.cs` and provider client | Validates read-only provider; Create/Submit/Observe/Abort and recovery path follow distinct semantics | Check current control/policy before each external effect; avoid business-approval logic embedded in provider JSON adapter |
| `src/MAF.Persistence.Sqlite/SqliteApprovalStore.cs` | `RequestAsync`, `DecideAsync`, `ConsumeAsync`, cancellation intent; `ConsumeAsync` atomically consumes Approved and inserts `Created` | Reuse exact CAS and IDs while preserving a single TaskId execution owner and no HTTP in transaction |
| `src/MAF.Core/Policy/TrustedAuthority.cs` | Lease-based candidate pin; identity/auth contracts with default unavailable trust | Define actual trusted source/identity deployment, or keep new real approval blocked |
| `SqliteApprovalStore.PinAuthority` / `ActivatePolicyAsync` | Pin during ordinary mutation may advance durable authority high-water; explicit activation method also exists | Decide **opportunistic** vs **explicit** activation semantics before integration |

Do not rely on this table alone: read the complete call graph and identify other direct library, future resume, observe, cancel/abort, background worker, and test-only entrypoints. Confirm actual API accessibility and all reachable provider POST paths before declaring gate closed.

## 3. Target boundary / invariants (not implemented)

```text
Host/CLI / future authorized clients
    -> authenticated request/operation context
    -> protected application admission (current policy, caller, connector, scope, binding)
    -> [Deny | RequireApproval + Pending commit | Allow + durable admission]
    -> durable execution owner + official MAF Workflow
    -> side-effect preflight -> harness adapter (provider-owned tool loop)
```

- **Application admission** owns operation-level decisions. Authentication/provenance and exact approval rights are independently supplied by replaceable trusted adapters, eventually a protected Broker (ADR-006). Keep deterministic policy evaluation in Core.
- **Persistence coordinator** owns admission/approval/control atomicity and authoritative durable activation order. `Approved` does not dispatch; `Consume` and insert of unique `Created` commit atomically. Never send HTTP inside a transaction.
- **Workflow** owns typed transitions/handoffs; it does not mint authentication, approval, policy versions or provider permission replies.
- **Side-effect guard** owns provider-facing checks for Create session, Submit prompt, and authorized Abort. Inspect/Get requires its own operation scope; recovery is GET-only on the original identity and never creates another prompt.
- **Harness** owns internal tool permissions. MAF approval does not imply OpenCode tool permission approval or OS-level sandboxing. Lack of trustworthy same-account isolation leaves production approval disabled.
- **Cancellation** records intent before observing/sending abort; acknowledgement is not terminal. M4 implements full delivery/reconciliation, not M3-A.
- **Checkpoints** of official MAF Workflow and the local provider side-effect ledger solve different problems. Evaluate coexistence, but do not replace the 2B-1 no-resubmit record with graph checkpoint replay.

## 4. Phase split, authority and exit gates

| Increment | Allowed work (requires separate owner authorization after M3-0) | Exit conditions |
|---|---|---|
| **M3-0** — architecture gate | Read source, produce call graph and bypass threat table, canonical data/lock ownership, activation and dispatch decisions, scoped API proposal, test mapping; **docs only** | Owner can review every side effect and blocking trust assumption. No runtime change or build/live execution required |
| **M3-A** — isolated policy admission | Synthetic local trusted fixtures + provider-free/fake-provider integration; default-deny protected entrypoints and explicit Allow/RequireApproval logic; negative tests for ephemeral/direct/recovery paths | All applicable P01–P12/S/R negative gates; no provider POST on Deny/RequiredApproval; no production fallback |
| **M3-B** — isolated approval/resume | Reuse M2-A transaction, stable IDs/input digest, exact approval CAS, explicit Resume, process-crash/uncertain-post fixtures | Applicable A01–A19; Approve POST=0, at most one new execution, recovery same session/no re-prompt; no production identity claim |
| **M3-C** — production trust & CLI activation | *Only after* authenticated authority/publisher, human presence/proof, protected writer and rollback/revocation design are demonstrated; minimal operator CLI and runtime gates | Independent real-identity, scope, audit, rollback, revocation, negative-bypass and process evidence; owner deployment acceptance. **Blocked at M2-B** |
| **M4/M5** — cancel + final acceptance | Durable cancel delivery/ack/terminal reconciliation, controlled process/live evidence, release review | Existing K01–K18, full regression, scoped interop/deployment gates separately accepted |

M3-A/B may establish test-environment correctness while M3-C is blocked. They **must not** create an undocumented production Allow path. M3-B is not authorization to build an operational approval CLI.

## 5. Hard unresolved decisions — M3-0 must recommend, not silently implement

1. **Activation:** should *only explicit authorized* `ActivatePolicyAsync` advance effective DB revision, or may a trusted protected mutation activate the newest candidate? `PinAuthority` currently allows the latter. Specify the source publication / local commit boundary, multi-instance order, failure and revocation semantics; no stale policy fallback.
2. **Identity / consent:** select viable distinct operator principal or Broker-protected human-presence flow, source authenticity and credentials lifecycle. Shared same-account SID, model text, `--actor` and synthetic context are insufficient.
3. **Dispatch race:** define admission-vs-policy/cancel linearization, preflight behavior, and the unavoidable commit-to-HTTP window. Deny prior to admission/dispatch intent; for previously authorized/in-flight effects use cancellation and reconciliation, never assert instantaneous rollback or remote exactly-once.
4. **Existing execution ownership:** decide which API can create a first execution and how an in-memory, non-serializable continuation is obtained only by its committing owner. After crash, original row is recovery-only even if POST was never seen; do not introduce force-resubmit.
5. **Scope / input re-entry:** input is not persisted; after restart explicit resume needs re-supplied original input and exact fingerprint. Define prompt disclosure/CLI history constraints and input-required UX.
6. **Trusted DB maintenance and anti-rollback:** maintain schema v2/current audit bounds; no automatic v1→v2 on startup, destructive prune, backup reversal, or agent-writable authority source.
7. **Check boundary completeness:** list library/Host/ephemeral/durable/recovery/batch/direct/poll/abort paths and trust proof for each, not only a UI or CLI check.

## 6. Required M3-0 deliverables

1. A **source-linked call graph** with function/file references and every reachable external Create/Submit/Abort path, plus a bypass finding table with severity, evidence and remediation seam.
2. A **component responsibility and dependency map**, not a new generic framework. For each proposed interface, state whether it is already implemented, needs an additive change or remains conceptual.
3. A **transaction/ownership/crash-window sequence** for Allow, RequireApproval, Approve (no dispatch), explicit Resume, consume+Created, recovery, and cancel races.
4. A short **decision log** covering the seven unresolved items above and protected Broker replacement contracts. Mark undecided matters as blockers.
5. An **acceptance traceability matrix** using existing `P/A/K/S/R` identifiers and actual code/test evidence; distinguish Planned / Implemented / Executed / Accepted and Blocked.
6. An incremental implementation plan with explicit M3-A/B/C scopes, migration compatibility, safety invariant preservation, and **owner approval required before coding**.

Deliver M3-0 as a separate source-grounded design/decision document under `docs/`. Avoid rewriting historical evidence. Review link consistency; commit/push **documentation only**, then stop. No service installation, ACL edits, endpoint workarounds, package upgrades, DB migration, provider POST, live model, or company data copying.

## 7. Release / evidence boundary

**M2-B evidence:** 94/94 focused, 347/347 offline as reported by the owner at `8d6ae99`; these are not independently rerun M3 results. No trusted production policy publisher, operator proof or isolated writer has been demonstrated. Mark production approval/activation, Broker deployment and M3 runtime as **Not Demonstrated / Blocked**, not as enabled. The seven [guardrails](../architecture/ARCHITECTURE_GUARDRAILS.md) stay unchanged.
