# Phase 2B-4 — deterministic typed handoffs

Owner-authorized 4A baseline: `32ec0e9cb7d5f617e2d424798aada6a45a1a34e1`.
Scope is controlled offline read-only composition; production activation remains blocked by M3-C.

## 4A implementation

One official Microsoft Agent Framework 1.24.0 graph connects four typed executors:
`PCA → ExternalDelegation → EvidenceReviewer → HumanGate`.
Core owns bounded task/brief/evidence/outcome contracts. The existing native PCA is
used through a strict JSON translation adapter; the workflow independently validates
its proposal. An internal, test-owned delegation boundary uses no provider client.
A distinct test reviewer assesses correlated summary evidence, separately from the
unchanged Git-diff ReviewService. The only successful gate is ReadyForHumanReview;
there is no Accepted, approval consumption, permission reply or dispatch grant.

See [4A results](../acceptance/PHASE2B4_A_TYPED_HANDOFF_RESULTS.md) for executed
events, exact simulation boundaries, failure cases and validation counts.
No new project/package, schema, Host route, production identity, retry worker or
harness loop is introduced. Historical M3/M4 results remain unchanged.

## 4B protected integration — separately authorized implementation

Owner authorized one internal fake-HTTP protected-runtime adapter under synthetic trust
and existing v4 storage. The implemented path preserves exact parent task,
run, prepared execution/message identities, current admission/effect enforcement,
original-session recovery and no-resubmit behavior. Current ProtectedExternalRuntime
owns its Workflow and ownership lifetime. Both official Workflow objects deliberately
share the trusted preallocated run ID; provider session identity remains separate.
The parent awaits the protected child's own bounded budget and ownership unwind,
without calling raw OpenCode APIs or racing a duplicate outer timeout.
Do not export the 4A fake boundary as a production authorization mechanism.

See [4B results](../acceptance/PHASE2B4_B_PROTECTED_HANDOFF_RESULTS.md) for actual guarded
Create/Submit/Inspect counts, zero-HTTP denial, cancellation/activation/ownership races
and seven real-process crash checkpoints. Fresh Inspect recovers only the original
child; it never submits again or claims Reviewer/HumanGate parent-stage resumption.
There is no durable outer Workflow engine or new checkpoint schema.
Live providers, production Host/M3-C, human consent, write tasks, Hybrid routing and
Registry remain outside 4B unless separately authorized. No automatic next phase.

## 4C independently acquired source evidence — separately authorized implementation

The existing EvidenceReviewer executor now supports an explicitly selected controlled
verified-source mode. Test-owned candidates are bound to the original completed child;
an independent local reader reuses ProjectTools exclusions, strict UTF-8/redaction and
bounded reads. Exact ranges/anchors, issuer-created immutable snapshots, v1 content
digests and post-assessment source revalidation establish review-time provenance.
Missing/mismatched/changed evidence never falls back to simulated summary acceptance.

See [4C results](../acceptance/PHASE2B4_C_REVIEWER_EVIDENCE_RESULTS.md) for limits,
digest golden, actual protected HTTP counts and filesystem-race limitations. Sufficient
source evidence is not semantic correctness or human approval. Historical 4A/4B tests
and native Git-diff Reviewer remain unchanged. No fifth stage, schema or production mode.

Historical 4C handoff is superseded by owner-reviewed 4D/R1 and separately authorized 4E below.

## Deferred Architecture Decisions

See [4C acceptance](../acceptance/PHASE2B4_C_REVIEWER_EVIDENCE_RESULTS.md).

| Decision | Current boundary | Future trigger |
| --- | --- | --- |
| D1 — VerifiedSourceEvidence persistence/rehydration | Reader-issued evidence is output-only; arbitrary JSON cannot restore trust | Durable outer Workflow checkpoint/recovery |
| D2 — Provenance versus semantic correctness | Verified snapshots/anchors do not prove the model conclusion | 4D native Reviewer integration and subsequent real-model evaluation |
| D3 — Filesystem TOCTOU | Observed changes are detected; malicious same-principal race-free isolation is not established | Production source-access security review |

4C is complete. 4D/R1 is owner-reviewed as a controlled technical milestone.
4E Bounded OpenCode Source Candidate Integration is the current authorized increment.
D1 and D3 are not implemented in 4D.

## 4D Native Reviewer Integration — owner-reviewed controlled milestone

The separately authorized controlled composition injects `NativeEvidenceReviewer` through
EvidenceReviewer's existing callback. It uses the official ChatClientAgent with a fresh
Session and separate scripted client; tools read/search only admitted immutable 4C
snapshots, retaining original line numbers. Strict bounded JSON remains advisory and
cannot grant execution or human approval. Existing Git-diff ReviewService is unchanged.
The real protected child and original identities remain intact; 4C independently checks
source currentness before/after the Agent. No Host activation, provider configuration,
new reader, schema, generic framework or persistent parent resume is added.

Final test results are recorded in [4D acceptance](../acceptance/PHASE2B4_D_NATIVE_REVIEWER_RESULTS.md).
241 focused / 1193 offline tests passed; solution build has 0 warnings/errors (historical 4D evidence). D2 is reflected in advisory
assessment semantics; real-model semantic accuracy and injection robustness remain unverified.

## 4D-R1 — actual evidence observation correction

SufficientEvidence requires every admitted reference to have successfully returned real
source content through registered ReadEvidence or a matching SearchEvidence result.
The per-run context records only bounded successful outputs; the existing client observer
confirms matching SDK Tool-role results before the next model call. Prompt references,
empty searches and invalid/over-budget results do not count. Valid JSON with missing
observations is InsufficientEvidence; protocol/tool failures remain ReviewUnavailable.
4C independent acquisition and post-Agent source revalidation are unchanged.
See [R1 results](../acceptance/PHASE2B4_D_R1_REVIEW_OBSERVATION_RESULTS.md): 230 focused / 1206 offline passed; build 0 warnings/errors. Owner-reviewed as a controlled milestone; later 4E authorization follows.

## 4E bounded OpenCode source candidate integration — current increment

One concrete v1.18.35/v1.19.0 completed read-tool metadata.display shape supplies bounded,
untrusted path/range/text proposals from existing guarded Observe bytes. No summary parsing,
local anchor derivation, new requests or prompt changes. A transient JSON-ignored location/
candidate handoff carries trusted application-created execution binding to the existing
VerifiedSourceMode without a synthetic candidate callback. The unchanged 4C reader and
R1 Native Reviewer independently verify and actually observe every required source.

Controlled validation: 315 focused / 1265 offline passed, build 0 warnings/errors; ready for owner architecture review.
See [4E results](../acceptance/PHASE2B4_E_SOURCE_CANDIDATE_RESULTS.md) for exact fields,
rejection rules, HTTP counts, recovery/persistence limits and actual validation.
Candidates are not stored or restored as historical evidence: independent authorized
Inspect can extract fresh proposals from fresh GETs, never resubmit. No automatic outer
resume, source persistence, new schema, production trust or live-model proof. D1/D3 remain
deferred and D2 remains a semantic-quality limitation. Stop after 4E for owner review.
