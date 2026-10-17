# M4-C — Authorized original-session reconciliation

2026-10-17 (Asia/Taipei). Baseline `81f0f3f4ab5388bca227b972b790ef9c7c3d74d5`. Controlled synthetic trust, disposable explicit-v4 SQLite and fake OpenCode HTTP only. This is not production activation, new live-provider evidence, power-loss proof or corporate endpoint clearance. Historical reports remain unchanged.

Owner clarified the attachment's transaction wording: **「允許短交易內的本機 lease，網路取得與 GET 仍在交易外」**. The existing protected authority therefore remains unchanged: acquire/verify its source outside SQL, retain only the local frozen-candidate lease during the short transaction, release before HTTP. No cached Allow or authority fallback is introduced.

## Implementation and source review

Reviewed the [B2 evidence](PHASE2B2_M4_B2_RESULTS.md), [B1b evidence](PHASE2B2_M4_B1B_RESULTS.md), [accepted v4 contract](../plans/PHASE2B2_M4_B1A_V4_SCHEMA_CONTRACT.md), [B/C design](../plans/PHASE2B2_M4_BC_DELIVERY_DESIGN.md), [guardrails](../architecture/ARCHITECTURE_GUARDRAILS.md), ADRs [004](../architecture/ADR-004_DURABLE_EXECUTION_RECOVERY.md)/[005](../architecture/ADR-005_POLICY_APPROVAL_CANCELLATION.md)/[006](../architecture/ADR-006_TRUSTED_BROKER_BOUNDARY.md), Roadmap/execution plan and actual adapter/runtime/stores/state-transition/DDL/test seams. There is no v5/schema/trigger/migration change and no redesign of earlier admission/approval/receipt milestones.

| Changed component | Responsibility |
|---|---|
| [Core reconciliation](../../src/MAF.Core/Policy/Reconciliation.cs), [observation](../../src/MAF.Core/ExternalContracts.cs) | Narrow inputs/result/coordinator and issuer-only session; internal provider-neutral terminal projection. Public JSON/input cannot supply terminal proof |
| [OpenCodeClient](../../src/MAF.Workflows.External/OpenCode/OpenCodeClient.cs) | Same ObserveCoreAsync, ReadMessagesAsync, ReattachAsync and terminal branches; project bounded metadata from the same parsed assistant message. No second parser or evidence GET. Projection is enabled only for this internal reconciliation composition; normal clients/observation JSON remain unchanged |
| [Application](../../src/MAF.Workflows.External/CancellationReconciliationApplication.cs) | Explicit one-shot current-caller authentication, exact configuration, actual execution ownership, existing ProtectedAccess and GET-only Reattach/Observe. Does not invoke DurableExecutionService's independent state writer or launch a Workflow |
| [SQLite reconciliation](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.Reconciliation.cs) | Current exact Inspect authorization, verified original receipt/cancellation/delivery, execution/control/delivery CAS, canonical metadata and joint terminal transaction |
| [Abort continuation](../../src/MAF.Core/Policy/AbortDelivery.cs), [delivery owner](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.AbortDelivery.cs), [dispatch](../../src/MAF.Workflows.External/OpenCode/ProtectedAbortDelivery.cs) | Narrow B2 owner-use lifetime fix; no extra reservation, retry or TaskId lock |
| [Synthetic reconciliation](../../tests/MAF.ApprovalAcceptance/SyntheticReconciliation.cs), [original fixture](../../tests/MAF.ApprovalAcceptance/SyntheticAbort.cs), [process helper](../../tests/MAF.ApprovalAcceptance/Program.cs), [reconciliation tests](../../tests/MAF.Tests/ReconciliationTests.cs), [owner regression](../../tests/MAF.Tests/AbortDeliveryTests.cs) | Synthetic-only policy/provider state, deterministic gates, fault injection and captured independent processes |

Other changes (16 scoped files total): this report, [Roadmap](../ROADMAP.md) and [execution plan](../plans/PHASE2B2_EXECUTION_PLAN.md). Host routing/DI, production trust, approval/Resume semantics, provider permission replies, original fingerprint/OperationBinding, central DurableExecutionTransitions and official Workflow are preserved. No native PCA/Reviewer, Registry, Hybrid, scheduler, event store or generic transaction framework is added.

## Independent authorization and observation

