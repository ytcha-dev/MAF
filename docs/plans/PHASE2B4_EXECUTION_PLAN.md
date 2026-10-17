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

## Proposed 4B, separately authorized only

After owner review of 4A evidence semantics, consider one fake-HTTP protected-runtime
adapter under synthetic trust and existing v4 storage. Preserve exact parent task,
run, prepared execution/message identities, current admission/effect enforcement,
original-session recovery and no-resubmit behavior. Current ProtectedExternalRuntime
owns its Workflow and ownership lifetime; embedding it must explicitly address
that lifetime and parent/child run correlation without calling raw OpenCode APIs.
Do not export the 4A fake boundary as a production authorization mechanism.

Acceptance should prove real guarded Create/Submit/Inspect counts, denial with zero
HTTP, cancellation/activation races, and crash/recovery across stage handoff. If
safe lifetime/correlation reuse requires broader changes, stop at that concrete gate.
Live providers, production Host/M3-C, human consent, write tasks, Hybrid routing and
Registry remain outside 4B unless separately authorized. No automatic next phase.
