using System.Diagnostics;
using System.Text.Json;
using MAF.ApprovalAcceptance;
using MAF.Core;
using MAF.Core.Policy;
using MAF.Persistence.Sqlite;
using MAF.Host;
using MAF.Workflows.External;
using MAF.Workflows.External.OpenCode;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace MAF.Tests;

public sealed class RuntimeEnforcementTests(ITestOutputHelper output) : IDisposable
{
    readonly string directory = Directory.CreateTempSubdirectory("maf-protected-").FullName;
    string Db => Path.Combine(directory, "synthetic.db");
    SyntheticRuntime F => new(Db);
    SqliteExecutionStore Executions => new(Db, ExecutionStoreProfile.ExplicitV2);
    async Task Setup(SyntheticRuntime f)
    { await ApprovalSchema.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance); await f.Store().ActivatePolicyAsync(); }
    async Task<object?> Sql(string text)
    {
        await using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Db, Pooling = false }.ToString());
        await c.OpenAsync(); using var cmd = c.CreateCommand(); cmd.CommandText = text; return await cmd.ExecuteScalarAsync();
    }
    static int Count(RuntimeHttpFixture http, string method, string path) => http.Requests.Count(x => x == method + " " + path);
    [Theory]
    [InlineData("--external-task")] [InlineData("--recover")] [InlineData("--recover-pending")]
    public async Task HostDoesNotConstructLegacyRuntimeWithoutProductionTrust(string mode)
    {
        var error = await Assert.ThrowsAsync<ExternalHarnessException>(() => ExternalConsole.RunAsync([mode], directory,
            _ => throw new InvalidOperationException("MustNotReadLegacyConfiguration")));
        Assert.Equal("ProductionTrustUnavailable", error.Code); Assert.False(File.Exists(Db));
    }
    [Fact]
    public async Task ExactAllowUsesOfficialWorkflowAndOneExecutionWithExactMethodPathAccounting()
    {
        var f = F; await Setup(f); using var fake = new RuntimeHttpFixture(f.Root); using var http = new HttpClient(fake);
        var result = await f.Runtime(http).RunAsync(f.Intent, f.Caller);
        Assert.Equal(ExternalExecutionStatus.Completed, result.Result.Status); Assert.NotEmpty(result.WorkflowEvents);
        Assert.Equal("runtime-run", result.Result.WorkflowRunId); Assert.Equal("runtime-execution", result.Result.Handle.ExecutionId);
        Assert.Equal("msg_runtime", result.Result.Handle.ExternalRequestId); Assert.Equal("ses_runtime", result.Result.Handle.ExternalSessionId);
        Assert.Equal(1, Count(fake, "POST", "/session")); Assert.Equal(1, Count(fake, "POST", "/session/ses_runtime/prompt_async"));
        Assert.Equal(2, fake.Requests.Count(x => x.StartsWith("POST "))); Assert.Equal(0, Count(fake, "POST", "/session/ses_runtime/abort"));
        Assert.Equal(1L, await Sql("SELECT count(*) FROM executions")); Assert.Equal(0L, await Sql("SELECT count(*) FROM approval_requests"));
        Assert.Equal(DurableExecutionState.Completed, (await Executions.GetAsync("runtime-task"))!.State);
        output.WriteLine(string.Join("\n", fake.Requests));
    }
    [Theory]
    [InlineData(PolicyOutcome.Deny)] [InlineData(PolicyOutcome.RequireApproval)]
    public async Task NonAllowHasNoExecutionAndZeroHttp(PolicyOutcome outcome)
    {
        var f = F; f.Policy = f.Policy with { Rules = [f.Policy.Rules[0] with { Outcome = outcome, RequiredOperatorScope = outcome == PolicyOutcome.RequireApproval ? "operator:readonly" : null }, f.Policy.Rules[1]] };
        await Setup(f); using var fake = new RuntimeHttpFixture(f.Root); using var http = new HttpClient(fake);
        await Assert.ThrowsAsync<ExternalHarnessException>(() => f.Runtime(http).RunAsync(f.Intent, f.Caller));
        Assert.Empty(fake.Requests); Assert.Null(await Executions.GetAsync("runtime-task"));
    }
    [Fact]
    public async Task SubmitNeverImpliesInspectAndInspectNeverImpliesSubmit()
    {
        var f = F; f.Policy = f.Policy with { Rules = [f.Policy.Rules[0]] }; await Setup(f);
        using var fake = new RuntimeHttpFixture(f.Root); using var http = new HttpClient(fake);
        var result = await f.Runtime(http).RunAsync(f.Intent, f.Caller);
        Assert.Equal(ExternalExecutionStatus.Unknown, result.Result.Status);
        Assert.Equal(1, Count(fake, "POST", "/session/ses_runtime/prompt_async")); Assert.Equal(0, Count(fake, "GET", "/session/status"));
        var before = fake.Requests.Count;
        await Assert.ThrowsAsync<ExternalHarnessException>(() => f.Runtime(http).RunAsync(new(ExternalAdmissionMode.Inspect, f.Request.TaskId, f.Request.Timeout), f.Caller));
        Assert.Equal(before, fake.Requests.Count);
    }
    [Theory]
    [InlineData("identity")] [InlineData("ephemeral")] [InlineData("batch")] [InlineData("cancel")] [InlineData("configuration")] [InlineData("legacy")]
    public async Task UnsupportedOrForgedCompositionCannotFallBack(string variant)
    {
        var f = F; await Setup(f); using var fake = new RuntimeHttpFixture(f.Root); using var http = new HttpClient(fake);
        var runtime = f.Runtime(http, executions: variant == "legacy" ? new SqliteExecutionStore(Db) : null);
        if (variant == "configuration") runtime = new(f.Inputs with { Scope = f.Scope with { ConfigurationRevision = 99 } }, new ImmutablePolicySource(f.Policy), f, Executions, f.Store(), f.Store(), f.Store(), http, f.Options, f.Clock);
        var intent = variant switch
        {
            "ephemeral" => f.Intent with { Mode = ExternalAdmissionMode.EphemeralSubmit },
            "batch" => f.Intent with { Mode = ExternalAdmissionMode.BatchInspect },
            "cancel" => f.Intent with { Mode = ExternalAdmissionMode.Cancel },
            _ => f.Intent
        };
        await Assert.ThrowsAnyAsync<Exception>(() => runtime.RunAsync(intent, variant == "identity" ? new SyntheticActor(f.Caller.SubjectId) : f.Caller));
        Assert.Empty(fake.Requests); Assert.Null(await Executions.GetAsync("runtime-task"));
    }
    [Fact]
    public async Task AllPublicRawEntrypointsDenyWithoutContextBeforeStorageOrTransport()
    {
        var f = F; await Setup(f); using var fake = new RuntimeHttpFixture(f.Root); using var http = new HttpClient(fake);
        var raw = new OpenCodeClient(http, f.Options); var handle = f.Inputs.PreparedHandle;
        await Assert.ThrowsAsync<ExternalHarnessException>(() => raw.ValidateAsync(f.Request, default));
        await Assert.ThrowsAsync<ExternalHarnessException>(() => raw.CreateAsync(f.Request, "runtime-run", handle, default));
        await Assert.ThrowsAsync<ExternalHarnessException>(() => raw.SubmitAsync(f.Request, handle, default));
        await Assert.ThrowsAsync<ExternalHarnessException>(() => raw.ObserveAsync(handle, default));
        await Assert.ThrowsAsync<ExternalHarnessException>(() => raw.ReattachAsync(f.Request.TaskId, f.Root, handle, default));
        await Assert.ThrowsAsync<ExternalHarnessException>(() => raw.AbortAsync(handle, default));
        await Assert.ThrowsAsync<ExternalHarnessException>(() => new ExternalWorkflow(raw).RunAsync(f.Request));
        using var service = new DurableExecutionService(Executions, raw);
        await Assert.ThrowsAsync<ExternalHarnessException>(() => service.RunAsync(f.Request));
        await Assert.ThrowsAsync<ExternalHarnessException>(() => service.RecoverAsync(f.Request.TaskId, f.Root, f.Request.Timeout));
        await Assert.ThrowsAsync<ExternalHarnessException>(() => service.ListRecoverableAsync());
        var executor = new OpenCodeExecutor(raw, "runtime-run", TimeSpan.FromMilliseconds(1));
        await Assert.ThrowsAsync<ExternalHarnessException>(async () => await executor.HandleAsync(f.Request, null!));
        Assert.Empty(fake.Requests); Assert.Null(await Executions.GetAsync(f.Request.TaskId));
        await Assert.ThrowsAsync<ExternalHarnessException>(() => f.Runtime(http).ListAsync()); Assert.Empty(fake.Requests);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task LostCreateOrSubmitResponseNeverProducesReplacementPost(bool create)
    {
        var f = F; await Setup(f); using var fake = new RuntimeHttpFixture(f.Root) { LoseCreateResponse = create, LoseSubmitResponse = !create }; using var http = new HttpClient(fake);
        var original = await f.Runtime(http).RunAsync(f.Intent, f.Caller);
        Assert.Equal(create ? ExternalExecutionStatus.Unknown : ExternalExecutionStatus.Completed, original.Result.Status);
        var before = fake.Requests.Count(x => x.StartsWith("POST "));
        var recovered = await f.Runtime(http).RunAsync(f.Intent, f.Caller, observeOnce: true);
        Assert.Equal(before, fake.Requests.Count(x => x.StartsWith("POST ")));
        Assert.Equal(original.Result.Handle, recovered.Result.Handle); Assert.Equal(original.Result.WorkflowRunId, recovered.Result.WorkflowRunId);
        Assert.Equal(1, Count(fake, "POST", "/session")); Assert.Equal(create ? 0 : 1, Count(fake, "POST", "/session/ses_runtime/prompt_async"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task InspectionCancellationAndTimeoutCannotIssueAbort(bool timeout)
    {
        var f = F; await Setup(f); using var fake = new RuntimeHttpFixture(f.Root) { Pending = true }; using var http = new HttpClient(fake);
        await f.Runtime(http).RunAsync(f.Intent, f.Caller);
        fake.Pending = false; fake.Running = true;
        using var stop = new CancellationTokenSource();
        fake.OnRequest = async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath != "/session/status") return;
            if (!timeout) stop.Cancel();
            await Task.Delay(Timeout.Infinite, token);
        };
        var result = await f.Runtime(http).RunAsync(new(ExternalAdmissionMode.Inspect, f.Request.TaskId, f.Request.Timeout), f.Caller, token: stop.Token);
        Assert.Equal(ExternalExecutionStatus.Unknown, result.Result.Status); Assert.False(result.Result.AbortRequested);
        Assert.Equal(0, Count(fake, "POST", "/session/ses_runtime/abort")); Assert.Equal(2, fake.Requests.Count(x => x.StartsWith("POST ")));
    }
    [Theory]
    [InlineData("policy", false)] [InlineData("control", false)] [InlineData("policy", true)] [InlineData("control", true)]
    public async Task RevisionOrControlWinsBeforeOrAfterIntentThenWireIsDenied(string kind, bool afterIntent)
    {
        var f = F; await Setup(f); using var fake = new RuntimeHttpFixture(f.Root); using var http = new HttpClient(fake);
        async Task Change()
        {
            if (kind == "control") await Sql("UPDATE task_controls SET version=version+1,cancel_id='test-cancel' WHERE task_id='runtime-task'");
            else
            {
                var other = F; other.Policy = f.Policy with { Epoch = new(2), Version = new(2) }; await other.Store().ActivatePolicyAsync();
            }
        }
        var changed = false;
        var sql = new SqliteApprovalStore(Db, f.Authority(), f.Clock, 10000, checkpoint =>
        {
            if (!afterIntent || changed || checkpoint != ApprovalCheckpoint.DispatchIntentCommitted) return;
            changed = true; Change().GetAwaiter().GetResult();
        });
        if (!afterIntent) fake.OnRequest = async (request, token) =>
        {
            if (changed || request.RequestUri!.AbsolutePath != "/agent") return;
            changed = true; await Change();
        };
        await f.Runtime(http, sql).RunAsync(f.Intent, f.Caller);
        Assert.True(changed); Assert.Equal(0, fake.Requests.Count(x => x.StartsWith("POST ")));
        var record = (await Executions.GetAsync(f.Request.TaskId))!;
        Assert.Equal(afterIntent ? SubmissionPhase.SessionCreationIntended : SubmissionPhase.None, record.SubmissionPhase);
    }
    [Theory]
    [InlineData("abort")] [InlineData("changed-prompt")] [InlineData("changed-run")]
    [InlineData("handle")] [InlineData("recovery-submit")] [InlineData("disposed-owner")]
    public async Task EvenProtectedRawClientCannotChangeBindingOrElevateEffect(string variant)
    {
        var f = F; await Setup(f); using var fake = new RuntimeHttpFixture(f.Root) { Pending = true }; using var http = new HttpClient(fake);
        await f.Runtime(http).RunAsync(f.Intent, f.Caller); var before = fake.Requests.Count;
        var record = (await Executions.GetAsync(f.Request.TaskId))!;
        await using var owner = (await Executions.TryAcquireAsync(f.Request.TaskId))!;
        using var access = new ProtectedExternalRuntime.ProtectedAccess(f.Inputs, new ImmutablePolicySource(f.Policy), f, Executions,
            f.Store(), owner, f.Caller, 0, f.Intent, variant == "recovery-submit", f.Clock);
        var raw = new OpenCodeClient(access, http, f.Options);
        if (variant == "disposed-owner") await owner.DisposeAsync();
        Func<Task> attempt = variant switch
        {
            "abort" => () => raw.AbortAsync(record.Handle, default),
            "changed-prompt" => () => raw.SubmitAsync(f.Request with { TaskText = "changed" }, record.Handle, default),
            "changed-run" => () => raw.CreateAsync(f.Request, "other-run", record.Handle, default),
            "handle" => () => raw.ObserveAsync(record.Handle with { ExecutionId = "other" }, default),
            "recovery-submit" => () => raw.SubmitAsync(f.Request, record.Handle, default),
            _ => () => raw.ValidateAsync(f.Request, default)
        };
        await Assert.ThrowsAsync<ExternalHarnessException>(attempt); Assert.Equal(before, fake.Requests.Count);
        Assert.Equal(0, Count(fake, "POST", "/session/ses_runtime/abort"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task IntentAuditFailureOrFaultRollsBackIntentAndPreventsHttp(bool fault)
    {
        var f = F; await Setup(f); using var fake = new RuntimeHttpFixture(f.Root); using var http = new HttpClient(fake);
        var sql = new SqliteApprovalStore(Db, f.Authority(), f.Clock, fault ? 10000 : 2,
            checkpoint => { if (fault && checkpoint == ApprovalCheckpoint.DispatchIntentWritten) throw new IOException("InjectedIntentFailure"); });
        await f.Runtime(http, sql).RunAsync(f.Intent, f.Caller);
        var record = (await Executions.GetAsync(f.Request.TaskId))!;
        Assert.Equal(SubmissionPhase.None, record.SubmissionPhase); Assert.Equal(0, fake.Requests.Count(x => x.StartsWith("POST ")));
        Assert.Equal(2L, await Sql("SELECT count(*) FROM control_audit")); Assert.Equal("ok", await Sql("PRAGMA integrity_check"));
    }
    [Fact]
    public async Task ExpiredPolicyDuringDiscoveryCannotAdvanceDispatchIntent()
    {
        var f = F; var snapshot = f.Policy; f.Policy = snapshot; await Setup(f);
        using var fake = new RuntimeHttpFixture(f.Root); using var http = new HttpClient(fake);
        fake.OnRequest = (request, token) => { if (request.RequestUri!.AbsolutePath == "/agent") f.Clock.Now = snapshot.NotAfter; return Task.CompletedTask; };
        await f.Runtime(http).RunAsync(f.Intent, f.Caller);
        Assert.Equal(0, fake.Requests.Count(x => x.StartsWith("POST ")));
        Assert.Equal(SubmissionPhase.None, (await Executions.GetAsync(f.Request.TaskId))!.SubmissionPhase);
    }
    [Fact]
    public async Task InspectOnlyPolicyCannotSubmitNewTask()
    {
        var f = F; f.Policy = f.Policy with { Rules = [f.Policy.Rules[1]] }; await Setup(f);
        using var fake = new RuntimeHttpFixture(f.Root); using var http = new HttpClient(fake);
        await Assert.ThrowsAsync<ExternalHarnessException>(() => f.Runtime(http).RunAsync(f.Intent, f.Caller));
        Assert.Empty(fake.Requests); Assert.Null(await Executions.GetAsync(f.Request.TaskId));
    }
    [Fact]
    public async Task ActivationAfterFinalPreflightCannotUndoAnAlreadyDispatchedHttpRequest()
    {
        var f = F; await Setup(f); using var fake = new RuntimeHttpFixture(f.Root); using var http = new HttpClient(fake);
        fake.OnRequest = async (request, token) =>
        {
            if (request.Method != HttpMethod.Post || request.RequestUri!.AbsolutePath != "/session") return;
            var newer = F; newer.Policy = f.Policy with { Epoch = new(2), Version = new(2) }; await newer.Store().ActivatePolicyAsync();
        };
        await f.Runtime(http).RunAsync(f.Intent, f.Caller);
        Assert.Equal(1, Count(fake, "POST", "/session")); Assert.Equal(0, Count(fake, "POST", "/session/ses_runtime/prompt_async"));
        Assert.Equal(SubmissionPhase.SessionKnown, (await Executions.GetAsync(f.Request.TaskId))!.SubmissionPhase);
        Assert.Equal(2L, await Sql("SELECT epoch FROM policy_authority"));
    }
    static Process Child(params string[] args)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var dll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../MAF.ApprovalAcceptance/bin", configuration, "net10.0/MAF.ApprovalAcceptance.dll"));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        start.ArgumentList.Add(dll); foreach (var arg in args) start.ArgumentList.Add(arg); return Process.Start(start)!;
    }
    [Theory]
    [InlineData("DispatchIntentWritten", 1)] [InlineData("DispatchIntentCommitted", 1)]
    [InlineData("DispatchIntentWritten", 2)] [InlineData("DispatchIntentCommitted", 2)]
    public async Task ActualProcessCrashAroundEachDispatchIntentNeverReplaysPost(string checkpoint, int ordinal)
    {
        var f = F; await Setup(f); using var child = Child("runtime-crash", Db, checkpoint, ordinal.ToString()); var start = child.StartTime;
        try
        {
            var signal = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(signal == "runtime-checkpoint", $"Unexpected signal {signal}; stderr={(child.HasExited ? await child.StandardError.ReadToEndAsync() : "still running")}");
            Assert.False(child.HasExited); Assert.Equal(start, child.StartTime); child.Kill(entireProcessTree: false); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var record = (await Executions.GetAsync(f.Request.TaskId))!;
            var phase = ordinal == 1 ? checkpoint == "DispatchIntentWritten" ? SubmissionPhase.None : SubmissionPhase.SessionCreationIntended
                : checkpoint == "DispatchIntentWritten" ? SubmissionPhase.SessionKnown : SubmissionPhase.PromptIntended;
            Assert.Equal(phase, record.SubmissionPhase);
            var before = File.Exists(Db + ".http") ? File.ReadAllLines(Db + ".http") : [];
            using var restart = Child("runtime-recover", Db);
            var json = await restart.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await restart.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, restart.ExitCode);
            using var result = JsonDocument.Parse(json);
            Assert.Equal("runtime-run", result.RootElement.GetProperty("WorkflowRunId").GetString());
            Assert.Equal(record.Handle, (await Executions.GetAsync(f.Request.TaskId))!.Handle);
            var after = File.ReadAllLines(Db + ".http");
            Assert.Equal(before.Count(x => x.StartsWith("POST ")), after.Count(x => x.StartsWith("POST ")));
            Assert.Equal(ordinal == 1 ? 0 : 1, after.Count(x => x == "POST /session"));
            Assert.DoesNotContain(after, x => x.Contains("prompt_async") || x.Contains("abort"));
            Assert.Equal("ok", await Sql("PRAGMA integrity_check")); Assert.Equal(1L, await Sql("SELECT count(*) FROM executions"));
            output.WriteLine($"Crash PID={child.Id}, startUTC={start.ToUniversalTime():O}, restartPID={restart.Id}, checkpoint={checkpoint}, ordinal={ordinal}, persistedPhase={phase}, task=runtime-task, run=runtime-run, execution=runtime-execution, message=msg_runtime, session={record.Handle.ExternalSessionId ?? "null"}, createPOST={after.Count(x => x == "POST /session")}, promptPOST=0, abortPOST=0, restartPOSTdelta=0, integrity=ok");
        }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: false); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } }
    }
    public void Dispose()
    {
        // Only this test's generated synthetic directory.
        foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories)) File.Delete(file);
        foreach (var folder in Directory.GetDirectories(directory)) Directory.Delete(folder);
        Directory.Delete(directory);
    }
}
