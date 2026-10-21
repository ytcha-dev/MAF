# S2D-D Reliability Closure — 2026-10-21

## Scope and baseline

Owner-authorized bounded reliability work on `a02564e2520fb19434ea30ea02658ed3f78c679e`,
branch `work`, initially clean. SDK 10.0.402. Previous Linux baseline:
1562 passed / 1 failed / 3 intentionally skipped live OpenCode tests.
No S2D-E, live integration, production activation, Registry or Hybrid authorization.

## Confirmed causes and changes

### Linux process diagnostics

`IndependentProcessesCompeteWhileExecutionOwnerIsHeld` waited for the winning child to
exit before reading its StartTime for diagnostic output. Linux can no longer resolve
that process metadata. Capture both owner/winner start times after their checkpoint
handshakes and before releasing either process. Keep DeliveryBusy, exit-code, single
Abort and single reservation assertions unchanged. Add competitor cleanup and nested
finally cleanup so owner cleanup is attempted even after another cleanup fails.
No sleeps, Linux skips, timeout increases or production process changes.

### Interrupted invalid-receipt outcomes

The Lab journal preserves a returned candidate session before receipt verification.
The runtime correctly cannot publish it as a verified handle; the resulting Unknown
outcome has a null session. RecordOutcome previously demanded equality with the candidate
and rejected that diagnostic result.

Record the bounded status, error code and actual outcome session without changing the
candidate. A distinct InterruptedObservation permits the missing handle only while a
bound Lab protocol has not verified its receipt, and only for Unknown/Failed/Cancelled.
A non-null handle must still equal the original candidate. Once verified, even a null
replacement is rejected. Unverified Completed or ReadyForHumanReview/sufficient-evidence
claims are rejected. Original task/run/execution/provider/request correlation remains exact.

Inspection exposes CandidateSession and VerifiedSession separately, validates the added
observation fields and rejects contradictory interrupted success/identity claims. Optional
fields keep old journal rows readable; older binaries are not promised to read the new
InterruptedObservation event. No migration, reconstructed authority or resume is introduced.
Journal data remains cooperative provenance, not authenticated human consent.

Recording a diagnostic consumes admission without refunding Create/Prompt attempts;
revoked admission remains revoked. Invalid receipt still sends no prompt, Abort, shell
or permission reply, and does not become a verified receipt. Reopen remains inspection-only.
Quiescent runtime disposal and all original production/live blockers remain unchanged.

## Regression coverage and filesystem ownership

Existing independent journeys now record invalid-receipt outcomes instead of using the
presentation-only fallback. They inspect status/error/session after closing the exclusive
writer and assert no duplicate record or new prompt. Existing lost-create journey records
Unknown with neither candidate nor verified session.

New identity cases reject another session, missing verified identity, unverified Completed
and review-ready substitutions, then prove the original diagnostic remains recordable.
Reopen tests reject edited outcome identity, success status and review gate.
Four interrupted-candidate cases cover Unknown/Failed/Cancelled and an explicitly echoed
candidate; revocation does not prevent diagnostic persistence or permit new execution.

These cases require actual exclusive/flushed journal files and reopening. They explicitly
allocate only their own `maf-independent-lab/<semantic-case>/<GUID-run-id>/workspace`
and sibling `storage`, using Example.cs and `<operation-id>.lab-journal`. No SQLite is
added to Lab fixtures. Owned handles close before exact flat-file/directory cleanup;
no global sweep, parent deletion, eager xUnit directory allocation or stale journal reuse.
The existing process suite retains its existing isolated SQLite/child-process fixtures.

## Validation

Build: `dotnet build MAF.slnx --no-restore --disable-build-servers -m:1`
passed with 0 warnings / 0 errors on the final source snapshot.

Focused: 135 passed / 0 failed / 0 skipped (1m57s). Selection: IndependentLabRuntimeTests,
AbortDeliveryTests, PersonalLabProvenanceTests, PersonalLabDurableWorkflowTests,
PersonalLabFirstRunBoundaryTests and LabOpenCodeBoundaryTests. The formerly failing
IndependentProcessesCompeteWhileExecutionOwnerIsHeld passed. Nine new theory rows
cover identity substitutions, journal tampering and interrupted candidates.

Full offline suite: 1572 passed / 0 failed / 3 skipped, total 1575 (3m20s), exit 0.
The three skipped cases are exactly the opt-in OpenCodeLiveTests. Relative to the previous
1562/1/3 baseline, the known failure now passes and nine new regression rows pass.
TRX counters and the named Linux portability case were checked directly.
`git diff --check` passed; only the two fixes, their tests and directly related documentation
changed. No unrelated working-tree modifications were present.

Tests use `--no-build --no-restore --disable-build-servers -m:1 --logger trx`,
MAF_LIVE_OPENCODE=0, and the formally authorized escalation for VSTest local communication.
The initial sandbox attempt aborted before discovery/execution with SocketException(13).
Owner explicitly authorized focused plus one full offline run. No external Harness or
paid inference was installed/launched/called, and no environment configuration changed.
Logs/TRX remain outside the repository under `/workspace/work/maf-closure/`.

Implementation commits: `75affd2` (process test portability), `16ad3d0` (Lab outcome
persistence and regression coverage). No push or merge performed.

## Acceptance boundary

This closes only the two authorized reliability defects. It does not change S2D-D's
conditional Owner acceptance into unconditional acceptance, establish production trust,
remove corporate-device holds, authorize a live transport, or enable public execution.
