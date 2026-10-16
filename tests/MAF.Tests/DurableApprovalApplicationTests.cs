using System.Diagnostics;
using System.Text.Json;
using MAF.ApprovalAcceptance;
using MAF.Core;
using MAF.Core.Policy;
using MAF.Persistence.Sqlite;
using MAF.Workflows.External;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace MAF.Tests;

public sealed class DurableApprovalApplicationTests(ITestOutputHelper output) : IDisposable
{
    readonly string directory = Directory.CreateTempSubdirectory("maf-approval-app-").FullName;
    string Db => Path.Combine(directory, "synthetic.db");
    SyntheticApprovalApplication Fixture => new(Db);
    async Task Setup(SyntheticApprovalApplication f)
    { await ApprovalSchema.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance); await f.Store().ActivatePolicyAsync(); }
    async Task<object?> Sql(string text)
    {
        await using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Db, Pooling = false }.ToString());
        await c.OpenAsync(); using var cmd = c.CreateCommand(); cmd.CommandText = text; return await cmd.ExecuteScalarAsync();
    }
    static ApprovalDecisionCommand Command(ApprovalRecord r, ApprovalChoice choice = ApprovalChoice.Approve, string id = "decision-one")
        => new(r.ApprovalId, id, r.Version, r.BindingDigest, choice);
    Task<ApprovalRecord> Pending(SyntheticApprovalApplication f, DurableApprovalApplication? app = null)
        => (app ?? f.Application()).RequestAsync("app-approval", f.Runtime.Intent, f.Caller);
    async Task NoExecution(Boundary? boundary = null)
    {
        Assert.Equal(0L, await Sql("SELECT count(*) FROM executions"));
        Assert.Equal("ok", await Sql("PRAGMA integrity_check")); boundary?.AssertNoEffects();
    }

    // Armed callbacks on the same injected object: even a swallowed accidental invocation fails the oracle.
    sealed class Boundary(SqliteApprovalStore inner, IExecutionStore executions) : IApprovalTransactionCoordinator, IExecutionStore, IExternalHarnessClient
    {
        public int Effects;
        public Func<Task>? BeforeRequest, BeforeDecision;
        T Trap<T>() { Effects++; throw new InvalidOperationException("UnexpectedExecutionCallback"); }
        public void AssertNoEffects() => Assert.Equal(0, Effects);
        public async Task<ApprovalRecord> RequestAsync(string id, PolicyRequest op, ITrustedCallerContext caller, CancellationToken token = default)
        { if (BeforeRequest is not null) await BeforeRequest(); return await inner.RequestAsync(id, op, caller, token); }
        public async Task<ApprovalRecord> DecideAsync(ApprovalDecisionCommand command, ITrustedCallerContext caller, CancellationToken token = default)
        { if (BeforeDecision is not null) await BeforeDecision(); return await inner.DecideAsync(command, caller, token); }
        public Task<ApprovalRecord> ExpireAsync(string id, long version, CancellationToken token = default) => Trap<Task<ApprovalRecord>>();
        public Task<ApprovalRecord> RevokeAsync(string id, long version, ITrustedCallerContext caller, CancellationToken token = default) => Trap<Task<ApprovalRecord>>();
        public Task<DurableExecution> ConsumeAsync(string id, long av, long cv, string digest, DurableExecution initial, ITrustedCallerContext caller, CancellationToken token = default) => Trap<Task<DurableExecution>>();
        public Task<TaskControl> RequestCancellationAsync(string id, string cancel, long version, ITrustedCallerContext caller, CancellationToken token = default) => Trap<Task<TaskControl>>();
        public ValueTask<IExecutionOwnership?> TryAcquireAsync(string task, CancellationToken token = default) => executions.TryAcquireAsync(task, token);
        public Task<DurableExecution?> GetAsync(string task, CancellationToken token = default) => executions.GetAsync(task, token);
        public Task<IReadOnlyList<DurableExecution>> ListRecoverableAsync(CancellationToken token = default) => Trap<Task<IReadOnlyList<DurableExecution>>>();
        public Task CreateAsync(DurableExecution execution, IExecutionOwnership owner, CancellationToken token = default) => Trap<Task>();
        public Task UpdateAsync(DurableExecution before, DurableExecution next, IExecutionOwnership owner, CancellationToken token = default) => Trap<Task>();
        public string Provider => "OpenCode";
        public int GetRequestCount(string task) => Trap<int>();
        public Task ValidateAsync(ExternalTaskRequest request, CancellationToken token) => Trap<Task>();
        public Task<ExternalExecutionHandle> CreateAsync(ExternalTaskRequest request, string run, ExternalExecutionHandle handle, CancellationToken token) => Trap<Task<ExternalExecutionHandle>>();
        public Task SubmitAsync(ExternalTaskRequest request, ExternalExecutionHandle handle, CancellationToken token) => Trap<Task>();
        public Task<ExternalObservation> ObserveAsync(ExternalExecutionHandle handle, CancellationToken token) => Trap<Task<ExternalObservation>>();
        public Task<bool> AbortAsync(ExternalExecutionHandle handle, CancellationToken token) => Trap<Task<bool>>();
        public Task LaunchWorkflow() => Trap<Task>();
    }
    Boundary Guard(SyntheticApprovalApplication f) => new(f.Store(), new SqliteExecutionStore(Db, ExecutionStoreProfile.ExplicitV2));

    [Theory]
    [InlineData(ApprovalChoice.Approve)] [InlineData(ApprovalChoice.Reject)]
    public async Task RequireApprovalPersistsAndAuthorizedDecisionIsStrictlyNonExecutable(ApprovalChoice choice)
    {
        var f = Fixture; await Setup(f); var boundary = Guard(f); var app = f.Application(boundary, boundary);
        var pending = await Pending(f, app);
        Assert.Equal(ApprovalStatus.Pending, pending.Status); Assert.Equal(0, pending.Version);
        Assert.Equal(PolicyOperation.SubmitNewReadOnlyTask, pending.Operation.Operation);
        Assert.Equal(OperationBinding.Digest(pending.Operation, f.Runtime.Submit), pending.BindingDigest);
        Assert.Equal(DurableExecution.Fingerprint(f.Runtime.Request), pending.Operation.RequestFingerprint);
        var decided = await app.DecideAsync(Command(pending, choice), f.Operator);
        Assert.Equal(choice == ApprovalChoice.Approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected, decided.Status);
        Assert.Equal(f.Operator.SubjectId, decided.DecidedBy); Assert.Equal(1, decided.Version);
        Assert.Equal(pending.Operation, decided.Operation); Assert.Equal(pending.BindingDigest, decided.BindingDigest);
        Assert.Equal(decided, await f.Store().GetAsync(pending.ApprovalId));
        Assert.Equal(3L, await Sql("SELECT count(*) FROM control_audit"));
        Assert.DoesNotContain(f.Runtime.Request.TaskText, (string)(await Sql("SELECT snapshot FROM approval_requests"))!);
        Assert.DoesNotContain(typeof(DurableApprovalApplication).GetMethods().Where(m => m.DeclaringType == typeof(DurableApprovalApplication)),
            m => m.ReturnType == typeof(AdmissionContinuation) || m.Name.Contains("Resume") || m.Name.Contains("Consume"));
        Assert.DoesNotContain(typeof(DurableApprovalApplication).GetConstructors().Single().GetParameters(),
            p => p.ParameterType == typeof(IExternalHarnessClient) || p.ParameterType == typeof(HttpClient));
        f.Runtime.Policy = f.Policy;
        using var fake = new RuntimeHttpFixture(f.Runtime.Root); using var http = new HttpClient(fake);
        await Assert.ThrowsAsync<ExternalHarnessException>(() => f.Runtime.Runtime(http).RunAsync(f.Runtime.Intent, f.Caller));
        Assert.Empty(fake.Requests); await NoExecution(boundary);
    }
    [Theory]
    [InlineData("missing")] [InlineData("forged")] [InlineData("expired")]
    [InlineData("future")] [InlineData("self")] [InlineData("agent")]
    [InlineData("no-presence")] [InlineData("revoked")]
    public async Task InvalidOperatorCannotDecide(string variant)
    {
        var f = Fixture; await Setup(f); var boundary = Guard(f); var app = f.Application(boundary, boundary);
        var pending = await Pending(f, app); ITrustedCallerContext? actor = f.Operator;
        switch (variant)
        {
            case "missing": actor = null; break;
            case "forged": actor = new SyntheticActor(f.Operator.SubjectId, f.Operator.AuthenticationContextId); break;
            case "expired": f.OperatorIdentity = f.OperatorIdentity with { NotAfter = f.Clock.Now }; break;
            case "future": f.OperatorIdentity = f.OperatorIdentity with { NotBefore = f.Clock.Now.AddMinutes(1) }; break;
            case "self": actor = f.Self; break;
            case "agent": actor = f.Caller; break;
            case "no-presence": f.OperatorIdentity = f.OperatorIdentity with { FreshHumanPresence = false }; break;
            case "revoked": f.OperatorEnabled = false; break;
        }
        await Assert.ThrowsAnyAsync<Exception>(() => app.DecideAsync(Command(pending), actor));
        Assert.Equal(pending, await f.Store().GetAsync(pending.ApprovalId)); await NoExecution(boundary);
    }
    [Fact]
    public async Task ExpiredApprovalCannotReceiveNewDecision()
    {
        var f = Fixture; await Setup(f); var pending = await Pending(f);
        f.Clock.Now = pending.ExpiresAt;
        Assert.Equal("ApprovalExpiredOrClockInvalid", (await Assert.ThrowsAsync<InvalidOperationException>(() => f.Application().DecideAsync(Command(pending), f.Operator))).Message);
        Assert.Equal(ApprovalStatus.Pending, (await f.Store().GetAsync(pending.ApprovalId))!.Status); await NoExecution();
    }
    [Fact]
    public async Task MissingResolverNeverFallsBackToSyntheticOrSameAccountClaims()
    {
        var f = Fixture; await Setup(f); var pending = await Pending(f); var boundary = Guard(f);
        var app = new DurableApprovalApplication(f.Runtime.Inputs, new ImmutablePolicySource(f.Policy), null, boundary, boundary, f.Clock);
        await Assert.ThrowsAsync<ExternalHarnessException>(() => app.RequestAsync("other", f.Runtime.Intent, f.Caller));
        await Assert.ThrowsAsync<ExternalHarnessException>(() => app.DecideAsync(Command(pending), f.Operator));
        Assert.Equal(pending, await f.Store().GetAsync(pending.ApprovalId)); await NoExecution(boundary);
    }
    [Fact]
    public async Task ExistingExecutionCannotBeTurnedIntoNewApprovalRequest()
    {
        var f = Fixture; var legacy = new SqliteExecutionStore(Db);
        var assessment = new ExternalExecutionApplication(f.Runtime.Inputs, new ImmutablePolicySource(f.Policy), f, f.Clock).Assess(f.Runtime.Intent, f.Caller);
        var existing = SyntheticApproval.Execution(assessment.Operation!, f.Clock.Now) with { Handle = f.Runtime.Inputs.PreparedHandle };
        await using (var owner = (await legacy.TryAcquireAsync(f.Runtime.Request.TaskId))!) { await legacy.CreateAsync(existing, owner); }
        await Setup(f); // Existing offline synthetic migration fixture, no new migration implementation.
        Assert.Equal("ExistingExecutionRecoveryOnly", (await Assert.ThrowsAsync<ExternalHarnessException>(() => Pending(f))).Code);
        Assert.Equal(0L, await Sql("SELECT count(*) FROM approval_requests"));
        Assert.Equal(existing, await new SqliteExecutionStore(Db, ExecutionStoreProfile.ExplicitV2).GetAsync(existing.TaskId));
    }
    [Fact]
    public async Task DuplicateRequestAndDecisionAreIdempotentButConflictsAndStaleCasFail()
    {
        var f = Fixture; await Setup(f); var app = f.Application(); var r = await Pending(f, app);
        Assert.Equal(r, await Pending(f, app)); Assert.Equal(2L, await Sql("SELECT count(*) FROM control_audit"));
        var command = Command(r); var approved = await app.DecideAsync(command, f.Operator);
        Assert.Equal(approved, await app.DecideAsync(command, f.Operator)); Assert.Equal(3L, await Sql("SELECT count(*) FROM control_audit"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.DecideAsync(command with { Choice = ApprovalChoice.Reject }, f.Operator));
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.DecideAsync(command with { DecisionId = "other-id" }, f.Operator));
        await Assert.ThrowsAsync<InvalidOperationException>(() => app.DecideAsync(command with { ExpectedBindingDigest = new('F', 64) }, f.Operator));
        Assert.Equal(approved, await f.Store().GetAsync(r.ApprovalId)); await NoExecution();
    }
    [Fact]
    public async Task CompetingApproveRejectHasOneWinner()
    {
        var f = Fixture; await Setup(f); var pending = await Pending(f);
        async Task<ApprovalRecord?> Decide(ApprovalChoice choice)
        {
            try { return await f.Application().DecideAsync(Command(pending, choice, choice.ToString()), f.Operator); }
            catch (InvalidOperationException e) when (e.Message == "DecisionConflict") { return null; }
        }
        var decisions = await Task.WhenAll(Task.Run(() => Decide(ApprovalChoice.Approve)), Task.Run(() => Decide(ApprovalChoice.Reject)));
        var winner = Assert.Single(decisions, d => d is not null)!;
        Assert.Equal(winner, await f.Store().GetAsync(pending.ApprovalId)); Assert.Equal(3L, await Sql("SELECT count(*) FROM control_audit")); await NoExecution();
    }
    [Theory]
    [InlineData(PolicyOutcome.Allow)] [InlineData(PolicyOutcome.Deny)]
    public async Task OnlyRequireApprovalCreatesPending(PolicyOutcome outcome)
    {
        var f = Fixture; f.Policy = f.Policy with { Rules = [f.Policy.Rules[0] with { Outcome = outcome, RequiredOperatorScope = null }, f.Policy.Rules[1]] };
        await Setup(f); var boundary = Guard(f);
        await Assert.ThrowsAsync<ExternalHarnessException>(() => Pending(f, f.Application(boundary, boundary)));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM approval_requests")); await NoExecution(boundary);
    }
    [Theory]
    [InlineData("missing")] [InlineData("forged")] [InlineData("ephemeral")] [InlineData("inspect")] [InlineData("batch")]
    public async Task InvalidRequestDoesNotPersistPending(string variant)
    {
        var f = Fixture; await Setup(f); var boundary = Guard(f); var intent = f.Runtime.Intent;
        if (variant == "ephemeral") intent = intent with { Mode = ExternalAdmissionMode.EphemeralSubmit };
        if (variant == "inspect") intent = intent with { Mode = ExternalAdmissionMode.Inspect };
        if (variant == "batch") intent = intent with { Mode = ExternalAdmissionMode.BatchInspect };
        var actor = variant == "missing" ? null : variant == "forged" ? new SyntheticActor(f.Caller.SubjectId) : f.Caller;
        await Assert.ThrowsAsync<ExternalHarnessException>(() => f.Application(boundary, boundary).RequestAsync("app-approval", intent, actor));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM approval_requests")); await NoExecution(boundary);
    }
    [Theory]
    [InlineData("cancel")] [InlineData("activate")] [InlineData("candidate")] [InlineData("identity-expiry")]
    public async Task ChangesAfterPreliminaryAuthenticationBeforeDecisionTransactionFailClosed(string change)
    {
        var f = Fixture; await Setup(f); var pending = await Pending(f); var boundary = Guard(f);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        boundary.BeforeDecision = async () => { entered.SetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(10)); };
        var decide = f.Application(boundary, boundary).DecideAsync(Command(pending), f.Operator);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            if (change == "cancel") await f.Store().RequestCancellationAsync(pending.ApprovalId, "cancel-one", 0, f.Caller);
            else if (change == "identity-expiry") f.OperatorIdentity = f.OperatorIdentity with { NotAfter = f.Clock.Now };
            else
            {
                f.Policy = f.Policy with { Epoch = new(2), Version = new(2) };
                if (change == "activate") await f.Store().ActivatePolicyAsync();
            }
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => decide);
        Assert.Equal(change == "cancel" ? ApprovalStatus.Revoked : ApprovalStatus.Pending, (await f.Store().GetAsync(pending.ApprovalId))!.Status);
        Assert.Equal(change == "activate" ? 2L : 1L, await Sql("SELECT epoch FROM policy_authority")); await NoExecution(boundary);
    }
    [Fact]
    public async Task ActivationBetweenAssessmentAndRequestPreventsStalePending()
    {
        var f = Fixture; await Setup(f); var boundary = Guard(f);
        boundary.BeforeRequest = async () => { f.Policy = f.Policy with { Epoch = new(2), Version = new(2) }; await f.Store().ActivatePolicyAsync(); };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Pending(f, f.Application(boundary, boundary)));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM approval_requests")); await NoExecution(boundary);
    }
    [Fact]
    public async Task ActivationAfterDecisionLeavesHistoricalDataButCannotReplayOldAuthorization()
    {
        var f = Fixture; await Setup(f); var pending = await Pending(f); var command = Command(pending);
        var approved = await f.Application().DecideAsync(command, f.Operator);
        f.Policy = f.Policy with { Epoch = new(2), Version = new(2) }; await f.Store().ActivatePolicyAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Application().DecideAsync(command, f.Operator));
        Assert.Equal(approved, await f.Store().GetAsync(pending.ApprovalId));
        Assert.Equal(2L, await Sql("SELECT epoch FROM policy_authority")); await NoExecution();
    }
    [Fact]
    public async Task DecisionCommitsBeforeCancellationThenCancellationRevokesWithoutLaunch()
    {
        var f = Fixture; await Setup(f); var pending = await Pending(f);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var sql = new SqliteApprovalStore(Db, f.Authority(), f.Clock, 10000, point =>
        {
            if (point != ApprovalCheckpoint.DecisionWritten) return;
            entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("DecisionTestGate");
        });
        var decision = Task.Run(() => f.Application(sql).DecideAsync(Command(pending), f.Operator)); Task<TaskControl>? cancellation = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            cancellation = Task.Run(() => f.Store().RequestCancellationAsync(pending.ApprovalId, "cancel-later", 0, f.Caller));
        }
        finally { release.Set(); }
        Assert.Equal(ApprovalStatus.Approved, (await decision).Status); await cancellation!;
        Assert.Equal(ApprovalStatus.Revoked, (await f.Store().GetAsync(pending.ApprovalId))!.Status); await NoExecution();
    }
    static Process Child(params string[] args)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var dll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../MAF.ApprovalAcceptance/bin", configuration, "net10.0/MAF.ApprovalAcceptance.dll"));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        start.ArgumentList.Add(dll); foreach (var arg in args) start.ArgumentList.Add(arg); return Process.Start(start)!;
    }
    [Fact]
    public async Task ApplicationPendingSurvivesAbruptWriterTerminationAndFreshProcessIdempotentRequest()
    {
        var f = Fixture; await Setup(f); using var writer = Child("approval-app-seed", Db); var start = writer.StartTime;
        try
        {
            Assert.Equal("application-pending-committed", await writer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.False(writer.HasExited); Assert.Equal(start, writer.StartTime); writer.Kill(entireProcessTree: false); await writer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var before = await f.Store().GetAsync("app-approval");
            using var reader = Child("approval-app-read", Db);
            var json = await reader.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20)); await reader.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, reader.ExitCode); var recovered = JsonSerializer.Deserialize<ApprovalRecord>(json)!;
            Assert.Equal(before, recovered); Assert.Equal(ApprovalStatus.Pending, recovered.Status);
            Assert.Equal(1L, await Sql("SELECT count(*) FROM approval_requests")); Assert.Equal(2L, await Sql("SELECT count(*) FROM control_audit")); await NoExecution();
            output.WriteLine($"Pending app: writerPID={writer.Id}, startUTC={start.ToUniversalTime():O}, freshPID={reader.Id}, approval={recovered.ApprovalId}, operation={recovered.Operation.OperationId}, task={recovered.Operation.TaskId}, run={recovered.Operation.WorkflowRunId}, execution={recovered.Operation.ExecutionId}, message={recovered.Operation.MessageId}, status={recovered.Status}, version={recovered.Version}, Workflow=0, providerPOST=0, executionRows=0, integrity=ok");
        }
        finally { if (!writer.HasExited) { writer.Kill(entireProcessTree: false); await writer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } }
    }
    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories)) File.Delete(file);
        foreach (var folder in Directory.GetDirectories(directory)) Directory.Delete(folder);
        Directory.Delete(directory);
    }
}
