# Runtime Lifecycle Closure — 2026-10-21

## Baseline and authorization

Baseline 03a37458d743f7a5330a84ba60bcf7bceec60433 (merged PR #1), initially clean work branch.
Prior commits 75affd2, 16ad3d0 and e7f802e are ancestors and their fixes/tests remain present.
Historical baseline: 1572 passed / 0 failed / 3 intentionally skipped live tests.
Owner explicitly authorized this offline lifecycle increment and formally escalated focused
plus one full regression run. No live Harness, paid inference or production activation.

## Design and implementation

Selected a small combination of bounded cancel/wait/drain and explicit caller ownership.
See [lifecycle architecture](../architecture/PERSONAL_LAB_RUNTIME_LIFECYCLE.md).
The runtime owns its single journey/child and external transport; caller-owned native clients
and journal must outlive drain. Track underlying Workflow/PCA/Reviewer tasks as well as
parent tasks, so bounded waits do not disguise active work. Link caller and runtime-stop tokens.

Runtime cancellation closes new starts, delivers local callbacks once, and preserves callback
errors. Waiting has an explicit bound/caller token; timeout leaves ownership intact. Drain
reports observed local task fault/cancellation counts without inventing a successful domain
outcome. Active disposal still fails; quiescent disposal is idempotent and finally releases
linked cancellation sources even when the owned transport throws. No provider Abort is added.

## Offline test coverage

RuntimeLifecycleTests uses deterministic entered/release/cancellation handshakes:
normal completion and journal inspection; cooperative and uncooperative child cancellation;
active disposal rejection; bounded wait timeout and cancelled wait without abandonment;
underlying PCA and Reviewer work held after journey completion; cancellation after prompt
submission preserving Unknown and original verified identity; caller-token cancellation;
repeated stop/disposal; stop before start; transport disposal failure; faulted child and
cancellation callback errors; isolation between independently owned runtime instances.

Existing independent-runtime, protected-handoff, native Reviewer, recipient and first-run
boundary regressions are included in focused validation. Existing invalid-receipt outcome
and production activation guards are unchanged.

New cases explicitly reuse the necessary file-backed owned fixture: semantic
maf-independent-lab/<lifecycle-case>/<GUID-run-id>/workspace and sibling storage,
Example.cs and <operation-id>.lab-journal. This validates real journal/receipt/source
behavior; no new SQLite or processes are added by lifecycle cases. No eager xUnit directory
allocation. Test finally blocks release controlled gates, cancel/drain, and dispose before
exact owned flat-file/directory cleanup. No parent/global sweep or stale journal reuse.

## Validation evidence

Initial lifecycle-only run: 8 passed / 1 failed / 0 skipped (9 total).
The cooperative cancellation-observation test registered a callback beside WaitAsync;
unwind could dispose that registration before its callback ran. The corrected controlled
handler observes cancellation in the catch path as well. No sleep or increased timeout.
Additional cases cover post-submit cancellation, faulted children and callback failure.
Historical initial TRX is retained outside the repository.

Expanded focused first run: 170 passed / 1 failed / 0 skipped (171 total).
The cancellation-callback-failure case incorrectly demanded zero GETs: an already-reserved
health GET can return after the controlled handler is released despite cancellation callback
failure. The assertion now permits only that one in-flight health GET and rejects every
other request, including all POSTs; no production guard, timeout or retry behavior changed.
This historical TRX is also retained.

Final build: 0 warnings / 0 errors, SDK 10.0.402.
Command: dotnet build MAF.slnx --no-restore --disable-build-servers -m:1.
Final focused: 171 passed / 0 failed / 0 skipped (18s), including all 12 new lifecycle rows.
Selection: RuntimeLifecycleTests, IndependentLabRuntimeTests, NativeEvidenceReviewerTests,
ProtectedHandoffTests, PersonalLabFirstRunBoundaryTests and BoundNativeRecipientTests.
Full offline regression: 1584 passed / 0 failed / 3 skipped, total 1587 (2m52s), exit 0.
All 12 new lifecycle cases passed in both final focused and full runs. The former Linux
process-portability failure also passed. The three skipped cases are exactly the opt-in
OpenCodeLiveTests. Compared with the prior 1572/0/3 baseline, 12 passing rows were added.
TRX counters and named outcomes were inspected directly. Git diff --check passed; the
source/security review confirmed no new public execution route, Abort, retry, production
approval or transport activation. Documentation is the only change after final verification.
Implementation/test commit: c7fe38f. Dedicated branch: codex/runtime-lifecycle-closure.
Logs and TRX: /workspace/work/maf-lifecycle (outside the repository).
Tests use existing build/packages, --no-build --no-restore --disable-build-servers -m:1,
MAF_LIVE_OPENCODE=0 and the explicitly authorized formal sandbox escalation for local VSTest.

## Remaining gates

This is offline lifecycle evidence, not Owner acceptance of all S2D-D or operational readiness.
Uncooperative tasks remain owned and can prevent disposal until they actually complete;
transport Dispose failures remain visible without guaranteed third-party cleanup. No forced
termination or automatic resume. No live transport factory, downstream attestation, production
identity/approval activation, second Harness, Registry or Hybrid is delivered. Corporate hold
remains independent. Controlled Live OpenCode PoC needs separate review/authorization.