The application authenticates the actual context before target disclosure. Store admission derives the original run/execution/message/fingerprint/session and requires independently current `InspectExistingExecution / ExecutionInspection` Allow. The active durable authority tuple/digest, exact single-operation capability, caller/context, original readonly scope/configuration/root/provider, verified receipt, immutable cancellation winner and exact delivery are checked. RequireApproval is not execution permission. Submit/Cancel/Abort grants, Ack, consumed approval and a database ID are not Inspect authorization.

The required trusted composition uses the same exact-v4 SQLite coordinator for admission, capture and per-GET enforcement, a matching explicit-v4 execution store, trusted policy/identity dependencies and owned transport. Only strict ExplicitV4/native ownership participates; legacy-unverified and quarantined receipts deny. The application acquires the actual TaskId execution handle for this invocation. It never obtains a delivery handle to inspect. Existing ProtectedExternalRuntime.ProtectedAccess reauthorizes each necessary GET against current policy/control/ownership. Existing generic durable recovery stays compatible and does not claim to update cancellation-delivery outcomes; the explicit M4-C application is required for this joint evidence contract. Source acquisition and all provider GETs are outside SQL; no authority lease spans GET. Capture reauthorizes after GET and rejects changed policy, identity/context, control, execution or delivery version before durable success.

Original `OpenCodeClient.ReattachAsync` performs existing bounded health/schema/agent/session verification; the same `ObserveAsync` obtains session, pending permission/question, status and correlated messages. A normal terminal case uses **9 GETs** in total; projecting evidence adds **zero** GETs. The protected recovery context denies Create, Submit and Abort. The only new application calls into the adapter are Reattach and Observe, under one bounded provider-GET budget (positive, at most 60 seconds); existing authority acquisition and SQLite acquisition remain separately bounded at five seconds. No background polling/retry loop exists.

The internal terminal projection is issued only after the existing idle/correlated terminal branch. It binds provider/scope, original session/message, assistant ID/parent, finish/error code, outcome and trusted observation time. Assistant parent and session must equal the original message/session. Nonintegral/inconsistent completion timestamps and missing/invalid/unrepresentable metadata cannot produce durable terminal proof. Numeric provider-shape exceptions now use the existing InvalidProviderShape failure boundary. Normal non-projection clients retain existing terminal classification.

Completed/Partial follow the existing completion, finish and truncation rules. Idle correlated MessageAbortedError yields Cancelled; another valid correlated terminal provider error yields Failed. Busy/retry, permission pending, uncorrelated/missing/incomplete messages, idle alone, malformed shapes, provider unavailability and CTS/timeout do not establish terminal evidence. In particular, Ack/HTTP true never yields Cancelled.

## Canonical evidence and joint transaction

The existing v4 `TerminalObservationBinding` and `CancellationObservationBinding` encodings are reused unchanged. Terminal metadata contains no prompt, transcript, response body, arbitrary output/exception message or credential. Codes/IDs remain bounded by the accepted closed contract. Delivery observation additionally binds the freshly authorized Inspect operation/capability, subject/context, authority epoch/version/digest, full task/run/execution/scope/fingerprint, original receipt/cancellation/delivery, version and audit reference.

Independent fixed golden TerminalEvidenceDigest:

```text
5FDD7B77DCF8CB87761924355D14579B25E7F62CAC534B3D97281E2CC3A91A80
```

It is independently calculated with strict UTF-8, big-endian length/Int64 encoding for fixed test metadata, then compared with the existing v4 encoder. Changing parent binding changes the digest; no raw JSON equality is used for evidence identity.

Commit order: native execution-owner use → acquire current authority outside SQL → guarded short writer transaction → reread/reauthorize exact target/control/delivery → compare captured versions/snapshot → central transition validation → execution terminal CAS → mandatory bounded audit → delivery terminal CAS/observation → full canonical read/transition verification → reauthorize → commit. **Execution and delivery terminal evidence commit in that single SQLite transaction.** The existing hard audit limit remains; any fault/CAS/audit failure rolls back all terminal writes and does not return Recorded.

Execution state writes retain IDs, phase, fingerprint, AbortRequested and provenance; timestamps never move backward (commit time is at least the existing execution UpdatedAt). This also accommodates existing service wall timestamps versus the explicit synthetic policy clock without weakening transition assertions. Execution terminal rows are immutable: same terminal state can receive its missing verified delivery evidence without rewriting the row; conflicting evidence returns Unknown/TerminalEvidenceChanged. A terminal ledger inconsistent with the execution denies rather than claiming joint success.

