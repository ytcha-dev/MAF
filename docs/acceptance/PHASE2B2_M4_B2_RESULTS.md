# M4-B2 — Authorized Abort delivery acceptance

2026-10-17 (Asia/Taipei). Baseline: `cdf5f311a654c6afee9b16bff67b5afe2d2a976f`. This increment is controlled synthetic Abort delivery/ack evidence only. M4-C remote terminal reconciliation and M3-C production trust are not implemented or waived. Host external modes remain `ProductionTrustUnavailable`; there is no operational cancellation CLI, Broker, real-human authentication, live model request, permission reply, write/shell capability or background worker.

## Implementation and required composition

| Component | Narrow change |
|---|---|
| [Policy contracts](../../src/MAF.Core/Policy/PolicyContracts.cs) | Add `AbortExistingExecution=8` / `ExecutionAbort=4` and their deterministic supported mapping. Existing values/names, OperationBinding v1 and submission fingerprint bytes are unchanged |
| [Delivery contracts](../../src/MAF.Core/Policy/AbortDelivery.cs) | Fixed command/trusted configuration, OS ownership interface, evidence projection and issuer-only, nonserializable, one-use continuation |
| [SQLite coordinator](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.AbortDelivery.cs) | Independent delivery ownership; short atomic reservation/audit; exact current preflight; historical response CAS/audit. No network/Workflow dependencies |
| [Existing evidence reader](../../src/MAF.Persistence.Sqlite/SqliteApprovalStore.DeliveryEvidence.cs) / [format](../../src/MAF.Persistence.Sqlite/DeliveryStorageFormat.cs) | Reuse full bounded canonical receipt/cancellation/control/audit validation inside reservation, preflight and response capture. Reader does not issue/reconstruct continuations |
| [Protected adapter](../../src/MAF.Workflows.External/OpenCode/ProtectedAbortDelivery.cs) | One original-session POST from an opaque committed continuation, owned no-redirect transport, bounded response parsing and acknowledgement capture. No arbitrary public session argument |
| [Synthetic fixture](../../tests/MAF.ApprovalAcceptance/SyntheticAbort.cs), [process helper](../../tests/MAF.ApprovalAcceptance/Program.cs), [tests](../../tests/MAF.Tests/AbortDeliveryTests.cs) | Test-only identities/policy, disposable exact-v4 DB, fake HTTP and independent captured OS processes |
| [Shared cancellation fixture](../../tests/MAF.ApprovalAcceptance/SyntheticCancellation.cs), [HTTP fixture](../../tests/MAF.ApprovalAcceptance/SyntheticRuntime.cs), [storage tests](../../tests/MAF.Tests/DeliveryStorageTests.cs) | Explicit test-only expired identity, fake Abort acknowledgement and authorized enum-allocation expectation update |

Documentation changes are this report, [Roadmap](../ROADMAP.md) and [execution plan](../plans/PHASE2B2_EXECUTION_PLAN.md): **15 scoped files in total**.

The protected caller supplies trusted `AbortDeliveryInputs` through controlled composition and the actual verified caller context. The store derives run/message/fingerprint/session from the original execution and independently authenticates/authorizes through the existing protected authority. Exact current Abort Allow is mandatory; RequireApproval is `AbortApprovalNotSupported`. Submit, Inspect, RequestCancel, Approved/Consumed, receipt status and capability availability never substitute for Abort rights.

Only exact ExplicitV4 stores participate. Original immutable cancellation winner, verified receipt, complete identity/scope/configuration, original provider, active authority tuple/digest, readonly capability and nonterminal PromptIntended-or-later execution must all match. An empty SessionKnown session, quarantine, legacy unverified row, terminal execution or changed binding cannot reserve. Current control must still be the captured cancellation version. There is no session creation, prompt submission, execution admission/consumption or execution-row update in this coordinator.

The three new production files total fewer than 300 lines. There is no new schema, migration, ledger, scheduler, agent loop or general transaction framework. Existing schema v4 and its immutable/unique/CAS triggers remain unchanged. `OpenCodeClient` ordinary bindings, raw Abort guard, `ProtectedExternalRuntime`, `DurableExecutionService`, Workflow and Host routing/DI remain unchanged.

## Ownership, ordering and HTTP boundary

`<canonical-db>.delivery-locks/<SHA256(CancelRequestId)>.lock` uses an actual `FileShare.None` handle, private database/cancellation identity and serialized disposal checks. It is independent of TaskId ownership; delivery never acquires or steals that lock. Existing schema maintenance already gates and acquires delivery files. Acquisition checks linked paths, takes the schema gate before opening the file, and never waits for a task owner. Transactions use the existing ReadWrite, Pooling=false, FK, synchronous FULL, bounded five-second SQLite acquisition profile. Process death releases the OS handle; a leftover file is not ownership.

Reservation order: live delivery owner → trusted authority lease outside SQL → exact active-policy writer transaction → full target/control/receipt verification → unique intent plus mandatory bounded audit → commit → issue continuation. Failed audit/transaction produces neither reservation nor continuation. No caller snapshot activates policy. No approval row is manufactured or changed.

