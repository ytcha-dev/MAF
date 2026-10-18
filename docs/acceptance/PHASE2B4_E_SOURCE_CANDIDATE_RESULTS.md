# Phase 2B-4E — bounded OpenCode source candidate integration

Baseline: `8e8791238d04b9b1499da1eb2b5acadc29860756`. Date: 2026-10-18.
P0 completed before runtime edits. **4E implemented; controlled technical gate passed; ready for owner architecture review.**

## P0: provider contract, not assumed citations

Official versioned sources inspected:

- [v1.19.0 ReadTool](https://github.com/anomalyco/opencode/blob/v1.19.0/packages/opencode/src/tool/read.ts)
- [v1.18.35 ReadTool](https://github.com/anomalyco/opencode/blob/v1.18.35/packages/opencode/src/tool/read.ts)
- [v1.19.0 OpenAPI](https://github.com/anomalyco/opencode/blob/v1.19.0/packages/sdk/openapi.json)
- [message hydration](https://github.com/anomalyco/opencode/blob/v1.19.0/packages/opencode/src/session/message-v2.ts)

The installed package manifest reports 1.19.0; no local provider was launched. Direct public
GETs returned both versioned ReadTool sources with SHA256
`AFEC8294965CBD9B9E29D3453B682164F0ECDBA26574EBC048E9BA21AF209C68`.
v1.19.0 tag resolves to commit `733b2c627e5aec39cb33cc22373bdce33053ef2c`.
Ignored discovery downloads are under TestResults/phase2b4-e-p0.

GET /session/{sessionID}/message returns an array of info/parts records. ToolPart includes
its sessionID/messageID, tool, callID and state. Completed tool state supplies input,
output, metadata and time. The concrete read implementation emits metadata.display for
text files: type=file, path, text, lineStart, lineEnd, totalLines, truncated; metadata
also includes preview, truncated and loaded. Explicit one-based ranges and raw snapshot
text allow a bounded literal anchor without parsing a natural-language summary or reading
local content first. Directory/image/PDF/legacy shapes do not establish such candidates.
FileSource.text offsets are not assumed to be source line numbers. Generic OpenAPI metadata
is an open object: the concrete versioned read implementation, not schema speculation,
justifies this one display shape. This is a defensible P0 path; implementation may proceed.

The existing client already holds bounded message pages under Observe authorization.
Tool parts may reside in earlier correlated assistant messages (finish=tool-calls), before
the final Completed assistant. Extraction must use those already-fetched correlated
records. The current result drops tool metadata, so a small transient transport field is
needed. SQLite persists DurableExecution snapshots, not the full ExternalTaskResult;
non-authoritative candidates will be JSON-ignored and will not change snapshots/schema.
Neither tool completion nor metadata establishes verified observations; independent 4C
reads and R1 successful native tool delivery remain mandatory.

## Implemented data flow and bounds

[OpenCodeReadSources](../../src/MAF.Workflows.External/OpenCode/OpenCodeReadSources.cs)
extracts only from original-session/original-parent assistant records already collected by
[OpenCodeClient.Observe](../../src/MAF.Workflows.External/OpenCode/OpenCodeClient.cs).
The final correlated message must independently establish Completed; earlier correlated
assistant tool-call messages may contain the read parts. No second message reader or GET.
Only exact health versions 1.18.35 and 1.19.0 are eligible; this version label is not trust.

Supported tool source shape (schematic, fixture-owned values):

```text
info: id, role=assistant, sessionID=original, parentID=original submitted message
parts[]:
  id, sessionID=original, messageID=info.id, callID, type=tool, tool=read
  state:
    status=completed, input={filePath, offset, limit}, output, title,
    time={start,end}, metadata={preview,truncated=false,loaded,
      display={type=file,path,text,lineStart,lineEnd,totalLines,truncated=false}}
```

The OpenAPI-required tool envelope/completion fields and full assistant-message fields
are supplied in positive fixtures. No invented citation fields are added to provider JSON.
Only metadata.display proposes source locations; output remains opaque, and summary text
never supplies citations. Tool status alone is not observation proof.

Rules: maximum 8 unique paths, each 1–20 explicit lines, path <=160 characters after
conversion, entire display text used as a nonblank literal anchor <=512 characters,
read part <=16 KiB UTF-8. All-or-empty on malformed/unsupported/duplicate/contradictory
source records; no range or anchor truncation. Preview must equal the short display text,
text line count must match the explicit range, input path/offset/limit must agree, and
non-truncated display must reach its stated totalLines. Missing envelope IDs, wrong
part correlation, pending/error/compacted state, empty output, contradictory error,
truncation flags, unsupported part/tool or unknown version yield no candidate. Ordinary
text/reasoning/step boundaries carry no source authority. Unknown extra tool metadata,
such as forged binding/verified claims, cannot override application identity.

Path conversion uses the existing trusted root and pure canonical path/containment checks,
without reading a file. It rejects escaping/ambiguous/UNC/URL/drive-relative paths,
dot segments and conflicting input/display locations. Existing reader exclusions,
redaction, UTF-8, reparse checks, 16 KiB evidence-batch limit and post-review currentness
are unchanged. The filesystem TOCTOU limitations in D3 remain.

[Core transient contracts](../../src/MAF.Core/ExternalContracts.cs) carry immutable
UntrustedSourceLocation records on the observation and SourceCandidates on the result.
Both properties are JsonIgnore; the additive OpenCodeReadDisplay origin is a descriptive
label only. [OpenCodeExecutor](../../src/MAF.Workflows.External/OpenCodeExecutor.cs)
assigns SourceBinding from the existing task/run/execution/session/message identities,
never provider-supplied identity claims. Parent validation still independently checks
the original result before evidence acquisition. No fingerprint or dispatch grant changes.

[VerifiedSourceMode](../../src/MAF.Workflows.External/VerifiedSourceMode.cs) has a small
provider-result constructor using those candidates, with **no synthetic callback fallback**.
The old explicitly test-owned callback remains for historical 4C/4D/R1 regression cases.
The 4E acceptance path supplies HTTP response bytes, the existing source reader and actual
NativeEvidenceReviewer only; there is no separately supplied candidate list or simulated
positive reviewer. Neither BoundedSourceReader nor NativeEvidenceReviewer was changed.

## Executed integration evidence

[SourceCandidateIntegrationTests](../../tests/MAF.Tests/SourceCandidateIntegrationTests.cs)
runs both supported version labels, one/two sources and contained absolute/relative paths.
The provider fixture emits an earlier completed read-tool message and final correlated
assistant completion. The guarded client extracts proposals, the original executor binds
identities, 4C independently reads real local files, official native Reviewer tools return
actual source lines, R1 confirms SDK tool delivery, and HumanGate is ReadyForHumanReview.
All required references must be observed: reading only A while citing A/B remains
InsufficientEvidence. There is no Accepted or human consent.

Actual source tool evidence includes:

```text
source-v1:B3AA049369A5632445B7AB3C1294868E51D277FB77BE884BF1A50FD0C3337A7F Example.cs:120: return 42;
source-v1:B3AA049369A5632445B7AB3C1294868E51D277FB77BE884BF1A50FD0C3337A7F Example.cs:121: // end
source-v1:A7EC6BE58FA0CA83BB4C1601AA3F5704B18C06D7CEAC978C354853F172047DCD Second.cs:120: return 99;
source-v1:A7EC6BE58FA0CA83BB4C1601AA3F5704B18C06D7CEAC978C354853F172047DCD Second.cs:121: // end
```

Task=runtime-task, WorkflowRun=runtime-run, Execution=runtime-execution,
Session=ses_runtime, submitted Message=msg_runtime. Provider tool message=msg_tools;
final assistant=msg_reply. Those are distinct identities. SQLite stays Completed with
Verified original receipt and original request fingerprint. Candidate data/anchors are
absent from execution snapshot and result JSON; JSON roundtrip restores no candidates.
SQLite integrity_check returns ok.

The pipeline performs one acquisition and one post-review source read per admitted file.
The positive test additionally calls IsCurrent once per source (three total reader attempts
per file including that assertion); native tools themselves never read the filesystem.
PCA client calls=1, Reviewer client calls=2, old simulated review calls=0.
Outer executors: PCA / ExternalDelegation / EvidenceReviewer / HumanGate, with typed
BriefHandoff / ExternalHandoff / ReviewHandoff / WorkflowOutcome completions and exactly
one WorkflowOutputEvent. Inner events remain WorkflowStartedEvent, SuperStepStartedEvent,
ExecutorInvokedEvent, ExecutorCompletedEvent, WorkflowOutputEvent, SuperStepCompletedEvent.

Comparable happy path **Create / Prompt / GET / Abort = 1 / 1 / 9 / 0**.
Fresh independent Inspect re-extracts current proposals at **0 / 0 / 9 / 0**; subsequent
GETs lacking source metadata yield InsufficientEvidence instead of restoring old candidates.
No observation metadata is durable execution-time source evidence and no outer Workflow
checkpoint/resume is implemented. New-task wrong-parent ambiguity retains existing bounded
polling (up to 200 observations) and ends Unknown without Abort; it is not forced to one
poll. Pagination uses existing bounds (repeated-page fixture: 10 GET, Partial, no candidate),
not a citation-specific request. Denied admission produces zero HTTP and zero source reads.

## Negative evidence and safety limits

Cases cover unstructured convincing summary citations; no source data; unknown source/tool;
failed/incomplete/empty/compacted tool output; part/message/session/parent mismatch or missing
IDs; absolute escape/traversal/UNC/URL/drive-relative and inconsistent paths; missing file;
incorrect/missing/large ranges, offset and anchor; oversized metadata/list; preview mismatch;
truncated flags/pages; duplicate candidates and JSON properties; malformed field types;
redacted/changed/locked local sources; unknown version; Unknown/Failed/Cancelled execution;
denied admission; partial native observation; summary and source prompt-injection text;
forged provider identity/verified metadata; and fresh Inspect without extra POST.
Invalid metadata leaves genuinely Completed execution intact and never calls the reader
or model. Valid-looking but stale/fabricated anchors/ranges are rejected independently by
4C, not accepted because a tool completed. Source changes during the Agent remain blocked
by unchanged post-review revalidation. Injection tests retain only the two admitted snapshot
tools and unchanged graph/effects; they do not establish real-model injection robustness.

## Verification and delivery boundary

Final focused results: 315 discovered / 315 passed / 0 failed / 0 skipped (19s).
New 4E cases: 59. Full solution build succeeded: 0 warnings / 0 errors.
Full offline: **1265 discovered / 1265 passed / 0 failed / 0 skipped (2m8s)**. Exact TRX test-name comparison retains all **1206 historical cases**, with zero removed/renamed cases and exactly 59 additions, all SourceCandidateIntegrationTests. No historical assertions were weakened.

Reproduction:

```powershell
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-restore --filter 'FullyQualifiedName~SourceCandidateIntegrationTests|FullyQualifiedName~OpenCodeClientTests|FullyQualifiedName~ProtectedHandoffTests|FullyQualifiedName~SourceEvidenceTests|FullyQualifiedName~NativeEvidenceReviewerTests|FullyQualifiedName~OpenCodeRecoveryTests|FullyQualifiedName~TypedHandoffTests' --logger 'trx;LogFileName=phase2b4-e-focused.trx' --results-directory TestResults/home-validation
dotnet build MAF.slnx --no-restore
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName!~MAF.Tests.OpenCodeLiveTests' --logger 'trx;LogFileName=phase2b4-e-offline.trx' --results-directory TestResults/home-validation
```

The executed integration is real SDK Workflow/Agent, guarded adapter, SQLite and local
source reading, with fake provider HTTP, scripted models and synthetic trust. Official
versioned source/schema inspection supports the shape; it is **not live-provider
compatibility or real-model semantic accuracy proof**. Older/missing display, larger/truncated
reads, other versions or tool types have no fallback. This intentionally narrow path may
produce InsufficientEvidence for otherwise valid OpenCode sessions.

Changed files: Core ExternalContracts/SourceEvidence; external OpenCodeClient/OpenCodeReadSources/OpenCodeExecutor/VerifiedSourceMode; SourceCandidateIntegrationTests; README, Roadmap, execution plan and this report (11 files).

README/Roadmap/plan now identify 4D/R1 as owner-reviewed controlled milestones and 4E as
current; historical acceptance reports/counts and D1/D2/D3 are preserved. Production
identity/M3-C, Host activation, human Approval UI, provider permissions, Registry/Hybrid,
new schema, persistence of source evidence and outer durable resume remain outside scope. Relative-link validation across the four changed documents checked 92 local targets with zero missing files; historical acceptance reports and protected trust/SQLite/Host code remain unchanged. Unrelated untracked `.vscode/` is excluded from the commit.
4E adds fresh-Inspect tests rather than a new source-specific OS-kill scenario; historical real-process crash regressions remain passing. Stop after 4E and return for owner architecture review; no 4F or activation is authorized.
