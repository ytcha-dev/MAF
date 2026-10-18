# Phase 2B-4D-R1 — Reviewer evidence observation correction

Date: 2026-10-18. Baseline: `91685b8c8c5acb5de0ff3af15b9cf42b4b6d3cb4`.
Status: **controlled R1 technical gate passed; ready for owner acceptance**. Scope is offline correction only.

## Defect and reproduction

The 4D adapter admitted a strictly valid SufficientEvidence report containing all expected
references without requiring actual source-tool observation. Those identifiers were already
present in the initial prompt. Admitted provenance, tool observation and advisory assessment
are distinct facts; none establishes semantic correctness or human approval.

Before changing implementation, official Agent/fake-HTTP regression cases reproduced four
false-positive behaviors: no tool, read only A while citing A/B, search matching only A while
citing A/B, and empty search. Each incorrectly returned SufficientEvidence instead of
InsufficientEvidence. The initial 12-case reproduction run had 7 passes / 5 failures:
four defect assertions and one new fixture call-count assumption. The latter was corrected:
the existing unsupported-tool guard stops at the first model call, not a second call.
No historical assertions were weakened. Local reproduction TRX:
`TestResults/home-validation/phase2b4-d-r1-before.trx` (ignored artifact).

## Minimal implementation

Only [NativeEvidenceReviewer](../../src/MAF.Agents.Review/NativeEvidenceReviewer.cs) changes
runtime code. Its existing per-run immutable snapshot context now holds a locked dictionary
of exact successful bounded tool-output strings and their references, plus an ordinal
observed-reference set. These are never static, serialized, persisted or shared across runs.

ReadEvidence registers a reference only after successfully producing a bounded result from
an admitted source range. SearchEvidence registers only references with real matching lines.
An empty search contributes no reference. Invalid ranges/references/keywords, unsupported
tools, failed tool results and output-budget rejection never establish observation.
The existing aggregate 32,768-character output bound and call/time limits remain intact.

The already-existing BoundedClient observer checks SDK Tool-role FunctionResultContent before
the next model invocation. Only successful results exactly matching outputs produced by this
run's registered functions enter its observed set; exceptions mark review unavailable. Prompt
text, model assertions and foreign/unproduced result strings cannot establish observation.
This verifies actual SDK tool-result delivery plumbing without a new wrapper, second Agent,
manual loop, filesystem access, interface or evidence-tracking framework. It proves delivery
to IChatClient, not that a real model attended to, understood or reasoned correctly about it.

Strict JSON validation runs first. A valid SufficientEvidence report still must cite every
admitted reference; it now also requires every one to be observed. Missing observations
return **InsufficientEvidence**, with a deterministic explanation. Protocol/malformed-output,
execution/tool/budget failures remain **ReviewUnavailable**. No human approval is granted.
Observation of one successful range or matching line per required reference is sufficient
for this bounded contract; reading every source line or proving semantic completeness is
not required or claimed.

## Actual official Agent execution

[SufficientAssessmentRequiresActualToolObservation](../../tests/MAF.Tests/NativeEvidenceReviewerTests.cs)
uses the official ChatClientAgent inside the unchanged four-executor outer Workflow and
protected inner Workflow. Source candidates are independently acquired by 4C from real
synthetic local files; only model responses and provider HTTP are scripted/fake.

| Scripted behavior | Required result |
| --- | --- |
| No tool, valid JSON citing all known references | InsufficientEvidence; one model call |
| Read A only, cite A and B | InsufficientEvidence |
| Read A and B through actual official function calls | SufficientEvidence; ReadyForHumanReview |
| Search yields real A line | SufficientEvidence for single-source request |
| Search hits A only, cite A and B | InsufficientEvidence |
| Search yields real lines from A and B | SufficientEvidence; ReadyForHumanReview |
| Empty search, then claim sufficient | InsufficientEvidence |
| Invalid search/range, invented reference, unsupported tool | ReviewUnavailable |
| Seven A reads then over-budget B read | ReviewUnavailable; B source content not returned |

