# Phase 2 Review Agent MVP architecture

> "Phase 2" is the historical Code Review stage name. The independent Review MVP is implemented; its [acceptance record](../../TEST_RESULTS_PHASE2.md) remains the evidence source. [Roadmap v0.2](../ROADMAP.md) groups PCA/Review as the native Phase 1 baseline; automatic cross-agent orchestration is still planned for Phase 2B-4, not implemented by this service.

## Existing architecture and reuse

Inspection confirmed .NET 10, Microsoft.Agents.AI 1.24.0, official `ChatClientAgent`, `AIAgent.RunAsync`, and `CreateSessionAsync`. The implementation retains those APIs and the existing Host / DI / OpenAI-compatible `IChatClient`; no second host or agent loop was introduced. Official API reference: [ChatClientAgent](https://learn.microsoft.com/en-us/dotnet/api/microsoft.agents.ai.chatclientagent?view=agent-framework-dotnet-latest).

`ProjectTools` moved from Comprehension to `MAF.Core.Tools`. Both agents use this single filesystem/Git/redaction implementation; existing Comprehension tests now import the shared namespace. Core still has no reference to either agent. Roslyn `Microsoft.CodeAnalysis.CSharp` 5.0.0 is the one additional analysis dependency, used solely for syntax parsing; it does not execute target analyzers or evaluate MSBuild.

| Component | Responsibility |
|---|---|
| Core `ReviewRequest`, `ReviewResult`, `ReviewFinding`, `IReviewService` | Orchestrator-facing input, structured output and async invocation |
| Core `ProjectTools`, typed review methods | Fixed read-only Git arguments, safe paths, bounded source reads, redaction, excluded-change accounting |
| Review `ReviewService` | Discovery → bounded context → deterministic diagnostics → semantic agent → validated report |
| Review `ReviewContext` | Per-run sanitized, numbered snapshot; bounded read/search tools with no filesystem authority |
| Review `ReviewAgentFactory`, `ReviewInstructions` | Independent official MAF Agent, policy and tools |
| Host `--review` | Existing model setup / DI and one-shot JSON output |
| `ReviewTests` | Deterministic contracts and official MAF integration using a fixture client |
| `evaluate-review.ps1` | Repeated live-model evaluation, retained reports and manual adjudication |

## Lifecycle and contracts

`IReviewService.ReviewAsync(ReviewRequest, CancellationToken)` owns each run. Request includes project root, All/Staged/Unstaged scope, focus, character budget, file count and optional project-relative existing-diagnostics JSON. Per request it constructs ProjectTools, discovers changes, admits context, builds one MAF agent and a fresh official Session. It neither reimplements tool calling nor persists sessions.

Results expose Completed/Partial/Failed, summary, scope, validated findings, files actually acquired, evidence sources, limitations, unperformed checks and suggested human actions. No Changes skips LLM entirely. Discovery failure is Failed; limited coverage or LLM/JSON failure after successful discovery is Partial. Deterministic findings survive semantic failures. Enum strings and camelCase JSON are shared through `ReviewResult.JsonOptions`.

Only supplied, sanitized changed source is accepted as finding evidence. File names must exactly match admitted files, line numbers must be valid changed new-source lines, and the evidence must be an exact nonempty excerpt on that line. Invalid or unchanged-line findings are rejected with a Partial limitation. Finding IDs are server-assigned; identical file/line/category/evidence findings merge. Model findings cannot masquerade as Compiler/Analyzer/Formatting diagnostics. Readability and Formatting normalize to Info. Critical requires Security/Correctness, High confidence and more than a C# `//` comment as the sole line evidence; unsupported Critical suggestions downgrade to Warning and flag Partial. This is a conservative guard, not proof of the model's impact claim; semantic severity judgments still require human review.

## Tool boundary and risk

The model receives only `ReadContext` and `SearchContext`, both acting on a sanitized in-memory snapshot and a shared output budget. It cannot pass a new root, read an arbitrary path, request a shell command, build/test/restore, Git mutation, or fix source. Repository text never registers tools or modifies access policy; instructions explicitly treat it as untrusted data. Tests request an unavailable shell tool and assert the available tools and unchanged target/index. This verifies authority boundaries, not a universal guarantee that every model ignores injection in its prose.

Discovery includes excluded changes as anonymous exclusions, so sensitive/binary/oversized/link paths cannot silently produce a clean comprehensive report. Paths and content use the existing ProjectTools policy. `.editorconfig` is newly admitted as a specific readable filename. The existing credential filter now preserves diff prefixes on redacted assignment lines, retaining subsequent hunk locations. Git has fixed ArgumentList commands, disabled external diff/textconv/hooks/fsmonitor and inherited Git overrides removed. No target MSBuild, analyzer binary or project script is loaded.

Reports and model input are redacted; redaction is heuristic and cannot identify every secret or prevent an LLM from reconstructing unknown sensitive data. Local filesystem checks still cannot defeat a hostile concurrent writer replacing files between checks. These tools are not an OS sandbox. Git requires an ordinary `.git` directory at the specified root; linked worktrees/submodules remain unsupported.

## Intentional MVP limits

- Syntax diagnostics are not full compilation, nullability analysis, Roslyn analyzer execution, or formatting verification. `.editorconfig` is available as review context; rules are not executed as a formatting engine.
- Existing diagnostics import accepts `ReviewFinding[]`, validates changed-source evidence, separates diagnostic categories and records unverified freshness. No automatic build/test/restore authorization mode exists.
- Context uses filename and bounded declared-type searches, not a full symbol-resolved call graph. File/character/search/context truncation is reported. Budgets count characters, not exact model tokens.
- Staged source uses the index. All uses working-tree source and flags mixed staged/unstaged files as Partial. Deleted diffs are available but cannot validate current-source finding locations, also Partial.
- Redacted C# is excluded from syntax parsing to avoid diagnostics caused by masking. Semantic evidence must refer to an unredacted admitted changed line.
- If the provider rejects tools or returns malformed JSON, no semantic success is claimed; Partial retains deterministic evidence.
- Live LLM quality must be assessed repeatedly and manually. Fixture-client findings demonstrate report validation and MAF integration, not independent defect discovery.

## Future Orchestrator integration

Register `IReviewService` with `ReviewService` in the existing DI host, supply an authorized project root and bounded request, and consume `ReviewResult` directly. An Orchestrator can route Failed/Partial to a human, display findings with source evidence, or request a separate Staged review. Any later build/test execution would need a separately designed explicit authorization and isolation boundary; the MVP exposes no such tools. Multi-agent negotiation, PR integration and automatic code modification remain outside this phase.
