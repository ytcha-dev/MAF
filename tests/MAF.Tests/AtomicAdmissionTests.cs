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

public sealed class AtomicAdmissionTests(ITestOutputHelper output) : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "maf-admission-" + Guid.NewGuid().ToString("N"));
    readonly SyntheticAdmission f = new();
    string Db => Path.Combine(root, "synthetic.db");
    SqliteExecutionStore Executions => new(Db, ExecutionStoreProfile.ExplicitV2);
    SyntheticAuthority Authority => new() { Snapshot = f.Policy };
    SqliteApprovalStore Store(int capacity = 10000, Action<ApprovalCheckpoint>? fault = null, IProtectedPolicyAuthority? authority = null)
        => new(Db, authority ?? Authority, f.Clock, capacity, fault);
    async Task Setup()
    {
        await ApprovalSchema.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance);
        await Store().ActivatePolicyAsync();
    }
    async Task<object?> Sql(string sql)
    {
        await using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Db, Pooling = false }.ToString());
        await c.OpenAsync(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return await cmd.ExecuteScalarAsync();
    }
    Task<AdmissionContinuation> Admit(SqliteApprovalStore store, IExecutionOwnership owner, PolicyRequest? op = null,
        PolicyDecision? decision = null, ExternalTaskRequest? request = null, DurableExecution? initial = null,
        ITrustedCallerContext? caller = null, long control = 0)
        => store.AdmitAllowedAsync(op ?? f.Assessment.Operation!, decision ?? f.Assessment.Decision,
            request ?? f.Request, initial ?? f.Initial, control, caller ?? f.Caller, owner);
    async Task Empty()
    {
        foreach (var table in new[] { "executions", "authorization_operations", "task_controls", "approval_requests" })
            Assert.Equal(0L, await Sql("SELECT count(*) FROM " + table));
        Assert.Equal(1L, await Sql("SELECT count(*) FROM control_audit"));
        Assert.Equal("ok", await Sql("PRAGMA integrity_check"));
    }
    [Fact]
    public async Task ApplicationCommitsExactCreatedWithNoApprovalAndSingleUseOwnerBoundContinuation()
    {
        await Setup(); await using var owner = (await Executions.TryAcquireAsync(f.Request.TaskId))!;
        var continuation = await f.Application(Store()).AdmitAsync(f.Intent, f.Caller, owner);
        Assert.Equal(f.Initial, await Executions.GetAsync(f.Request.TaskId));
        Assert.Equal(f.Initial, await continuation.ConsumeAsync(owner));
        Assert.Equal("ContinuationAlreadyConsumed", (await Assert.ThrowsAsync<InvalidOperationException>(async () => await continuation.ConsumeAsync(owner))).Message);
        Assert.Equal(0L, await Sql("SELECT count(*) FROM approval_requests"));
        Assert.Equal(1L, await Sql("SELECT count(*) FROM authorization_operations"));
        var audit = (await Store().ReadAuditAsync()).Last();
        Assert.Equal(ControlAuditCode.Requested, audit.Code); Assert.Null(audit.ApprovalId);
        Assert.Equal(f.Policy.Epoch, audit.Epoch); Assert.Equal(f.Policy.Version, audit.PolicyVersion);
        Assert.DoesNotContain(f.Request.TaskText, (string)(await Sql("SELECT snapshot FROM executions"))!);
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(continuation));
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<AdmissionContinuation>("{}"));
        Assert.Empty(typeof(AdmissionContinuation).GetConstructors());
        Assert.False(typeof(AdmissionContinuation).IsDefined(typeof(SerializableAttribute), false));
    }
    [Theory]
    [InlineData("scope")] [InlineData("fingerprint")] [InlineData("version")] [InlineData("epoch")]
    [InlineData("authority")] [InlineData("operation")] [InlineData("capability")]
    [InlineData("caller")] [InlineData("forged-context")] [InlineData("expired")]
    [InlineData("provider")] [InlineData("created-state")] [InlineData("initial-version")]
    [InlineData("request-text")] [InlineData("request-timeout")] [InlineData("session")]
    [InlineData("generation")] [InlineData("binding")]
    public async Task InvalidBindingsFailWithoutWrites(string variant)
    {
        await Setup(); await using var owner = (await Executions.TryAcquireAsync(f.Request.TaskId))!;
        var op = f.Assessment.Operation!; var d = f.Assessment.Decision; var r = f.Request; var e = f.Initial;
        ITrustedCallerContext caller = f.Caller;
        switch (variant)
        {
            case "scope": op = op with { Scope = op.Scope with { ConfigurationRevision = 2 } }; break;
            case "fingerprint": op = op with { RequestFingerprint = new('D', 64) }; break;
            case "version": op = op with { Version = new(2) }; break;
            case "epoch": op = op with { Epoch = new(2) }; break;
            case "authority": op = op with { AuthorityId = "other" }; break;
            case "operation": op = op with { Operation = (PolicyOperation)999 }; break;
            case "capability": op = op with { Capability = (PolicyCapability)999 }; break;
            case "caller": caller = new SyntheticActor("other"); break;
            case "forged-context": caller = new SyntheticActor("submitter", "forged"); break;
            case "expired": d = d with { NotAfter = f.Clock.Now }; break;
            case "provider": e = e with { Handle = e.Handle with { Provider = "other" } }; break;
            case "created-state": e = e with { State = DurableExecutionState.Running }; break;
            case "initial-version": e = e with { Version = 1 }; break;
            case "request-text": r = r with { TaskText = "changed" }; break;
            case "request-timeout": r = r with { Timeout = TimeSpan.FromSeconds(31) }; break;
            case "session": op = op with { ProviderSessionId = "session" }; break;
            case "generation": op = op with { IntentGeneration = 2 }; break;
            case "binding": d = d with { BindingDigest = new('F', 64) }; break;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Admit(Store(), owner, op, d, r, e, caller)); await Empty();
    }
    [Theory]
    [InlineData(PolicyOutcome.Deny)] [InlineData(PolicyOutcome.RequireApproval)]
    public async Task NoApprovalManufacturedForNonAllow(PolicyOutcome outcome)
    {
        await Setup(); await using var owner = (await Executions.TryAcquireAsync(f.Request.TaskId))!;
        var policy = f.Policy with { Rules = [f.Policy.Rules[0] with { Outcome = outcome, RequiredOperatorScope = outcome == PolicyOutcome.RequireApproval ? "operator:readonly" : null }] };
        var application = new AtomicAdmissionApplication(f.Inputs, new ImmutablePolicySource(policy), f, Store(), f.Clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => application.AdmitAsync(f.Intent, f.Caller, owner)); await Empty();
    }
    [Theory]
    [InlineData(ExternalAdmissionMode.EphemeralSubmit)] [InlineData(ExternalAdmissionMode.BatchInspect)] [InlineData(ExternalAdmissionMode.Cancel)]
    public async Task UnsupportedApplicationIntentCannotAdmit(ExternalAdmissionMode mode)
    {
        await Setup(); await using var owner = (await Executions.TryAcquireAsync(f.Request.TaskId))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Application(Store()).AdmitAsync(f.Intent with { Mode = mode }, f.Caller, owner)); await Empty();
    }
    sealed class ForgedOwner(string task) : IExecutionOwnership { public string TaskId => task; public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    [Theory]
    [InlineData("fake")] [InlineData("disposed")] [InlineData("wrong-task")] [InlineData("wrong-db")] [InlineData("legacy")]
    public async Task RequiresLiveExactV2Ownership(string variant)
    {
        await Setup();
        await using var owner = variant switch
        {
            "fake" => new ForgedOwner(f.Request.TaskId),
            "wrong-task" => (await Executions.TryAcquireAsync("other"))!,
            "wrong-db" => (await new SqliteExecutionStore(Path.Combine(root, "other.db"), ExecutionStoreProfile.ExplicitV2).TryAcquireAsync(f.Request.TaskId))!,
            "legacy" => (await new SqliteExecutionStore(Db).TryAcquireAsync(f.Request.TaskId))!,
            _ => (await Executions.TryAcquireAsync(f.Request.TaskId))!
        };
        if (variant == "disposed") await owner.DisposeAsync();
        Assert.Equal("ExecutionOwnershipRequired", (await Assert.ThrowsAsync<InvalidOperationException>(() => Admit(Store(), owner))).Message); await Empty();
    }
    [Fact]
    public async Task OwnershipReleaseAndReacquisitionCannotReplayContinuationOrReadmit()
    {
        await Setup(); var owner = (await Executions.TryAcquireAsync(f.Request.TaskId))!;
        var continuation = await Admit(Store(), owner); await owner.DisposeAsync();
        await using var replacement = (await Executions.TryAcquireAsync(f.Request.TaskId))!;
        Assert.Equal("ContinuationOwnerMismatch", (await Assert.ThrowsAsync<InvalidOperationException>(async () => await continuation.ConsumeAsync(replacement))).Message);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await continuation.ConsumeAsync(owner));
        Assert.Equal("ExistingExecutionRecoveryOnly", (await Assert.ThrowsAsync<InvalidOperationException>(() => Admit(Store(), replacement))).Message);
        Assert.Equal(f.Initial, await Executions.GetAsync(f.Request.TaskId));
    }
    [Theory]
    [InlineData("ExecutionInserted", false)] [InlineData("BeforeCommit", false)] [InlineData("AdmissionCommitted", true)]
    public async Task TransactionFaultsNeverReturnContinuation(string checkpoint, bool committed)
    {
        await Setup(); await using var owner = (await Executions.TryAcquireAsync(f.Request.TaskId))!;
        var store = Store(fault: point => { if (point.ToString() == checkpoint) throw new IOException("InjectedFailure"); });
        await Assert.ThrowsAsync<IOException>(() => Admit(store, owner));
        if (!committed) await Empty();
        else
        {
            Assert.Equal(f.Initial, await Executions.GetAsync(f.Request.TaskId));
            Assert.Equal("ExistingExecutionRecoveryOnly", (await Assert.ThrowsAsync<InvalidOperationException>(() => Admit(Store(), owner))).Message);
        }
    }
    [Fact]
    public async Task RequiredAuditExhaustionRollsBackAllAdmissionRows()
    {
        await Setup(); await using var owner = (await Executions.TryAcquireAsync(f.Request.TaskId))!;
        Assert.Equal("AuditCapacityReached", (await Assert.ThrowsAsync<InvalidOperationException>(() => Admit(Store(1), owner))).Message); await Empty();
    }
    [Theory]
    [InlineData(1, false)] [InlineData(0, true)] [InlineData(1, true)]
    public async Task StaleOrCancelledControlDeniesAdmission(long version, bool cancelled)
    {
        await Setup();
        await Sql($"INSERT INTO task_controls(task_id,execution_id,version,cancel_id) VALUES('task-admit','execution-admit',{version},{(cancelled ? "'cancel-test'" : "NULL")})");
        await using var owner = (await Executions.TryAcquireAsync(f.Request.TaskId))!;
        Assert.Equal("ControlConflict", (await Assert.ThrowsAsync<InvalidOperationException>(() => Admit(Store(), owner))).Message);
        Assert.Equal(0L, await Sql("SELECT count(*) FROM executions")); Assert.Equal(0L, await Sql("SELECT count(*) FROM authorization_operations"));
    }
    [Fact]
    public async Task PendingApprovalCannotBeReplacedWithDirectAllow()
    {
        await ApprovalSchema.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance);
        var approvalPolicy = f.Policy with { Rules = [f.Policy.Rules[0] with { Outcome = PolicyOutcome.RequireApproval, RequiredOperatorScope = "operator:readonly" }] };
        var old = Store(authority: new SyntheticAuthority { Snapshot = approvalPolicy }); await old.ActivatePolicyAsync();
        await old.RequestAsync("pending", f.Assessment.Operation!, f.Caller);
        var next = f.Policy with { Epoch = new(2), Version = new(2) }; var store = Store(authority: new SyntheticAuthority { Snapshot = next }); await store.ActivatePolicyAsync();
        var op = f.Assessment.Operation! with { Epoch = new(2), Version = new(2) };
        var d = new DeterministicPolicyEvaluator(new ImmutablePolicySource(next), f.Clock).Evaluate(op, f.Caller);
        await using var owner = (await Executions.TryAcquireAsync(f.Request.TaskId))!;
        Assert.Equal("InitialOperationAlreadyExists", (await Assert.ThrowsAsync<InvalidOperationException>(() => Admit(store, owner, op, d))).Message);
        Assert.Equal(ApprovalStatus.Pending, (await old.GetAsync("pending"))!.Status); Assert.Null(await Executions.GetAsync(f.Request.TaskId));
    }
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(3)] [InlineData(99)]
    public async Task ExplicitV2ProfileRejectsOtherSchemasWithoutChangingThem(int version)
    {
        Directory.CreateDirectory(root); await Sql($"PRAGMA user_version={version}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Executions.GetAsync("task"));
        Assert.Equal((long)version, await Sql("PRAGMA user_version"));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM sqlite_master WHERE name='executions'"));
    }
    sealed class GatedAuthority(SyntheticAuthority inner) : IProtectedPolicyAuthority
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken token = default)
        { Entered.SetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), token); return await inner.AcquireAsync(token); }
        public PolicySnapshot GetSnapshot() => inner.GetSnapshot();
        public PolicyDecision EvaluateStoredOperation(PolicyRequest op, DateTimeOffset at) => inner.EvaluateStoredOperation(op, at);
        public bool CanControl(ITrustedCallerContext caller, PolicyRequest op) => inner.CanControl(caller, op);
        public bool CanDecide(ITrustedCallerContext caller, PolicyRequest op, string scope) => inner.CanDecide(caller, op, scope);
        public bool IsDecisionSubjectStillAuthorized(string subject, PolicyRequest op, string scope) => inner.IsDecisionSubjectStillAuthorized(subject, op, scope);
        public bool MatchesExecutionBinding(PolicyRequest op, DurableExecution e) => inner.MatchesExecutionBinding(op, e);
    }
    [Fact]
    public async Task ActivationCommittedBeforeAdmissionRejectsStaleAssessment()
    {
        await Setup(); await using var owner = (await Executions.TryAcquireAsync(f.Request.TaskId))!;
        var gate = new GatedAuthority(Authority);
        var admission = Admit(Store(authority: gate), owner);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            await Store(authority: new SyntheticAuthority { Snapshot = f.Policy with { Epoch = new(2), Version = new(2) } }).ActivatePolicyAsync();
        }
        finally { gate.Release.TrySetResult(); }
        Assert.Equal("AuthorityRollback", (await Assert.ThrowsAsync<InvalidOperationException>(() => admission)).Message);
        Assert.Null(await Executions.GetAsync(f.Request.TaskId));
        Assert.Equal(2L, await Sql("SELECT epoch FROM policy_authority"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AdmissionWriterSerializesActivationOrCancellationAndOwnerDisposal(bool cancellation)
    {
        await Setup();
        if (cancellation) await Sql("INSERT INTO task_controls(task_id,execution_id,version) VALUES('task-admit','execution-admit',0)");
        var owner = (await Executions.TryAcquireAsync(f.Request.TaskId))!;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var store = Store(fault: point =>
        {
            if (point != ApprovalCheckpoint.ExecutionInserted) return;
            entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("TestGateTimeout");
        });
        var admission = Task.Run(() => Admit(store, owner));
        Task? rival = null; Task? disposal = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            using var attempted = new ManualResetEventSlim();
            rival = Task.Run(async () =>
            {
                attempted.Set();
                if (cancellation)
                {
                    // Test-owned control writer models a durable cancellation CAS; no production cancellation API added.
                    Assert.Equal(1L, await Sql("UPDATE task_controls SET version=1,cancel_id='race-cancel',actor='submitter',requested_at='2026-10-16T00:00:00Z' WHERE task_id='task-admit' AND version=0 AND cancel_id IS NULL; SELECT changes();"));
                }
                else await Store(authority: new SyntheticAuthority { Snapshot = f.Policy with { Epoch = new(2), Version = new(2) } }).ActivatePolicyAsync();
            });
            Assert.True(attempted.Wait(TimeSpan.FromSeconds(10)));
            disposal = owner.DisposeAsync().AsTask(); Assert.False(disposal.IsCompleted);
            release.Set(); var continuation = await admission.WaitAsync(TimeSpan.FromSeconds(15));
            await rival.WaitAsync(TimeSpan.FromSeconds(15)); await disposal.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(f.Initial, await Executions.GetAsync(f.Request.TaskId));
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await continuation.ConsumeAsync(owner));
            Assert.Equal(cancellation ? 1L : 0L, await Sql("SELECT version FROM task_controls"));
            Assert.Equal(cancellation ? 1L : 2L, await Sql("SELECT epoch FROM policy_authority"));
        }
        finally
        {
            release.Set();
            try { await admission; if (rival is not null) await rival; if (disposal is not null) await disposal; }
            finally { await owner.DisposeAsync(); }
        }
    }
    [Fact]
    public async Task SameOwnerConcurrentAdmissionAndContinuationConsumptionHaveSingleWinner()
    {
        await Setup(); await using var owner = (await Executions.TryAcquireAsync(f.Request.TaskId))!;
        async Task<AdmissionContinuation?> Attempt()
        { try { return await Admit(Store(), owner); } catch (InvalidOperationException e) when(e.Message == "ExistingExecutionRecoveryOnly") { return null; } }
        var results = await Task.WhenAll(Task.Run(Attempt), Task.Run(Attempt)); var continuation = Assert.Single(results, x => x is not null)!;
        async Task<bool> Consume()
        { try { await continuation.ConsumeAsync(owner); return true; } catch (InvalidOperationException e) when(e.Message == "ContinuationAlreadyConsumed") { return false; } }
        Assert.Single(await Task.WhenAll(Task.Run(Consume), Task.Run(Consume)), x => x);
        Assert.Equal(1L, await Sql("SELECT count(*) FROM executions"));
    }
    [Theory]
    [InlineData("unactivated")] [InlineData("candidate")] [InlineData("expired")]
    [InlineData("digest")] [InlineData("unavailable")]
    public async Task PolicyFailureNeverActivatesThroughAdmission(string variant)
    {
        var assessment = f.Assessment; var initial = f.Initial;
        await ApprovalSchema.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance);
        if (variant != "unactivated") await Store().ActivatePolicyAsync();
        var authority = Authority;
        if (variant == "candidate") authority.Snapshot = f.Policy with { Epoch = new(2), Version = new(2) };
        if (variant == "expired") f.Clock.Now = f.Policy.NotAfter;
        if (variant == "digest") await Sql("UPDATE policy_authority SET digest='different'");
        await using var owner = (await Executions.TryAcquireAsync(f.Request.TaskId))!;
        IProtectedPolicyAuthority selected = variant == "unavailable" ? new UnavailableAuthority() : authority;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Admit(Store(authority: selected), owner, assessment.Operation, assessment.Decision, initial: initial));
        Assert.Null(await Executions.GetAsync(f.Request.TaskId));
        Assert.Equal(variant == "unactivated" ? 0L : 1L, await Sql("SELECT count(*) FROM policy_authority"));
        if (variant != "unactivated") Assert.Equal(1L, await Sql("SELECT epoch FROM policy_authority"));
    }
    [Fact]
    public async Task CancellationCommittedWhileAdmissionWaitsForAuthorityPreventsCreated()
    {
        await Setup();
        await Sql("INSERT INTO task_controls(task_id,execution_id,version) VALUES('task-admit','execution-admit',0)");
        await using var owner = (await Executions.TryAcquireAsync(f.Request.TaskId))!;
        var gate = new GatedAuthority(Authority); var admission = Admit(Store(authority: gate), owner);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.Equal(1L, await Sql("UPDATE task_controls SET version=1,cancel_id='cancel-wins',actor='submitter',requested_at='2026-10-16T00:00:00Z' WHERE task_id='task-admit' AND version=0; SELECT changes();"));
        }
        finally { gate.Release.TrySetResult(); }
        Assert.Equal("ControlConflict", (await Assert.ThrowsAsync<InvalidOperationException>(() => admission)).Message);
        Assert.Null(await Executions.GetAsync(f.Request.TaskId));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM authorization_operations"));
    }
    sealed class UnavailableAuthority : IProtectedPolicyAuthority
    {
        public ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken token = default) => new SyntheticAuthority().AcquireAsync(token);
        public PolicySnapshot? GetSnapshot() => null;
        public PolicyDecision EvaluateStoredOperation(PolicyRequest op, DateTimeOffset at) => throw new InvalidOperationException();
        public bool CanControl(ITrustedCallerContext caller, PolicyRequest op) => false;
        public bool CanDecide(ITrustedCallerContext caller, PolicyRequest op, string scope) => false;
        public bool IsDecisionSubjectStillAuthorized(string subject, PolicyRequest op, string scope) => false;
        public bool MatchesExecutionBinding(PolicyRequest op, DurableExecution e) => false;
    }
    sealed class Acquisition(PolicySnapshot policy) : ITrustedPolicyAcquisition
    {
        public Task<AcquiredPolicy?> AcquireAsync(CancellationToken token)
            => Task.FromResult<AcquiredPolicy?>(new("synthetic-source", policy, PolicySnapshotBinding.Digest(policy)));
    }
    [Fact]
    public async Task RealAuthorityAdapterWithoutInstalledIdentityAndAuthorizationStillDeniesAllow()
    {
        await Setup(); await using var owner = (await Executions.TryAcquireAsync(f.Request.TaskId))!;
        var protectedAuthority = new TrustedPolicyAuthority("synthetic-source", "authority", new Acquisition(f.Policy), f.Clock);
        Assert.Equal("CallerNotAuthorized", (await Assert.ThrowsAsync<InvalidOperationException>(() => Admit(Store(authority: protectedAuthority), owner))).Message);
        await Empty();
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ApplicationRejectsMissingOrForgedSameMetadataIdentity(bool missing)
    {
        await Setup(); await using var owner = (await Executions.TryAcquireAsync(f.Request.TaskId))!;
        var forged = missing ? null! : new SyntheticActor(f.Caller.SubjectId, f.Caller.AuthenticationContextId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Application(Store()).AdmitAsync(f.Intent, forged, owner));
        await Empty();
    }
    sealed class NoEffectsClient(string scope) : IResumableExternalHarnessClient
    {
        public int Calls;
        T Trap<T>() { Calls++; throw new InvalidOperationException("ForbiddenExecutionCallback"); }
        public string Provider => "Synthetic";
        public string Scope => scope;
        public ExternalExecutionHandle PrepareExecution() => Trap<ExternalExecutionHandle>();
        public int GetRequestCount(string task) => Trap<int>();
        public Task ValidateAsync(ExternalTaskRequest request, CancellationToken token) => Trap<Task>();
        public Task<ExternalExecutionHandle> CreateAsync(ExternalTaskRequest request, string run, ExternalExecutionHandle handle, CancellationToken token) => Trap<Task<ExternalExecutionHandle>>();
        public Task SubmitAsync(ExternalTaskRequest request, ExternalExecutionHandle handle, CancellationToken token) => Trap<Task>();
        public Task<ExternalObservation> ObserveAsync(ExternalExecutionHandle handle, CancellationToken token) => Trap<Task<ExternalObservation>>();
        public Task<bool> AbortAsync(ExternalExecutionHandle handle, CancellationToken token) => Trap<Task<bool>>();
        public Task ReattachAsync(string task, string root, ExternalExecutionHandle handle, CancellationToken token) => Trap<Task>();
    }
    [Fact]
    public async Task ExistingCreatedRecoversUnknownWithoutWorkflowOrAnyProviderCallback()
    {
        await Setup();
        await using (var owner = (await Executions.TryAcquireAsync(f.Request.TaskId))!) { await Admit(Store(), owner); }
        var trap = new NoEffectsClient(f.Scope.ProviderScopeDigest);
        using var recovery = new DurableExecutionService(HistoricalRuntimeAccess.Instance, Executions, trap);
        var result = await recovery.RecoverAsync(f.Request.TaskId, f.Root, f.Request.Timeout);
        Assert.Equal(ExternalExecutionStatus.Unknown, result.Result.Status); Assert.Equal("MissingSubmissionEvidence", result.Result.ErrorCode);
        Assert.Empty(result.WorkflowEvents); Assert.Equal(0, trap.Calls);
        var record = (await Executions.GetAsync(f.Request.TaskId))!;
        Assert.Equal(f.Initial.Handle, record.Handle); Assert.Equal(f.Initial.WorkflowRunId, record.WorkflowRunId);
        await using var replacement = (await Executions.TryAcquireAsync(f.Request.TaskId))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Admit(Store(), replacement)); Assert.Equal(0, trap.Calls);
    }
    [Fact]
    public async Task V2IsExplicitReadUpdateOnlyAndLegacyStillRejectsV2()
    {
        await Assert.ThrowsAsync<SqliteException>(() => Executions.GetAsync("task")); Assert.False(File.Exists(Db));
        await Setup(); await using var owner = (await Executions.TryAcquireAsync(f.Request.TaskId))!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Executions.CreateAsync(f.Initial, owner));
        await Admit(Store(), owner); await Assert.ThrowsAsync<InvalidOperationException>(() => new SqliteExecutionStore(Db).GetAsync(f.Request.TaskId));
        var before = (await Executions.GetAsync(f.Request.TaskId))!;
        var next = before with { State = DurableExecutionState.Unknown, Version = 1, ErrorCode = "MissingSubmissionEvidence" };
        await Executions.UpdateAsync(before, next, owner); Assert.Equal(next, await Executions.GetAsync(f.Request.TaskId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Executions.UpdateAsync(before, next, owner));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Executions.UpdateAsync(next, next with { Version = 2, RequestFingerprint = new('A', 64) }, owner));
        var terminal = next with { State = DurableExecutionState.Failed, Version = 2, CompletedAt = f.Clock.Now };
        await Executions.UpdateAsync(next, terminal, owner);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Executions.UpdateAsync(terminal, terminal with { Version = 3 }, owner));
        Assert.Equal(2L, await Sql("PRAGMA user_version"));
    }
    static Process Child(params string[] args)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var dll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../MAF.ApprovalAcceptance/bin", configuration, "net10.0/MAF.ApprovalAcceptance.dll"));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        start.ArgumentList.Add(dll); foreach (var a in args) start.ArgumentList.Add(a); return Process.Start(start)!;
    }
    static async Task<string?> Line(Process child) => await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
    static async Task Kill(Process child)
    {
        if (!child.HasExited) child.Kill(entireProcessTree: false);
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }
    async Task<IExecutionOwnership> ReacquireAfterCrash()
    {
        // Windows process exit notification may precede final file-handle teardown.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var owner = await Executions.TryAcquireAsync(f.Request.TaskId, timeout.Token);
            if (owner is not null) return owner;
            await Task.Delay(20, timeout.Token);
        }
    }
    [Fact]
    public async Task CompetingProcessesAdmitExactlyOnceAndRestartIsRecoveryOnly()
    {
        await Setup(); using var first = Child("admit-child", Db); var start = first.StartTime;
        try
        {
            Assert.Equal("owner-acquired", await Line(first));
            using var competitor = Child("admit-child", Db);
            Assert.Equal("owner-busy", await Line(competitor)); await competitor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, competitor.ExitCode);
            await first.StandardInput.WriteLineAsync("go"); Assert.Equal("admitted-committed", await Line(first)); await Kill(first);
            using var restart = Child("admit-child", Db);
            try
            {
                Assert.Equal("owner-acquired", await Line(restart)); await restart.StandardInput.WriteLineAsync("go"); Assert.Equal("recovery-only", await Line(restart));
                await restart.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, restart.ExitCode);
                Assert.Equal(f.Initial, await Executions.GetAsync(f.Request.TaskId));
                foreach (var table in new[] { "executions", "authorization_operations", "task_controls" }) Assert.Equal(1L, await Sql("SELECT count(*) FROM " + table));
                Assert.Equal(0L, await Sql("SELECT count(*) FROM approval_requests")); Assert.Equal("ok", await Sql("PRAGMA integrity_check"));
                output.WriteLine($"Competition: ownerPID={first.Id}, startUTC={start.ToUniversalTime():O}, contenderPID={competitor.Id}, restartPID={restart.Id}, task=task-admit, run=run-admit, execution=execution-admit, message=message-admit, state=Created/None/v0, unique=1, restart=recovery-only, providerPOST=0, integrity=ok");
            }
            finally { await Kill(restart); }
        }
        finally { await Kill(first); }
    }
    [Theory]
    [InlineData("ExecutionInserted", false)] [InlineData("AdmissionCommitted", true)]
    public async Task ActualProcessCrashBeforeOrAfterCommitCannotReplayContinuation(string checkpoint, bool committed)
    {
        await Setup(); using var child = Child("admit-crash", Db, checkpoint); var start = child.StartTime;
        try
        {
            Assert.Equal("owner-acquired", await Line(child)); await child.StandardInput.WriteLineAsync("go"); Assert.Equal("admission-checkpoint", await Line(child));
            Assert.False(child.HasExited); Assert.Equal(start, child.StartTime); await Kill(child);
            await using var owner = await ReacquireAfterCrash();
            if (committed)
            {
                Assert.Equal(f.Initial, await Executions.GetAsync(f.Request.TaskId));
                Assert.Equal("ExistingExecutionRecoveryOnly", (await Assert.ThrowsAsync<InvalidOperationException>(() => Admit(Store(), owner))).Message);
            }
            else { await Empty(); Assert.NotNull(await Admit(Store(), owner)); }
            Assert.Equal("ok", await Sql("PRAGMA integrity_check"));
            output.WriteLine($"Crash: PID={child.Id}, startUTC={start.ToUniversalTime():O}, checkpoint={checkpoint}, committed={committed}, original/recovered=task-admit/run-admit/execution-admit/message-admit, continuationReturned=false, providerPOST=0, integrity=ok");
        }
        finally { await Kill(child); }
    }
    public void Dispose()
    {
        if (!Directory.Exists(root)) return;
        // Generated synthetic fixture directory only; no caller-supplied cleanup paths.
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) File.Delete(file);
        foreach (var directory in Directory.GetDirectories(root)) Directory.Delete(directory);
        Directory.Delete(root);
    }
}
