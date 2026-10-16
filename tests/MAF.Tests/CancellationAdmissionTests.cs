using System.Diagnostics;
using MAF.ApprovalAcceptance;
using MAF.Core;
using MAF.Core.Policy;
using MAF.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace MAF.Tests;

public sealed class CancellationAdmissionTests(ITestOutputHelper output) : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "maf-cancel-" + Guid.NewGuid().ToString("N"));
    string Db => Path.Combine(root, "synthetic.db");
    SyntheticCancellation F(bool approved = false) => new(Db, approved);
    async Task<object?> Sql(string sql)
    {
        await using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Db, Pooling = false }.ToString());
        await c.OpenAsync(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return await cmd.ExecuteScalarAsync();
    }
    async Task NoIntent()
    {
        Assert.Equal(0L, await Sql("SELECT count(*) FROM cancellation_requests"));
        Assert.Equal(0L, await Sql("SELECT version FROM task_controls"));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM authorization_operations WHERE kind=3"));
        Assert.Equal("ok", await Sql("PRAGMA integrity_check"));
    }
    [Fact]
    public async Task IndependentActorCommitsWhileExecutionOwnerHeldAndExactReplayWritesNothing()
    {
        var f = F(); await f.Setup(); await using var owner = (await f.Executions.TryAcquireAsync(f.Command.TaskId))!;
        using var transport = new RuntimeHttpFixture(f.Runtime.Root); using var http = new HttpClient(transport);
        var original = await f.Executions.GetAsync(f.Command.TaskId);
        var first = await f.Application().RequestAsync(f.Command, f.Canceller);
        var audit = await Sql("SELECT count(*) FROM control_audit");
        var second = await f.Application().RequestAsync(f.Command, f.Canceller);
        Assert.Equal(CancellationAdmissionStatus.Recorded, first.Status); Assert.Equal(CancellationAdmissionStatus.AlreadyRecorded, second.Status);
        Assert.Equal(first.Evidence!.BindingDigest, second.Evidence!.BindingDigest);
        Assert.Equal(audit, await Sql("SELECT count(*) FROM control_audit"));
        Assert.Equal(1L, await Sql("SELECT version FROM task_controls"));
        Assert.Equal(original, await f.Executions.GetAsync(f.Command.TaskId)); Assert.Empty(transport.Requests);
        Assert.Equal("synthetic-canceller", first.Evidence.Operation.CallerSubject);
        Assert.Equal("cancel-one", (await f.Store().ReadAuditAsync()).Last().CancelRequestId);
        output.WriteLine($"Cancel original/recovered={first.Evidence.Operation.TaskId}/{first.Evidence.Operation.ExecutionId}, control=1, Create/Submit/Observe/Abort=0/0/0/0, digest={first.Evidence.BindingDigest}");
    }
    [Theory]
    [InlineData("missing")] [InlineData("forged")] [InlineData("submit-only")] [InlineData("inspect-only")]
    [InlineData("unavailable")] [InlineData("expired")] [InlineData("unactivated")] [InlineData("scope")]
    [InlineData("authority")] [InlineData("capability")] [InlineData("require-approval")] [InlineData("deny")] [InlineData("policy-deny")]
    [InlineData("execution")] [InlineData("stale")]
    public async Task InvalidCancelFailsWithoutAnyIntent(string variant)
    {
        var f = F();
        if (variant == "require-approval") f.Policy = f.Policy with { Rules = [..f.Policy.Rules.Select(r => r.Operation == PolicyOperation.RequestCancelExecution ? r with { Outcome = PolicyOutcome.RequireApproval, RequiredOperatorScope = "operator:readonly" } : r)] };
        if (variant == "policy-deny") f.Policy = f.Policy with { Rules = [..f.Policy.Rules.Select(r => r.Operation == PolicyOperation.RequestCancelExecution ? r with { Outcome = PolicyOutcome.Deny } : r)] };
        await f.Setup(); var inputs = f.Inputs; var command = f.Command; ITrustedCallerContext? caller = f.Canceller;
        if (variant is "submit-only" or "inspect-only")
        {
            var forbidden = variant == "submit-only" ? PolicyOperation.InspectExistingExecution : PolicyOperation.SubmitNewReadOnlyTask;
            f.Policy = f.Policy with { Epoch = new(2), Version = new(2), Rules = [..f.Policy.Rules.Where(r => r.Operation != forbidden)] };
            await f.Store().ActivatePolicyAsync(); inputs = f.Inputs; caller = f.Runtime.Caller;
        }
        switch (variant)
        {
            case "missing": caller = null; break;
            case "forged": caller = new SyntheticActor(f.Canceller.SubjectId); break;
            case "unavailable": f.Available = false; break;
            case "expired": f.Clock.Now = f.Policy.NotAfter; break;
            case "unactivated": f.Policy = f.Policy with { Epoch = new(2), Version = new(2) }; break;
            case "scope": inputs = inputs with { Scope = inputs.Scope with { ScopeRevision = 2 } }; break;
            case "authority": inputs = inputs with { AuthorityId = "other" }; break;
            case "capability": inputs = inputs with { Capability = inputs.Capability with { Capability = (PolicyCapability)999 } }; break;
            case "deny": f.Enabled = false; break;
            case "execution": command = command with { ExecutionId = "wrong" }; break;
            case "stale": command = command with { ExpectedControlVersion = 1 }; break;
        }
        var error = await Assert.ThrowsAnyAsync<Exception>(() => f.Application(inputs: inputs).RequestAsync(command, caller));
        if (variant == "require-approval") Assert.Equal("CancelApprovalNotSupported", error.Message);
        if (variant is "submit-only" or "inspect-only" or "policy-deny") Assert.Equal("CancellationNotAuthorized", error.Message);
        await NoIntent();
    }
    [Theory]
    [InlineData("id")] [InlineData("version")] [InlineData("operation")] [InlineData("expired")] [InlineData("revoked")]
    public async Task ReplaysCannotReplaceOrRefreshWinner(string variant)
    {
        var f = F(); await f.Setup(); await f.Application().RequestAsync(f.Command, f.Canceller);
        var command = f.Command; var inputs = f.Inputs;
        switch (variant)
        {
            case "id": command = command with { CancelRequestId = "different" }; break;
            case "version": command = command with { ExpectedControlVersion = 1 }; break;
            case "operation": inputs = inputs with { OperationId = "different-operation" }; break;
            case "expired": f.Clock.Now = f.Clock.Now.AddMinutes(6); break;
            case "revoked": f.Enabled = false; break;
        }
        await Assert.ThrowsAnyAsync<Exception>(() => f.Application(inputs: inputs).RequestAsync(command, f.Canceller));
        Assert.Equal(1L, await Sql("SELECT count(*) FROM cancellation_requests")); Assert.Equal(1L, await Sql("SELECT version FROM task_controls"));
    }
    [Fact]
    public async Task AnotherCurrentlyAuthorizedCallerCannotReplayOrReplaceOriginalActor()
    {
        var f = F();
        f.Policy = f.Policy with { Rules = [..f.Policy.Rules, f.Policy.Rules.Last() with { CallerSubject = "another-canceller" }] };
        await f.Setup(); await f.Application().RequestAsync(f.Command, f.Canceller);
        f.Canceller = new("another-canceller");
        Assert.Equal("CancellationIdConflict", (await Assert.ThrowsAsync<InvalidOperationException>(() => f.Application().RequestAsync(f.Command, f.Canceller))).Message);
        Assert.Equal("synthetic-canceller", (await f.Store().GetCancellationAsync("cancel-one"))!.Operation.CallerSubject);
    }
    [Theory]
    [InlineData(DurableExecutionState.Running)] [InlineData(DurableExecutionState.Unknown)] [InlineData(DurableExecutionState.SubmissionUncertain)]
    public async Task CancellationPreservesExistingRunningOrUncertainExecutionAndOriginalHandle(DurableExecutionState state)
    {
        var f = F(); await f.Setup(); await using var owner = (await f.Executions.TryAcquireAsync(f.Command.TaskId))!;
        var current = (await f.Executions.GetAsync(f.Command.TaskId))!;
        async Task Next(DurableExecutionState nextState, SubmissionPhase phase, bool session = false)
        {
            var next = current with { State = nextState, SubmissionPhase = phase, Version = current.Version + 1,
                Handle = session ? current.Handle with { ExternalSessionId = "ses_runtime" } : current.Handle };
            await f.Executions.UpdateAsync(current, next, owner); current = next;
        }
        if (state == DurableExecutionState.Unknown) await Next(state, SubmissionPhase.None);
        else
        {
            await Next(DurableExecutionState.SessionCreating, SubmissionPhase.SessionCreationIntended);
            await Next(DurableExecutionState.ReadyToSubmit, SubmissionPhase.SessionKnown, true);
            await Next(DurableExecutionState.SubmissionIntended, SubmissionPhase.PromptIntended);
            await Next(state, state == DurableExecutionState.Running ? SubmissionPhase.Acknowledged : SubmissionPhase.PromptIntended);
        }
        var result = await f.Application().RequestAsync(f.Command, f.Canceller);
        Assert.Equal(current, await f.Executions.GetAsync(f.Command.TaskId)); Assert.False(current.AbortRequested);
        Assert.Equal(current.Handle.ExternalSessionId, result.Evidence!.Operation.ProviderSessionId);
        Assert.Equal(current.Handle.ExternalRequestId, result.Evidence.Operation.MessageId);
        Assert.Equal(result.Evidence.BindingDigest, (await f.Store().GetCancellationAsync("cancel-one"))!.BindingDigest);
    }
    [Theory]
    [InlineData("CancelEvidenceInserted")] [InlineData("CancelControlWritten")] [InlineData("CancelApprovalRevoked")] [InlineData("CancelAuditWritten")]
    public async Task TransactionFaultRollsBackAllEvidenceControlAndRevocation(string checkpoint)
    {
        var f = F(true); await f.Setup(true); var before = await f.Store().GetAsync("cancel-approval");
        var audit = await Sql("SELECT count(*) FROM control_audit");
        await Assert.ThrowsAsync<IOException>(() => f.Application(f.Store(p => { if (p == checkpoint) throw new IOException("synthetic"); })).RequestAsync(f.Command, f.Canceller));
        await NoIntent(); Assert.Equal(before, await f.Store().GetAsync("cancel-approval")); Assert.Equal(audit, await Sql("SELECT count(*) FROM control_audit"));
    }
    [Theory]
    [InlineData("missing")] [InlineData("actor")] [InlineData("version")] [InlineData("unlink")] [InlineData("revocation")]
    public async Task EvidenceReadRejectsMissingOrMismatchedAudit(string corruption)
    {
        var f = F(true); await f.Setup(true); await f.Application().RequestAsync(f.Command, f.Canceller);
        await Sql(corruption switch
        {
            "missing" => "DELETE FROM control_audit WHERE code=6",
            "actor" => "UPDATE control_audit SET actor='wrong' WHERE code=6",
            "version" => "UPDATE control_audit SET version=99 WHERE code=6",
            "unlink" => "UPDATE control_audit SET cancel_request_id=NULL WHERE code=6",
            _ => "DELETE FROM control_audit WHERE code=4"
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store().GetCancellationAsync("cancel-one"));
    }
    [Fact]
    public async Task PendingRevokedAndAdmissionBlockedWithoutExecutionCreation()
    {
        var f = F(true); await f.Setup(true); await f.Application().RequestAsync(f.Command, f.Canceller);
        Assert.Equal(ApprovalStatus.Revoked, (await f.Store().GetAsync("cancel-approval"))!.Status);
        Assert.Equal(0L, await Sql("SELECT count(*) FROM executions")); Assert.Equal(2L, await Sql("SELECT count(*) FROM control_audit WHERE cancel_request_id='cancel-one'"));
    }
    [Fact]
    public async Task TerminalAndImmutableEvidenceRemainHonest()
    {
        var f = F(); await f.Setup(); await using (var owner = (await f.Executions.TryAcquireAsync(f.Command.TaskId))!)
        {
            var e = (await f.Executions.GetAsync(f.Command.TaskId))!;
            await f.Executions.UpdateAsync(e, e with { State = DurableExecutionState.Failed, CompletedAt = f.Clock.Now, Version = 1 }, owner);
        }
        Assert.Equal(CancellationAdmissionStatus.AlreadyTerminal, (await f.Application().RequestAsync(f.Command, f.Canceller)).Status); await NoIntent();
    }
    [Theory]
    [InlineData("CancelControlWritten", false)] [InlineData("CancelCommitted", true)]
    public async Task ActualCancelCrashHasAtomicControlEvidenceAndFreshProcessRead(string checkpoint, bool committed)
    {
        var f = F(); await f.Setup(); using var child = CancellationSchemaTests.Child("m4-cancel", Db, checkpoint); var start = child.StartTime;
        try
        {
            Assert.Equal(checkpoint, await CancellationSchemaTests.Line(child)); Assert.Equal(start, child.StartTime);
            await CancellationSchemaTests.Kill(child);
            if (!committed)
            {
                await NoIntent(); using var reader = CancellationSchemaTests.Child("m4-verify", Db);
                var json = await reader.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20));
                await reader.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, reader.ExitCode);
                Assert.Contains("\"Version\":3", json); Assert.Contains("\"Integrity\":\"ok\"", json);
                output.WriteLine($"Cancel rollback PID={child.Id}, startUTC={start.ToUniversalTime():O}, restartPID={reader.Id}, checkpoint={checkpoint}, control=0, evidence=0, providerPOST=0, integrity=ok");
            }
            else
            {
                using var reader = CancellationSchemaTests.Child("m4-read", Db);
                var result = await reader.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20));
                await reader.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, reader.ExitCode);
                Assert.Contains("runtime-execution", result); Assert.Equal(1L, await Sql("SELECT version FROM task_controls"));
                Assert.Contains("runtime-run", result); Assert.Contains("msg_runtime", result);
                Assert.Contains("\"Status\":\"Unknown\"", result); Assert.Contains("\"ProviderCalls\":0", result);
                output.WriteLine($"Cancel crash PID={child.Id}, startUTC={start.ToUniversalTime():O}, restartPID={reader.Id}, checkpoint={checkpoint}, task=runtime-task, execution=runtime-execution, control=1, evidence=1, providerPOST=0, integrity=ok");
            }
            Assert.Equal("ok", await Sql("PRAGMA integrity_check"));
        }
        finally { await CancellationSchemaTests.Kill(child); }
    }
    [Fact]
    public async Task TwoCancelProcessesCommitOneWinnerWhileSeparateOwnerProcessHoldsLock()
    {
        var f = F(); await f.Setup();
        using var owner = CancellationSchemaTests.Child("m4-owner", Db);
        using var one = CancellationSchemaTests.Child("m4-race", Db, "cancel-one");
        using var two = CancellationSchemaTests.Child("m4-race", Db, "cancel-two");
        try
        {
            Assert.Equal("owned", await CancellationSchemaTests.Line(owner));
            Assert.Equal("ready", await CancellationSchemaTests.Line(one)); Assert.Equal("ready", await CancellationSchemaTests.Line(two));
            await one.StandardInput.WriteLineAsync("go"); await two.StandardInput.WriteLineAsync("go");
            var results = await Task.WhenAll(CancellationSchemaTests.Line(one), CancellationSchemaTests.Line(two));
            Assert.Contains("Recorded", results); Assert.Contains("conflict", results);
            await one.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); await two.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, one.ExitCode); Assert.Equal(0, two.ExitCode); Assert.False(owner.HasExited);
            Assert.Null(await f.Executions.TryAcquireAsync(f.Command.TaskId));
            Assert.Equal(1L, await Sql("SELECT count(*) FROM cancellation_requests")); Assert.Equal(1L, await Sql("SELECT version FROM task_controls"));
            Assert.Equal(1L, await Sql("SELECT count(*) FROM control_audit WHERE code=6"));
            output.WriteLine($"Execution ownerPID={owner.Id}, cancelPIDs={one.Id}/{two.Id}, winnerPID={(results[0] == "Recorded" ? one.Id : two.Id)}, ownerStillHeld=true, control=1, uniqueEvidence=1, providerCreate/Submit/Observe/Abort=0/0/0/0");
            await owner.StandardInput.WriteLineAsync("release");
            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, owner.ExitCode);
        }
        finally { await CancellationSchemaTests.Kill(one); await CancellationSchemaTests.Kill(two); await CancellationSchemaTests.Kill(owner); }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CancelBeforeResumeOrAfterConsumeNeverLaunchesOrResubmits(bool consumeFirst)
    {
        var f = F(true); await f.Setup(true);
        var pending = (await f.Store().GetAsync("cancel-approval"))!;
        var approved = await f.Store().DecideAsync(new(pending.ApprovalId, "decision", 0, pending.BindingDigest, ApprovalChoice.Approve), f.Approval.Operator);
        using var transport = new RuntimeHttpFixture(f.Runtime.Root); using var http = new HttpClient(transport);
        if (!consumeFirst)
        {
            await f.Application().RequestAsync(f.Command, f.Canceller);
            await Assert.ThrowsAnyAsync<Exception>(() => f.Protected(http).ResumeAsync(new(approved.ApprovalId, approved.Version, approved.BindingDigest, f.Runtime.Request), f.Runtime.Caller, f.Approval.Operator));
            Assert.Equal(0L, await Sql("SELECT count(*) FROM executions"));
        }
        else
        {
            var assessment = new MAF.Workflows.External.ExternalExecutionApplication(f.Runtime.Inputs, new ImmutablePolicySource(f.Policy), f, f.Clock).Assess(f.Runtime.Intent, f.Runtime.Caller);
            await using var owner = (await f.Executions.TryAcquireAsync(f.Command.TaskId))!;
            var op = assessment.Operation!; var e = new DurableExecution(op.TaskId, op.RequestFingerprint, op.Scope.ProviderScopeDigest, op.Scope.ResourceId, op.WorkflowRunId,
                new(op.ExecutionId, "OpenCode", ExternalRequestId: op.MessageId), DurableExecutionState.Created, SubmissionPhase.None, f.Clock.Now, f.Clock.Now);
            var continuation = await f.Store().ConsumeOwnedAsync(new(approved.ApprovalId, approved.Version, approved.BindingDigest, f.Runtime.Request), op, e, 0, f.Runtime.Caller, f.Approval.Operator, owner);
            await f.Application().RequestAsync(f.Command, f.Canceller);
            var (_, evidence) = await continuation.ConsumeApprovedAsync(owner);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store().VerifyApprovedEffectAsync(evidence, op, assessment.Decision, e, null, 0, ProviderEffect.Validate, f.Runtime.Caller, owner));
            Assert.Equal(ApprovalStatus.Consumed, (await f.Store().GetAsync(approved.ApprovalId))!.Status);
        }
        Assert.Empty(transport.Requests);
    }
    [Fact]
    public async Task CancellationWhileCreateIsAlreadySentPreventsPromptAndDoesNotIssueAbort()
    {
        var f = F(); await ApprovalSchema.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance);
        await ApprovalSchema.MigrateV2ToV3Async(Db, SyntheticApproval.Maintenance, f); await f.Store().ActivatePolicyAsync();
        using var transport = new RuntimeHttpFixture(f.Runtime.Root); using var http = new HttpClient(transport);
        transport.OnRequest = async (request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/session")
            {
                var before = transport.Requests.Count;
                await f.Application().RequestAsync(f.Command, f.Canceller);
                Assert.Equal(before, transport.Requests.Count);
            }
        };
        await f.Protected(http).RunAsync(f.Runtime.Intent, f.Runtime.Caller);
        Assert.Equal(1, transport.Requests.Count(x => x == "POST /session"));
        Assert.DoesNotContain(transport.Requests, x => x.Contains("prompt_async") || x.Contains("abort"));
        Assert.Equal(1L, await Sql("SELECT count(*) FROM cancellation_requests")); Assert.Equal(1L, await Sql("SELECT version FROM task_controls"));
        Assert.False((await f.Executions.GetAsync(f.Command.TaskId))!.AbortRequested);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActivationAndCancellationHaveExplicitDurableOrder(bool cancelFirst)
    {
        var f = F(); await f.Setup(); var oldInputs = f.Inputs;
        if (cancelFirst) await f.Application().RequestAsync(f.Command, f.Canceller);
        f.Policy = f.Policy with { Epoch = new(2), Version = new(2) }; await f.Store().ActivatePolicyAsync();
        if (cancelFirst)
        {
            Assert.Equal(1, (await f.Store().GetCancellationAsync("cancel-one"))!.Operation.Epoch.Value);
            Assert.Equal(1L, await Sql("SELECT version FROM task_controls"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Application().RequestAsync(f.Command, f.Canceller));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Application(inputs: oldInputs).RequestAsync(f.Command, f.Canceller));
            await NoIntent(); await f.Application().RequestAsync(f.Command, f.Canceller);
        }
        Assert.Equal(2L, await Sql("SELECT epoch FROM policy_authority"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActivationAndCancelCompeteOnWriterWithFrozenAuthorityLease(bool cancelFirst)
    {
        var f = F(); await f.Setup(); var oldInputs = f.Inputs;
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var acquired = new ManualResetEventSlim();
        Task first;
        if (cancelFirst)
            first = Task.Run(() => f.Application(f.Store(point =>
            { if (point == "CancelEvidenceInserted") { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("CancelGate"); } }))
                .RequestAsync(f.Command, f.Canceller));
        else
        {
            f.Policy = f.Policy with { Epoch = new(2), Version = new(2) };
            var sql = new SqliteApprovalStore(Db, f.Authority(), f.Clock, 10000, point =>
            { if (point == ApprovalCheckpoint.BeforeCommit) { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("ActivationGate"); } }, ApprovalStoreProfile.ExplicitV3);
            first = Task.Run(() => sql.ActivatePolicyAsync());
        }
        Task? second = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            if (cancelFirst) f.Policy = f.Policy with { Epoch = new(2), Version = new(2) };
            f.Acquired = acquired.Set;
            second = cancelFirst ? Task.Run(() => f.Store().ActivatePolicyAsync())
                : Task.Run(async () => { await Assert.ThrowsAsync<InvalidOperationException>(() => f.Application(inputs: oldInputs).RequestAsync(f.Command, f.Canceller)); });
            Assert.True(acquired.Wait(TimeSpan.FromSeconds(10))); release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(10)); await second.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2L, await Sql("SELECT epoch FROM policy_authority"));
            if (cancelFirst) Assert.Equal(1, (await f.Store().GetCancellationAsync("cancel-one"))!.Operation.Epoch.Value);
            else await NoIntent();
        }
        finally { release.Set(); await first; if (second is not null) await second; }
    }
    [Theory]
    [InlineData("admission")] [InlineData("consume")] [InlineData("dispatch")]
    public async Task CompetingWriterCommitsBeforeCancelWithoutWaitingForExecutionOwnership(string kind)
    {
        var f = F(kind == "consume");
        if (kind == "admission")
        {
            await ApprovalSchema.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance);
            await ApprovalSchema.MigrateV2ToV3Async(Db, SyntheticApproval.Maintenance, f); await f.Store().ActivatePolicyAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Application().RequestAsync(f.Command, f.Canceller)); // no arbitrary tombstone
        }
        else await f.Setup(kind == "consume");
        ApprovalRecord? approved = null;
        if (kind == "consume")
        {
            var pending = (await f.Store().GetAsync("cancel-approval"))!;
            approved = await f.Store().DecideAsync(new(pending.ApprovalId, "decision", 0, pending.BindingDigest, ApprovalChoice.Approve), f.Approval.Operator);
        }
        await using var owner = (await f.Executions.TryAcquireAsync(f.Command.TaskId))!;
        var assessment = new MAF.Workflows.External.ExternalExecutionApplication(f.Runtime.Inputs, new ImmutablePolicySource(f.Policy), f, f.Clock).Assess(f.Runtime.Intent, f.Runtime.Caller);
        var op = assessment.Operation!; var initial = new DurableExecution(op.TaskId, op.RequestFingerprint, op.Scope.ProviderScopeDigest, op.Scope.ResourceId, op.WorkflowRunId,
            new(op.ExecutionId, "OpenCode", ExternalRequestId: op.MessageId), DurableExecutionState.Created, SubmissionPhase.None, f.Clock.Now, f.Clock.Now);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var acquired = new ManualResetEventSlim();
        var point = kind == "admission" ? ApprovalCheckpoint.ExecutionInserted : kind == "consume" ? ApprovalCheckpoint.ApprovalConsumed : ApprovalCheckpoint.DispatchIntentWritten;
        var coordinator = new SqliteApprovalStore(Db, f.Authority(), f.Clock, 10000, p =>
        {
            if (p != point) return; entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("WriterGate");
        }, ApprovalStoreProfile.ExplicitV3);
        var writer = Task.Run(async () =>
        {
            if (kind == "admission") await new MAF.Workflows.External.AtomicAdmissionApplication(f.Runtime.Inputs, new ImmutablePolicySource(f.Policy), f, coordinator, f.Clock).AdmitAsync(f.Runtime.Intent, f.Runtime.Caller, owner);
            else if (kind == "consume") await coordinator.ConsumeOwnedAsync(new(approved!.ApprovalId, approved.Version, approved.BindingDigest, f.Runtime.Request), op, initial, 0, f.Runtime.Caller, f.Approval.Operator, owner);
            else await coordinator.VerifyEffectAsync(op, assessment.Decision, initial, initial with { State = DurableExecutionState.SessionCreating, SubmissionPhase = SubmissionPhase.SessionCreationIntended, Version = 1 }, 0, ProviderEffect.CreateSession, f.Runtime.Caller, owner);
        });
        Task<CancellationAdmissionResult>? cancel = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10))); f.Acquired = acquired.Set;
            cancel = Task.Run(() => f.Application().RequestAsync(f.Command, f.Canceller));
            Assert.True(acquired.Wait(TimeSpan.FromSeconds(10))); release.Set();
            await writer.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(CancellationAdmissionStatus.Recorded, (await cancel.WaitAsync(TimeSpan.FromSeconds(10))).Status);
            Assert.Equal(1L, await Sql("SELECT count(*) FROM executions")); Assert.Equal(1L, await Sql("SELECT version FROM task_controls"));
            Assert.NotNull(await f.Store().GetCancellationAsync("cancel-one"));
            Assert.Null(await f.Executions.TryAcquireAsync(f.Command.TaskId));
        }
        finally { release.Set(); await writer; if (cancel is not null) await cancel; }
    }
    [Fact]
    public async Task CancelFirstPreventsDispatchIntentAndTokenCancellationCreatesNoIntent()
    {
        var f = F(); await f.Setup(); using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Application().RequestAsync(f.Command, f.Canceller, stop.Token)); await NoIntent();
        await f.Application().RequestAsync(f.Command, f.Canceller);
        await using var owner = (await f.Executions.TryAcquireAsync(f.Command.TaskId))!;
        var e = (await f.Executions.GetAsync(f.Command.TaskId))!;
        var assessment = new MAF.Workflows.External.ExternalExecutionApplication(f.Runtime.Inputs, new ImmutablePolicySource(f.Policy), f, f.Clock).Assess(f.Runtime.Intent, f.Runtime.Caller);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store().VerifyEffectAsync(assessment.Operation!, assessment.Decision, e,
            e with { State = DurableExecutionState.SessionCreating, SubmissionPhase = SubmissionPhase.SessionCreationIntended, Version = 1 }, 0, ProviderEffect.CreateSession, f.Runtime.Caller, owner));
        Assert.Equal(DurableExecutionState.Created, (await f.Executions.GetAsync(e.TaskId))!.State);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