Same terminal metadata replay reauthenticates/reobserves but retains the original terminal object/digest, version and audit. A different later outcome or assistant terminal identity preserves the committed history and returns explicit conflict. Nonterminal observations return bounded Running/Waiting/Unknown diagnostics with no ledger/audit growth. Original delivery stage and Ack time remain unchanged; `Intended` with zero actual Abort can still reconcile an independently proven Completed/Cancelled outcome without sending Abort.

## B2 owner-disposal regression

The deterministic `DeliveryPreflightReturned` gate reproduces the exact baseline gap: preflight completes/releases owner use, another thread disposes the actual owner, and baseline code sends **one fake Abort POST**, then fails acknowledgement capture. The initial test failed its zero-POST assertion; evidence is retained in `phase2b2-m4-c-owner-before.trx` (test output, not committed).

The fix adds one private dispatch-use scope captured by the existing issuer-only continuation. After preflight, the adapter acquires/checks that same live delivery owner before initiating HTTP and retains use through the bounded response. Disposal before use acquisition rejects with **zero POST**. Disposal during an already valid in-flight send waits for use to finish; it cannot retroactively unsend the request. If ownership closes before acknowledgement capture, the spent Intended row remains uncertain, without fabricated Ack or retry. This scope is **not** a SQLite transaction/authority lease; no TaskId lock is acquired, and concurrent explicit policy activation remains possible during HTTP. Existing crash/unique-reservation semantics remain unchanged.

## Validation and process evidence

All artifacts are synthetic; no live model/OpenCode, company endpoint, security-policy/ACL change or corporate event-record modification is performed.

| Final selected tests | Discovered / executed / passed | Failed / skipped |
|---|---|---|
| New reconciliation cases | 57 / 57 / 57 | 0 / 0 |
| New B2 owner-lifetime regressions | 2 / 2 / 2 | 0 / 0 |
| Retained B1b+B2 focused acceptance | 181 / 181 / 181 | 0 / 0 |
| Combined focused | **240 / 240 / 240** | **0 / 0** |
| Full offline suite | **973 / 973 / 973** | **0 / 0** |
| Solution build | **Success, 0 warnings / 0 errors** | — |

Actual TRX test-name **multiset** comparison against B2's 914 cases: zero missing/renamed historical cases and exactly 59 additions. No passing assertion, schema/profile check, no-resubmit or authorization test was weakened. Live OpenCode tests are excluded by the explicit filter rather than counted as skipped acceptance.

Final commands:

```text
dotnet build MAF.slnx --no-restore
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --list-tests --filter 'FullyQualifiedName~ReconciliationTests|FullyQualifiedName~AbortDeliveryTests|FullyQualifiedName~SessionReceipt|FullyQualifiedName~DeliveryStorageTests|FullyQualifiedName~CancellationSessionEvolutionTests'
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~ReconciliationTests|FullyQualifiedName~AbortDeliveryTests|FullyQualifiedName~SessionReceipt|FullyQualifiedName~DeliveryStorageTests|FullyQualifiedName~CancellationSessionEvolutionTests' --logger 'trx;LogFileName=phase2b2-m4-c-focused.trx' --results-directory TestResults/home-validation
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName!~MAF.Tests.OpenCodeLiveTests' --logger 'trx;LogFileName=phase2b2-m4-c-offline.trx' --results-directory TestResults/home-validation
```

Actual outcome coverage: Ack→Completed/Partial/Failed/Cancelled; genuinely completed truncated output→Partial; uncertainty→Cancelled/Completed; Intended with zero Abort→Cancelled/Completed. Every normal terminal reconciliation uses **GET/Create/Prompt/Abort = 9/0/0/0**, including the original session/message identities below. Running, pending permission, idle without a terminal message, wrong parent/session, missing ID/completion, malformed/overflowing times, invalid codes/shapes, unknown provider status, outage and Inspect cancellation do not create terminal evidence. Abort-only, Cancel-only, Submit-only, forged/expired/revoked caller, wrong scope, unavailable policy, legacy and quarantine deny without provider effects.

Fault/audit-capacity tests roll back execution, evidence and required audit together. Canonical receipt/cancellation/delivery/session tampering denies before GET. Activation/revocation/control changes after GET deny capture. A real in-flight authorized Abort Ack commits while an Inspect is observing; capture detects the changed delivery version and rejects without a partial execution write. This demonstrates the independent owner/CAS ordering rather than assuming delivery and inspection cannot overlap. Replayed evidence retains digest/version/audit; changed outcome, changed terminal message or later Running returns Unknown/conflict and preserves prior terminal history. An ordinary competing execution writer cannot acquire the held TaskId; a separate privileged test injection of completion during GET also cannot make reconciliation overwrite that terminal state.

