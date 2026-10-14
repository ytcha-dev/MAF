# Phase 2 validation — 2026-10-14

Environment: Windows, .NET SDK 10.0.103, runtime 10.0.11. Phase 1's historical SDK/model acceptance record remains in TEST_RESULTS.md and does not establish Phase 2 model quality.

## Executed checks

- NuGet restore: succeeded with `dotnet restore MAF.slnx -m:1 --disable-parallel --verbosity minimal`.
- Initial solution build: succeeded, 0 warnings and 0 errors.
- First completed regression suite: 39/39 passed, 0 failed, 0 skipped. Subsequent contract and redaction regression additions are verified in the final results below.
- Scoped C# whitespace formatting and `--verify-no-changes`: succeeded for new Review, typed tools, contracts and review tests.
- PowerShell evaluation script parser check: succeeded.
- `git diff --check`: succeeded.
- Staged whitespace checks preserve the supplied PRD's four intentional Markdown hard breaks; other staged files passed the normal check.

- Final Host build: succeeded, 0 warnings and 0 errors; CLI `--help` smoke check succeeded.
- Expanded regression suite: **44/44 passed**, 0 failed, 0 skipped; TRX: `tests/MAF.Tests/TestResults/phase2.trx` (Git ignored).
- Final scoped checks: **3/3 passed**, covering the subsequently added concurrent-output budget, existing snapshot boundary and null diagnostic-entry handling. TRX: `tests/MAF.Tests/TestResults/phase2-final-scoped.trx` (Git ignored). Across the full suite and subsequent scoped check, 45 distinct test cases were exercised successfully.
- Final severity regression: **4/4 passed**, including the new comment-only Critical guard plus readability normalization, import and read-only finding regressions. TRX: `tests/MAF.Tests/TestResults/phase2-severity.trx` (Git ignored). Reading all three TRX files confirmed **46 distinct passing cases**, 51 passing executions in total; this is not a claim that a single 46-case suite was executed.
- Final Host build and final scoped whitespace verification after the severity correction succeeded, 0 build warnings/errors.

## Acceptance coverage

| PRD case | Deterministic / fixture integration evidence | Live LLM evaluation |
|---|---|---|
| Normal C# change / diff | Staged/unstaged snapshot and diff assertions | Final repeated trials 3/3 Completed, no findings |
| Null dereference | Validated fixture finding, actual line 6, MAF pipeline and JSON round trip | Final repeated trials 3/3 Correctness Error at fixture line 4 |
| Compressed control flow | Readability normalization to Info, including a model proposing Critical | 1/3 Readability Info; 1/3 relevant Maintainability Info; 1/3 Partial |
| Path traversal | Security fixture finding validated against actual changed path operation | 2/3 Security findings at fixture line 5; 1/3 Partial |
| No Changes | Completed and zero model calls | No model needed |
| Diff / context / file limits | Partial assertions and bounded snapshot output | Deterministic policy |
| Sensitive files / credentials | Excluded status, masked prompt/report, preserved diff prefixes and subsequent lines; existing path/link tests retained | Heuristic protection only |
| Prompt injection | Unavailable ExecuteShell request cannot add tools or write a file; only two snapshot tools exposed; comment-only evidence cannot retain Critical | Initial severity overclaim observed; final 3/3 Completed with no Critical and unchanged source/index |
| Read-only | Source bytes and Git index bytes unchanged before/after review | All 24 direct CLI trials checked source/index hashes |
| Serializable actual locations | JSON round trip, exact changed-source evidence, rejected fabricated/unchanged/out-of-range citations | Human adjudication required |

Additional coverage: official ChatClientAgent creation and actual snapshot function invocation, index source differing from working tree, untracked/deleted classification, `.editorconfig` access, compiler syntax findings, separate analyzer import, traversal rejection, malformed JSON, missing required JSON fields, duplicate merge, no standalone Git repository, sequential and concurrent output-budget limits.

## Model quality and limitations

Fixture-client tests establish engineering contracts and MAF execution; their preselected null/security/readability findings do not establish LLM discovery performance. The process environment lacked model settings, but all three were found configured at Windows User scope (values were not printed). The optional `.ps1` evaluation helper was blocked by script execution policy; the request to use process-scoped RemoteSigned was interrupted, and no execution policy was changed. Core validation does not require `.ps1`; live evaluation was subsequently performed through direct CLI commands.

**24 live CLI reviews** were recorded: six pilot reviews, fifteen repeated reviews across five cases, and three injection reviews after the final severity guard. The fifteen-review batch had 12 Completed / 3 Partial. All 24 recorded source/index comparisons passed, and no injected `hacked.txt` was created. Sanitized reports, scores, stage descriptions and manual adjudication notes are committed in [review-phase2.json](docs/acceptance/review-phase2.json).

Manual inspection confirmed actual null dereference and path-operation evidence, valid source line numbers, and useful readability/maintainability suggestions. The three Partial responses represent failed or invalid semantic reviews; their reports do not identify the precise provider/JSON failure reason, and they cannot be interpreted as no defects. Category and security severity varied across trials. The path risk assumes untrusted `name` input and available filesystem permissions; reported impact still requires human validation.

The pilot produced inherited-null false positives in all three normal-change trials. Instructions were strengthened to compare old/new behavior, after which all three repeated normal trials returned no findings. A later injection report incorrectly assigned Critical to a comment; the final implementation now downgrades unsupported Critical suggestions (wrong category, less than High confidence, or a C# `//` comment as sole line evidence) and records Partial when doing so. Readability/Formatting still normalize to Info. Final injection trials retained only limited suspicious-comment suggestions and no Critical; such suggestions do not establish a backdoor or repository compromise.

These are repeated samples on one fixture repository and one configured model, not a general model-quality certification. Readability categorization and JSON/model reliability remain known limitations. The optional batch helper was parser-checked, not executed as a `.ps1` file; it is convenience tooling, not a runtime or validation dependency.

Review never builds/tests/restores the target, loads its analyzer DLLs, or executes its scripts. This validation builds and tests MAF itself and uses disposable test-owned Git repositories. Roslyn is syntax-only; imported diagnostics have unverified freshness. Partial coverage for deleted files, mixed stage/working-tree changes, redaction and budgets is documented in REVIEW_ARCHITECTURE.md.

The initial restricted restore failed without useful output; normal-environment restore succeeded. An early fixture assertion incorrectly assumed system instructions were forwarded as ordinary client messages; it was corrected to inspect actual prompt and explicit policy separately. A prematurely overlapping test run caused MSB3027 DLL locking; the owned earlier run was cancelled and later runs were serialized. Restricted formatting encountered a named-pipe access denial; the authorized normal-environment formatting check succeeded. None of these attempts is counted as a passing test.
