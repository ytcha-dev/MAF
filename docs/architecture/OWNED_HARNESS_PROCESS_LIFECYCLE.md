# Owned Harness Process Lifecycle — 2026-10-22

Status: implemented for static review only. Build, all new tests and existing regression
are **NotRun** under the corporate execution hold. This is not validated acceptance.
No live Harness, public Host execution, socket or provider/model calls are enabled.

## Ownership and scope

`MAF.Host/OwnedHarnessProcess.cs` is a small internal independently testable primitive.
It is not registered in DI, a route, or PersonalLabOfflineRuntime. Calling it does not
grant authorization: a future approved composition must select and approve the actual
binary, arguments, environment, working directory and shutdown protocol before calling Start.
Executable metadata is not binary integrity verification, a sandbox or script-content inspection.

Start validates explicit fully qualified existing executable and directory paths, arguments,
environment and finite positive shutdown deadlines (at most one minute each). It uses
UseShellExecute=false and ArgumentList, rejects known shells/script extensions, clears the
inherited environment and adds only caller-selected values. There is no PATH lookup,
installation, fallback, PID attachment or process-name lookup. Explicit environment values
are trusted caller input, not a general environment-security policy; approval must consider
credentials, loader hooks, HOME/config locations and any executable's downstream tools.

Start is synchronous. Native start failure is propagated; cancelled input is checked before
the process-creation commit point. Cancellation racing after that check can still result in
a successfully returned owner. There is no cancellable OS process-creation API here.
SurvivedStartupAsync waits a bounded survival interval: false means early exit, even with
exit code zero; true proves only survival, not readiness. Cancelling an observation does
not abandon or kill the child. Retain the returned owner and explicitly stop it.

The owner retains the original Process object (and its Windows creation handle). Exit
observation polls HasExited at 20 ms intervals and records the exit code independently
of stream EOF. There is no PID reacquisition. Running, Exited and Unresolved observation
are separate from shutdown outcomes and resource disposal. Observation faults remain
Unresolved and prevent disposal; callers must retain the owner for explicit resolution.
An exited local process never proves an OpenCode Session is terminal or a task succeeded.

## Shutdown and resources

Concurrent StopAsync calls share one result/task and never retry termination. Stop returns
AlreadyExited, GracefulExit, Terminated, TerminationTimedOut or Unresolved. GracefulExit means the process exited
during the grace interval, not proof that it honored a protocol or completed a session.
There is no universal graceful console-process shutdown API. Explicit opt-in closes stdin
without writing a command, for reviewed EOF-aware children only. Otherwise the grace interval
allows normal completion. No control signals, CloseMainWindow, Ctrl+C or protocol Abort is sent.

After the grace interval, Windows uses Kill(false) on the retained original Process; after
a further bounded wait, absence of observed exit is TerminationTimedOut (ownership remains
unresolved and disposal is rejected). Linux/other Unix systems
retain cooperative EOF/normal exit but intentionally do not force-kill: built-in PID-based
termination does not supply the atomic original-child identity guarantee required here.
Unsupported termination or OS errors return Unresolved. This is the safe subset for this
increment, not an assertion that .NET Unix process termination is generally unusable.
Native Start/Close/Kill/Dispose calls are synchronous OS APIs; deadlines bound asynchronous
waits, not a hard realtime guarantee against a stalled OS API or thread scheduling.

Dispose rejects active shutdown or an unobserved/unresolved exit. It never silently detaches
or kills. Once exit is observed, it attempts each owned resource release once; repeated
disposal does not re-run it. DisposeAsync joins stop, requires resolved exit, disposes,
and bounds its wait on ResourceCleanup. Cleanup failure/timeout is observable and does not
prove full release; retain the owner and inspect ResourceCleanup. A cached timeout/Unresolved stop
result is not rewritten after eventual exit; WaitForExitAsync/Observe can still show that exit.

Both output streams are drained immediately with independent fixed 4096-byte buffers and
discarded, retaining no output text or credentials. No line or ReadToEnd accumulation exists
in the component. IO/disposed-stream errors end that drain; they are not task/session success
evidence. The normal child could then block on output, handled by bounded stop rather than
unlimited buffering. Exit is independent of descendants retaining inherited pipe handles;
cleanup cancels reads and closes our pipe ends, and ResourceCleanup observes the drain tasks.
No descendants are enumerated, adopted or terminated. Remaining descendants are outside this
owner's guarantee on both Windows and Linux; no job object, process group or subreaper exists.

## Reuse and future integration

Existing PersonalLabOfflineRuntime retains task cancellation/drain/transport ownership.
LabOpenCodeExecutionPlan remains a configuration description, not an operational factory.
Existing ProjectTools Git execution and acceptance Child helpers were reviewed: their PATH
lookup, output accumulation or tree-kill behavior do not meet this contract, so they are
not reused or changed. Reuse the existing Host assembly, test friendship, test project and
MAF.ApprovalAcceptance runner instead of adding a dependency, assembly or generic supervisor.

Future runtime composition must explicitly own this object, retain it through unresolved
shutdown, and order HTTP/task drain, process shutdown and resource cleanup. That integration,
operational process authorization, loopback preflight, effective configuration/agent checks,
and live ReadOnly execution remain separate milestones. No changes to production trust,
Network denial, admission, journal, receipt, recovery, Unknown/no-retry or existing guards.

## Authored validation (all NotRun)

- `OwnedHarnessProcessTests`: 13 Facts plus 3 Theories / 8 rows (21 intended cases).
  In-memory fakes and existing file/directory metadata only: explicit paths/environment,
  invalid/missing inputs, finite deadlines, pre-launch cancellation, normal and early exit,
  startup survival/wait timeout, cancelled observation preserving ownership, EOF, termination
  timeout/failure, repeated disposal, concurrent stop/dispose and unrelated-child isolation.
- `OwnedHarnessProcessOsTests`: one metadata-only shell rejection Fact, plus five opt-in
  OS process Facts for launch/exit/early exit, EOF shutdown and pipe cleanup, 2 MiB on each
  output stream, original-child-only timeout/termination and existing non-executable failure.
  Windows handle termination and Linux cooperative/unresolved behavior require separate runs.
- No test constructs a model client, HTTP client or provider. The test-only child mode runs
  before the acceptance runner's database/Workflow dispatch and performs no file/network work.
  Existing runtime/configuration/guard tests must still be run later; compatibility is only
  source-reviewed here, not proven by an executed regression.

OS Facts default to skipped unless `MAF_OWNED_PROCESS_OS_TESTS=1`; this environment variable
is an execution opt-in, never clearance. After independent authorization, also supply
`MAF_OWNED_PROCESS_TEST_DOTNET` as the explicit approved absolute dotnet path. Use the existing
built MAF.ApprovalAcceptance output; no PATH search or executable-copy fixtures. The child
has bounded synthetic modes only (exit, EOF, output, five-second linger). OS fixture tests
are tagged Category=OwnedProcessOS. Run focused pure/metadata tests first, then approved OS
cases on Windows/Linux, existing RuntimeLifecycleTests/LabOpenCodeExecutionPlanTests and
guard suites, then full offline regression. No commands were executed for these validations.

No new directories or filesystem fixtures are allocated by the new tests; existing build
output/repository files provide metadata and the test child. Independent process instances
provide isolation. No pre-existing directory cleanup or broad fixture refactor is introduced.
Known PR #3 baseline remains 1624 passed / 0 failed / 3 skipped, build 0 warnings/errors;
this milestone has no executed result to compare with it. Future totals will include five
additional default-skipped OS cases unless independently enabled.