Dispatch burns the continuation before any effect. Trusted OpenCode options/root/provider scope and owned transport must match. Final preflight rechecks the live owner, exact authentication context, current independently authorized Abort operation, active authority, nonterminal execution, original receipt/control and complete unchanged reservation. Authority lease and SQLite transaction end before HTTP. The adapter uses the verified original-session capability directly, with **no fabricated generic client binding and no Reattach GET**. The only effect is `POST /session/{originalSessionId}/abort?directory={originalTrustedRoot}`. Abort does not confer Inspect rights.

SQLite commit and HTTP delivery are not atomic. A policy/control/terminal change after final preflight cannot retroactively unsend HTTP. The transport budget is independently bounded by the trusted Abort scope (positive, at most five seconds), not the running task CTS. An explicit pre-wire cancellation suppresses this reserved attempt; cancellation after send does not start another Abort. The response body is bounded to 32 bytes, accepts JSON `true` only from the actual owned request/response association, and is never stored.

## Truthful evidence and crash semantics

| Event | Durable meaning |
|---|---|
| Reservation committed | Intended, may have sent; no replayable dispatch token in the DB |
| Original valid acknowledgement | Acknowledged, `remote_outcome=None` |
| Response lost / timeout / outage / false / invalid or foreign response | SentUncertain, `remote_outcome=None`, no retry |
| Live pre-wire authorization/configuration/control denial | SuppressedBeforeWire, outcome None, reservation remains spent |
| Crash after reservation before wire | May send zero requests permanently; restart cannot reconstruct the continuation |
| Crash after wire before evidence commit, or evidence/audit write failure | Original Intended remains uncertain; never fabricate Ack or retry |
| Disposed owner prevents capture | Fail closed; retain Intended rather than claim an unwritable suppression/ack |

Response capture is historical, tied to the original committed Abort pair/context/digest and live owner. It does not require or create fresh permission after HTTP, and can preserve the original Ack if policy changes during the request. It updates only delivery version/stage/observation/audit. It never calls `DurableExecutionService.PersistingClient.AbortAsync()` (which saves AbortRequested first), never sets AbortRequested, and never writes a terminal execution. A task completing during the POST retains that exact Completed snapshot; Ack remains unconfirmed remote outcome. Idle, HTTP true and an empty session are not cancellation proof.

The lifetime uniqueness constraints remain authoritative after an OS lock is released. One reservation and at most one adapter SendAsync invocation are supported; **exactly-once remote delivery is not claimed**. No timer, startup sender, retry dispatcher, second generation or automatic reset exists.

## Verification

All tests use synthetic trust and fake transport; no live OpenCode/model or corporate endpoint test is included.

| Final validation | Discovered / executed / passed | Failed / skipped |
|---|---|---|
| New AbortDeliveryTests | 56 / 56 / 56 | 0 / 0 |
| Retained B1b receipt/storage/B0 focused cases | 125 / 125 / 125 | 0 / 0 |
| Combined focused | **181 / 181 / 181** | **0 / 0** |
| Full offline suite | **914 / 914 / 914** | **0 / 0** |
| Solution build | Success, **0 warnings / 0 errors** | — |

Commands actually run against the final sources:

```text
dotnet build MAF.slnx --no-restore
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --list-tests --filter 'FullyQualifiedName~AbortDeliveryTests|FullyQualifiedName~SessionReceipt|FullyQualifiedName~DeliveryStorageTests|FullyQualifiedName~CancellationSessionEvolutionTests'
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName~AbortDeliveryTests|FullyQualifiedName~SessionReceipt|FullyQualifiedName~DeliveryStorageTests|FullyQualifiedName~CancellationSessionEvolutionTests' --logger 'trx;LogFileName=phase2b2-m4-b2-focused.trx' --results-directory TestResults/home-validation
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName!~MAF.Tests.OpenCodeLiveTests' --logger 'trx;LogFileName=phase2b2-m4-b2-offline.trx' --results-directory TestResults/home-validation
```

TRX multiset comparison against B1b's 858 cases finds **zero missing historical cases and exactly 56 additions**. One historical storage test is renamed from `ReservedEvidenceIsReadableButNoPolicyOperationOrProviderDispatchWasEnabled` to `ReservedEvidenceIsReadableButDoesNotIssueDeliveryContinuation`: its former enum-undefined assertions are replaced by the explicitly authorized values 8/4. Its original data-only stage/version/outcome and zero authorization-operation-row assertions remain. No other passing assertion is weakened. The new typed Abort encoding golden test equals B1b's reserved canonical bytes; existing Inspect/binding/fingerprint regressions remain passing.

Negative cases include Submit-only, Inspect-only, Cancel-only, explicit Deny, unsupported RequireApproval, forged/missing/wrong/expired caller, expired/unavailable policy, revoked rights, wrong task/execution/session/scope/revision, quarantine, legacy, absent cancellation, empty session, terminal target, invalid/disposed/foreign owner, control/policy activation changes and opaque/wrong-origin/root transport. These produce no Abort POST. Response loss, bounded timeout, outage, false and foreign acknowledgements produce one uncertain attempt without retry. Transaction fault and audit exhaustion roll back the corresponding intent/ack checkpoint; no fabricated acknowledgement is returned. A completion during the POST preserves the exact terminal execution snapshot.