The scripted model receives actual Tool-role results including `Example.cs:120: return 42;`
and, in complete two-source runs, `Second.cs:120: return 99;`, each with its original v1
reference. Both sources are admitted at lines 120–121, while these successful reads use
line 120 only. The old positive 4D test reading 120–121 is preserved unchanged and passes.
Search success similarly retains reference/path/original line number. Empty searches return
no source line. The oversized test supplies bounded snapshots within the existing 16 KiB
batch bound, then exhausts cumulative tool output rather than changing production limits.

A synchronized concurrent test uses the **same adapter, identical digest references and
separate runs**. One Agent actually reads and receives source content; the other waits for
that delivery, then returns correct-reference JSON without tools. Exactly one assessment is
SufficientEvidence/ReadyForHumanReview and one is InsufficientEvidence/NeedsHumanReview.
The observed state cannot leak merely because identifiers coincide.

Every new protected case retains Completed execution and Verified receipt where checked.
All happy-path and negative observation cases retain raw fake-HTTP accounting
**Create / Prompt / GET / Abort = 1 / 1 / 9 / 0**. Reviewer adds zero provider calls.
Task/run/execution/session/message identities and SQLite v4 semantics are unchanged.
The original official graph remains PCA → ExternalDelegation → EvidenceReviewer → HumanGate.

## Independent 4C checks and regression

VerifiedSourceMode and Core source/digest contracts are unchanged. Independent acquisition
still precedes the Agent; post-Agent currentness still rejects source edits, restored
mtime and corrupted digest. Historical source-missing, binding mismatch, injection,
provider denial, Unknown/Failed/Cancelled, output validation, native Review, M3/M4,
process crash/restart and no-resubmit assertions remain in the offline regression.

Final focused run: 230 discovered / 230 passed / 0 failed / 0 skipped.
This includes NativeEvidenceReviewer 66 (53 historical + 13 new), SourceEvidence 53,
ProtectedHandoff 43 and TypedHandoff 68. Full solution build: success, 0 warnings/errors.
Full offline regression: **1206 discovered / 1206 passed / 0 failed / 0 skipped**, duration 2m8s. TRX name comparison against 4D retains all **1193 historical cases**, with zero removed/renamed cases and exactly 13 additions, all in NativeEvidenceReviewerTests. Original 4A–4D assertions remain intact.

Reproduction from repository root (.NET 10):

```powershell
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-restore --filter 'FullyQualifiedName~NativeEvidenceReviewerTests|FullyQualifiedName~SourceEvidenceTests|FullyQualifiedName~ProtectedHandoffTests|FullyQualifiedName~TypedHandoffTests' --logger 'trx;LogFileName=phase2b4-d-r1-focused.trx' --results-directory TestResults/home-validation
dotnet build MAF.slnx --no-restore
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName!~MAF.Tests.OpenCodeLiveTests' --logger 'trx;LogFileName=phase2b4-d-r1-offline.trx' --results-directory TestResults/home-validation
```

TRX artifacts remain ignored local evidence; no live-model tests are run.

## Documentation and scope

README now describes the growing local-first orchestration platform, identifies native
PCA/Review as components, lists Workflows.External and Persistence.Sqlite, removes the
blank splitting the status list, and retains the distinction between controlled composition
and an operational Host entrypoint. The execution plan shortens obsolete 4C handoff text,
retains D1/D2/D3 and separately gated 4E, and records the actual-observation condition.
Roadmap links this corrective acceptance as current evidence. Relative-link checks across the four changed documents found 87 targets and zero missing files. Final diff is limited to six files (one adapter, one test file, README, Roadmap, execution plan and this report); Core, SQLite, Workflow, Host and Guardrails have no changes. Unrelated untracked `.vscode/` is excluded. Original 4D and historical
M3/M4/4A–4C acceptance reports and counts remain unchanged.

No schema/migration, authority/policy/approval, provider protocol, new tool, production
Host/identity, real model request, Registry or dynamic routing was added. Original Git-diff
ReviewService and protected authorization/recovery remain unchanged. D1 evidence persistence,
D3 malicious same-principal source isolation, real-model semantic quality and injection
robustness remain unproven/unimplemented. Tool observation remains advisory evidence, never
human approval or a dispatch permission. Stop after R1; 4E needs separate owner authorization.
