# Phase 2B-4F — source evidence coverage calibration

Date: 2026-10-18. Baseline: `f7dc9e855865d4fd6abfee67e9213726f69c5e30`.
P0 source review completed before runtime edits. Controlled technical validation passed; owner review pending.

## P0 source grounding and evidence categories

Official pinned [1.18.35 ReadTool](https://github.com/anomalyco/opencode/blob/v1.18.35/packages/opencode/src/tool/read.ts),
[1.19.0 ReadTool](https://github.com/anomalyco/opencode/blob/v1.19.0/packages/opencode/src/tool/read.ts),
[grep](https://github.com/anomalyco/opencode/blob/v1.19.0/packages/opencode/src/tool/grep.ts),
[glob](https://github.com/anomalyco/opencode/blob/v1.19.0/packages/opencode/src/tool/glob.ts) and
[message hydration](https://github.com/anomalyco/opencode/blob/v1.19.0/packages/opencode/src/session/message-v2.ts)
were inspected. Public downloads are ignored under TestResults/phase2b4-f-p0. Both tags
have identical respective UTF-8 source hashes:

| Tool | SHA256 |
| --- | --- |
| read | AFEC8294965CBD9B9E29D3453B682164F0ECDBA26574EBC048E9BA21AF209C68 |
| grep | F7BBAB8AE3FABFE3DD78B5728A283A56BFF318D5B1F278D15D1C637227FF660F |
| glob | A5069377AE916495A72BE319D218878136FC13F18551F3E16981D8C692C68A4A |

ReadTool returns first-20-line preview, explicit display range/text, and truncation flags
for omitted lines/byte cap. Its default is 2000 lines; individual lines may be clipped at
2000 characters even without the aggregate truncation flag. In byte-cap cases totalLines
can describe lines scanned before stopping, not a proven whole-file total. Message
hydration restores envelope IDs while retaining stored metadata. No live provider/model
was launched. All new positives are **source-derived offline fixtures**, not captured
live execution. No newly authorized genuine sanitized source-tool traces were supplied;
genuine-provider coverage count is zero. Contradictory/security variants are synthetic negatives.

## P0 compatibility matrix (baseline outcomes to be measured by regression)

| Scenario / grounding | Expected eligibility | 4E behavior / reason | Safety necessity / smallest correction |
| --- | --- | --- | --- |
| Short complete read / read display | Yes | Eligible | Preserve |
| 40-line complete read / preview first20 | Bounded excerpt | Rejects range/text/preview bounds | Full-range proof unnecessary; validate full metadata then fixed prefix <=20 lines/512 chars |
| Middle read in longer file / offset+limit | Bounded excerpt | Rejects truncation/end!=total | Preserve omissions as limitation, never claim unseen lines |
| 100-line default-limit read / default2000 | Bounded excerpt | Rejects >20/text/preview | Same fixed prefix correction |
| 2000-line large default read / read limits | No under retained part budget | Rejects >16KiB part | Retain finite envelope budget; no expansion to force success |
| Read then grep / grep metadata matches | Read only | Rejects unrelated tool | Ignore only narrowly validated, non-source completed grep |
| Glob then read / glob metadata count | Read only | Rejects unrelated tool | Ignore only narrowly validated, non-source completed glob |
| Repeated identical read / distinct part/call IDs | One candidate | Rejects duplicate path | Bounded exact full-observation dedup; no conflict merging |
| Conflicting repeated reads / synthetic negative | No | Rejects duplicate path | Retain fail closed, including differences outside chosen prefix |
| Two files / independent read displays | Yes | Eligible | Preserve both required observations |
| Internally consistent truncated read / read flags | Bounded excerpt | Rejects all truncated reads | Verify only supplied excerpt; preserve provider-truncated limitation |
| Oversized read part / bounded contract | No | Rejects envelope cap | Retain |
| Very long first line / per-line clipping | No | Rejects >512 anchor | Retain; do not skip to a favorable later line |
| 20 longer lines / bounded anchor | Bounded prefix | Rejects >512 full text | Whole-line fixed prefix only, no partial-line anchor |
| Source changes before review / synthetic negative | Candidate only | Reader rejects mismatch | Preserve independent source verification |

P0 justifies a small provider-specific correction, not a new evidence framework. The
16KiB part limit, 8 independent source candidates, existing path rules, source-reader
budgets, message collection truncation rejection, native Reviewer/R1 and D1–D3 remain.

## Measured before/after matrix

The same 15 named scenarios run for both version labels (30 cases). Initial red-phase
TRX recorded 14 passes / 16 expected new-behavior failures, all eight false-negative
scenarios twice. No historical tests failed or were altered in that characterization.
Before artifacts: TestResults/home-validation/phase2b4-f-before.trx. Final focused artifacts:
TestResults/home-validation/phase2b4-f-focused.trx. These ignored TRX outputs record
extracted count, independently admitted count, model calls, assessment and raw effects.

| Scenario | Before eligible | After eligible | After independent verification / native observation | Main boundary |
| --- | --- | --- | --- | --- |
| short | Yes | Yes | Complete / complete | Original 5-line positive retained |
| over20 | No | Yes | Complete for selected 1–20 / complete | Display 1–40; rest explicitly unverified |
| middle | No | Yes | Complete for selected 30–39 / complete | Provider truncated; unseen lines unverified |
| default100 | No | Yes | Complete for selected 1–20 / complete | Default-limit read of 100 lines; excerpt only |
| default2000 | No | No | Not attempted | Retained 16KiB tool-part cap |
| read-grep | No | Yes | Read source only / complete | Grep contributes no source reference |
| glob-read | No | Yes | Read source only / complete | Valid root glob may have empty title |
| repeat | No | Yes, one candidate | Complete / complete | Distinct part/call IDs, identical entire display |
| conflict | No | No | Not attempted | No favorable candidate selection |
| two | Yes | Yes, two candidates | Both complete / both complete | Original multi-source behavior retained |
| truncated | No | Yes | Complete for selected 1–10 / complete | Both provider flags remain disclosed true |
| oversized | No | No | Not attempted | 80 long lines, retained envelope cap |
| long-line | No | No | Not attempted | First provider line is clipped and exceeds anchor budget; no skipping |
| anchor-budget | No | Yes | Complete for selected 1–8 / complete | Eight whole 63-character lines + newlines = 511 chars |
| changed | Yes | Yes | Rejected / not invoked | Local source changed after provider observation, before review |

Both versions produced identical outcomes. **Eligible cases: 6 → 22 of these 30 named
fixtures; independently verified cases: 4 → 20; complete native observations: 4 → 20.**
These are counts of constructed scenario/version cases, not a statistical live-provider
acceptance rate. The two changed-source cases deliberately remain ineligible for successful
assessment despite valid extracted metadata. Four retained rejection scenarios account
for eight ineligible version cases. Eight newly supported scenario types account for
16 newly eligible cases. The initial oversize characterization used a 2000-line record;
the final oversized fixture separately uses 80 long lines, exercising the same retained
part-size rejection instead of duplicating the default2000 input.

Path findings: ordinary contained absolute/relative spellings remain accepted by the
unchanged 4E path checks. Dot-segment aliases, UNC/URL/drive-relative/escaping paths,
input/display mismatch and long paths retain that conservative profile. Some provider-
resolvable dot-relative aliases may still be false negatives (code-inspection limitation,
not counted as measured coverage); 4F does not change the approved path policy.

## Small correction and retained safety

[OpenCodeReadSources](../../src/MAF.Workflows.External/OpenCode/OpenCodeReadSources.cs)
still accepts only the pinned versions and original session/parent/message/part envelope.
It validates the ENTIRE supplied display before selecting anything: exact line count,
preview equal to its first 20 lines, agreeing Boolean truncation flags, input/range/path,
full-content redaction and existing JSON/type/duplicate checks. Nontruncated displays
require end==total; truncated displays require stated scanned/total count beyond the
last displayed line. No claim is made about an unseen physical file tail.

Selection is deterministic: longest prefix of complete supplied lines fitting BOTH
20 lines and 512 characters including newlines. Original start is unchanged; selected
end=start+selectedCount-1. No arbitrary source summary, local read, omitted line, partial
line or later favorable line is used to manufacture an anchor. A first line over budget
rejects the batch. Full-range contradictions still reject before or during dedup.

[UntrustedSourceLocation](../../src/MAF.Core/SourceEvidence.cs) adds one optional transient
CoverageLimitation. [OpenCodeExecutor](../../src/MAF.Workflows.External/OpenCodeExecutor.cs)
forwards the adapter-generated bounded disclosure into result limitations and therefore
HumanGate limitations, for example: `Source excerpt 1-20 only; provider display 1-40,
provider-truncated=false. Other lines remain unverified.` Truncated provider reads retain
`provider-truncated=true`. A bounded excerpt is not labeled a complete verified provider
read. Existing JSON-ignored candidate/location transport and SQLite snapshots stay unchanged.

Only exact completed grep/glob non-source shapes may be ignored: original envelope,
finite input pattern/optional contained path, known metadata keys (matches or count and
truncated), count 0–100 consistent with its flag, no attachments, no error/compaction or
unknown source-bearing metadata. These records are not executed, parsed into citations
or used as source proof. Unknown/suspicious/malformed tools still reject the whole batch.
The empty glob title at the workspace root is supported; read titles still require text.

Repeated reads deduplicate only with distinct part IDs/call identities and identical
canonical path, original display start/end, stated count, truncation and ENTIRE text.
Differences beyond the selected prefix or different ranges reject the whole batch.
Cloned identity/JSON duplicates still reject. Total tools are capped at 16 per extraction,
unique source candidates at 8. No unbounded dedup, merging or generic tool registry exists.
The 16KiB part cap, original HTTP/message bounds, 16KiB independently admitted batch,
reader path/security rules and native Reviewer/R1 remain unchanged.

## Actual execution and additional negative evidence

[Coverage tests](../../tests/MAF.Tests/SourceEvidenceCoverageTests.cs) use source-derived
fake HTTP responses, the real protected OpenCode client, actual SQLite v4, existing
four-executor outer/inner official Workflows and actual native PCA/Reviewer Agents with
scripted IChatClient. Every newly eligible matrix case independently reads its fixture
source, revalidates after the Agent, receives real SDK Tool-role evidence at the correct
original line numbers and passes unchanged R1 delivery tracking. Gate is at most
ReadyForHumanReview; no human approval, semantic correctness or provider-wide source proof.

The 42 new tests comprise 30 matrix cases, 10 additional non-source/conflict cases and
two partial-observation/Inspect cases. Extra negatives include conflict outside the chosen
prefix, different repeated ranges, invalid grep metadata types/flags/path/status/identity,
source-bearing fields smuggled into grep, unknown tool and grep-only results. They produce
no source read/model call and preserve Completed external execution. Two independently
verified 40-line displays each supply a 20-line excerpt; a Reviewer reading only the first
still returns InsufficientEvidence through R1. Fresh Inspect preserves disclosure and
identity; missing candidates never reconstruct from stored summaries or JSON.

Comparable healthy raw **Create / Prompt / GET / Abort = 1 / 1 / 9 / 0**.
Fresh independent Inspect = **0 / 0 / 9 / 0**. No extra source endpoint, request, retry or
POST. Existing source-change, denied admission, non-Completed, permission, root/redaction,
identity, pagination, injection and crash/recovery assertions remain historical regressions.
All 59 existing 4E tests pass unchanged, including former negative fixtures: their invalid
preview/range/flags, oversized envelope or cloned identity remains genuinely invalid.

## Validation, limitations and next action

Final focused run: **357 discovered / 357 passed / 0 failed / 0 skipped (22s)**.
Solution build: success, **0 warnings / 0 errors** (1.55s).
Full offline: **1307 discovered / 1307 passed / 0 failed / 0 skipped (2m 10s)**.
Historical 4E baseline: 1265 cases retained, with 42 new coverage cases.

```powershell
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-restore --filter 'FullyQualifiedName~SourceEvidenceCoverageTests|FullyQualifiedName~SourceCandidateIntegrationTests|FullyQualifiedName~SourceEvidenceTests|FullyQualifiedName~NativeEvidenceReviewerTests|FullyQualifiedName~ProtectedHandoffTests|FullyQualifiedName~OpenCodeClientTests|FullyQualifiedName~OpenCodeRecoveryTests|FullyQualifiedName~TypedHandoffTests' --logger 'trx;LogFileName=phase2b4-f-focused.trx' --results-directory TestResults/home-validation
dotnet build MAF.slnx --no-restore
dotnet test tests/MAF.Tests/MAF.Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName!~MAF.Tests.OpenCodeLiveTests' --logger 'trx;LogFileName=phase2b4-f-offline.trx' --results-directory TestResults/home-validation
```

No live-provider execution, real-model reasoning evaluation or newly authorized genuine
provider trace was used. Large default-limit reads above the retained envelope budget,
very long leading lines, conflicting/different-range repeats, stricter path aliases and
unsupported versions/tools remain limitations. Source-derived fixtures establish bounded
plumbing/coverage, not measured real-world success. D1 persistence/rehydration, D2 semantic
quality and D3 source snapshot isolation remain deferred. No new SQLite, dependency,
authority, Host activation, Workflow graph, provider dispatch or human approval code.

Smallest justified next action: owner review of excerpt disclosure, ignored-tool and exact-
dedup semantics and retained coverage limits. If practical use requires larger default
reads or path aliases, separately authorize focused calibration with the same independent
reader/R1 and finite budgets, rather than production activation or another infrastructure
subsystem. Stop after 4F; no automatic 2C/Hybrid/activation or next phase.
