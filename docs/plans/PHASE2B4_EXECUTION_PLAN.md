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

Next proposed narrow step requires owner review of those evidence semantics, followed
only if separately authorized by a bounded structured candidate extraction contract.
Persistent parent-stage recovery, real semantic models and production trust each need
their own future authorization; none is silently added here.