### Actual OS processes and checkpoints

Original and recovered identity in every process case: **`runtime-task / runtime-execution / runtime-run / msg_runtime / ses_runtime`**. Cancellation `cancel-one`, delivery `delivery-one`. The original execution/Abort setup has already ended before the independent Inspect processes run. Source is freshly configured **Inspect-only epoch/version2**, with no Submit/Cancel/Abort rule and no restored continuation or approval. The test-owned fake provider persists only original session permissions/message identity for restart simulation, not transcript or prompt.

| Abrupt termination checkpoint | Killed PID / start UTC | Fresh reader PID → restart PID | Reader sees before restart | Total GET / Create / Prompt / Abort |
|---|---|---|---|---|
| Before Inspect GET | 58080 / 2026-10-17T05:37:32.9789004Z | 14480 → 15832 | Execution Unknown; Ack v1 / outcome None | **9 / 0 / 0 / 0** |
| After GET, before capture | 62528 / 2026-10-17T05:37:42.9770407Z | 14472 → 51756 | Unknown; Ack v1 / None | **18 / 0 / 0 / 0** |
| Execution written, precommit | 48788 / 2026-10-17T05:37:39.5774807Z | 38696 → 65684 | Unknown; Ack v1 / None; execution write rolled back | **18 / 0 / 0 / 0** |
| Evidence written, precommit | 23888 / 2026-10-17T05:37:29.5600752Z | 70052 → 18816 | Unknown; Ack v1 / None; both writes rolled back | **18 / 0 / 0 / 0** |
| Complete transaction committed | 16696 / 2026-10-17T05:37:36.1955471Z | 52932 → 63104 | Execution Completed + delivery v2 / Completed | **18 / 0 / 0 / 0** |

All fresh readers report `integrity_check=ok`. Restarts either commit the complete terminal pair or return AlreadyRecorded with the existing committed digest. GET is repeated with new valid Inspect authorization; no POST is repeated. The postcommit reader's actual digest is `5CFDF04D9C241F3170B0E955CCFDB5BC93B5F8D16AC93F02DEEA9B096D01C14B`; it remains the original recorded evidence rather than a rebuilt dispatch token.

Independent Inspect competition: winner PID **21404**, start UTC **2026-10-17T05:38:01.4035466Z**, holds the actual execution owner at AfterGet; competitor PID **3660** returns InspectBusy with no provider request. Winner commits Completed. Combined **GET/Create/Prompt/Abort = 9/0/0/0**. B2's separate cancellation/delivery ownership, lifetime uniqueness, crashes and no-resend cases remain part of the passing focused/full regression.

Final owner-race regression records **AbortPOST=0 / deliveryStage=Intended** when the owner is disposed after preflight. The in-flight owner-use test proves disposal waits while HTTP is valid, policy activation is not blocked by a SQL/authority lease, and subsequent lost-owner capture cannot fabricate Ack.

## Acceptance boundary and next owner review

**The controlled M4 cancellation lifecycle satisfies its synthetic technical acceptance gate and is ready for owner acceptance**, within this report's bounds: Cancel intent → single authorized Abort attempt/uncertainty/Ack → independently authorized original-session outcome. No unresolved v4/adapter contract blocker requires a permissive workaround. This does **not** accept all Phase 2B-2 or prove provider honesty, cryptographic message authenticity, power-loss/distributed durability, hostile same-principal isolation, production identity or corporate compatibility. Provider retention, bounded message pages, representable metadata, hard audit capacity and accepted zero-send reservation crash windows limit availability; insufficient proof remains Unknown.

M3-C authenticated sources/callers/human proof where applicable, protected writers, revocation and anti-rollback remain mandatory before operational activation. Host remains disabled. No live-provider claims are made from fake transport tests.

Final scoped review: 90 relative document links and fenced blocks verified; staged whitespace checking passed. No generated DB/TRX/provider-state file or unrelated `.vscode/` content is included.

**STOP after M4-C. Request owner-level architecture review before additional infrastructure.** Recommended separately authorized next scope after that review: a minimal deterministic PCA → OpenCode → Reviewer Workflow with typed handoffs and controlled fake harnesses first, reusing these lifecycle/authorization contracts. Do not automatically start M3-C, M5 or later phases, another adapter or Hybrid routing.