Initial validation caught a synthetic fixture that reported completion after losing the prompt response; the implementation correctly rejected that terminal target. The fixture now explicitly stays Running, and terminal denial is separately asserted. Initial test-harness failures also exposed the zero-call ledger-file absence and killed-owner cleanup timing; zero calls are counted from an absent ledger and the non-crash competition owner now releases gracefully. The dedicated crash cases still abruptly terminate only their captured owned process. Final reruns above include these corrections.

## Actual HTTP and OS-process evidence

Final focused-run original identity: `runtime-task / runtime-execution / runtime-run / msg_runtime / ses_runtime`. Target remains **that same `ses_runtime`**, cancellation `cancel-one`, delivery `delivery-one`, Abort operation `abort-operation`. Setup deliberately loses one prompt response while the controlled provider stays Running. Its existing bounded observation may perform many GETs; delivery does not reuse that observation path.

| Origin of execution | Setup GET / Create / Prompt / Abort | Delivery GET / Create / Prompt / Abort | Result |
|---|---|---|---|
| Direct Allow | 306 / 1 / 1 / 0 | **0 / 0 / 0 / 1** | Acknowledged, outcome None, exact execution snapshot unchanged |
| Approved Explicit Resume | 181 / 1 / 1 / 0 | **0 / 0 / 0 / 1** | Same; consumed approval evidence retained, independent current Abort grant required |

Counts above are the actual final focused run, not fixed expected GET counts or live-provider accounting. Fake transport records the raw method/path at entry; delivery checks the exact trusted origin/query and no request body. All delivery calls are `POST /session/ses_runtime/abort`; there is no Reattach GET. The separate Abort-only policy test removes Submit/Inspect/Cancel rules and still sends precisely that one POST.

| Abrupt termination checkpoint | Killed PID / process start UTC | Independent reader PID → restart PID | Evidence before restart / aggregate Abort calls |
|---|---|---|---|
| DeliveryIntentWritten, before commit | 58220 / 2026-10-16T18:46:10.4642461Z | 61436 → 59760 | No row; killed process sent 0, fresh first reservation sends 1 |
| DeliveryIntentCommitted, before HTTP | 44512 / 2026-10-16T18:46:19.9976238Z | 28072 → 70264 | Intended v0 / **0 permanently** |
| AbortWireEntered, fake transport entry | 38896 / 2026-10-16T18:46:13.7616235Z | 23360 → 31536 | Intended v0 / **1**, restart adds 0 |
| DeliveryEvidenceWritten, before evidence commit | 69080 / 2026-10-16T18:46:06.4575724Z | 64268 → 68376 | Intended v0 / **1**, uncommitted Ack rolls back |
| AbortEvidenceCommitted | 65956 / 2026-10-16T18:46:16.9246473Z | 7736 → 65420 | Acknowledged v1, outcome0 / **1**, restart adds 0 |

Every committed case's independent restart returns `DeliveryAlreadyReserved`; the fresh reader issues no provider call. Every DB passes `integrity_check=ok`. Every row retains original task/execution/session correlation. GET/Create/Prompt counts in these delivery/restart phases are all zero. This proves actual process termination and conservative one-attempt recovery in the synthetic composition, not real HTTP exactly-once delivery.

Independent competition: execution owner PID **65184**, start UTC **2026-10-16T18:47:54.4254433Z**, continuously holds the original TaskId lock. Delivery winner PID **67456**, start UTC **2026-10-16T18:47:54.4314446Z**, pauses at DeliveryIntentCommitted. Competitor PID **60016** receives DeliveryBusy. After releasing the winner's deterministic gate: **one durable reservation, GET/Create/Prompt/Abort = 0/0/0/1**. Delivery succeeds without obtaining execution ownership; the execution owner releases only after that result. Native owner-file, disposed/foreign/wrong-target and stale-file tests supplement this process proof.

## Limitations and next separately authorized gate

M4-C should add independently authorized original-session Inspect reconciliation, correlated terminal evidence, restart/crash/completion races and preservation of truthful Unknown/terminal outcomes. It must not retry delivery, recreate a session/prompt, treat Ack as Cancelled, or infer Inspect permission from Abort. Current GET recovery remains the historical separately protected path; this increment does not implement the cancellation reconciliation loop.

M3-C must still establish authenticated production policy/caller sources, real human proof where required, protected writers/transport custody, revocation and external anti-rollback. C# visibility, in-process guards, hashes and OS locks do not isolate a malicious principal able to replace the DB, executable, DI or transport. Tests prove controlled process crashes, not power loss, distributed ownership, provider honesty or corporate security compatibility. Strict legacy/profile rejection, hard audit capacity and the accepted zero-send crash window deliberately limit availability. No policy/security/ACL or historical corporate event record is changed.

**STOP after M4-B2.** No M4-C, M3-C, M5 or later orchestration is authorized by these results.
