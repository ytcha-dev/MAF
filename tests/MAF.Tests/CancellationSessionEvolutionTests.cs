using MAF.ApprovalAcceptance;
using MAF.Core;
using MAF.Core.Policy;
using MAF.Persistence.Sqlite;
using MAF.Workflows.External;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace MAF.Tests;

// Characterization of the B0 design blocker, NOT acceptance of successful reconciliation.
// Remove the expected read failure only with an authorized correlation-evidence repair.
public sealed class CancellationSessionEvolutionTests(ITestOutputHelper output) : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "maf-cancel-evolution-" + Guid.NewGuid().ToString("N"));
    string Db => Path.Combine(root, "synthetic.db");
    async Task<object?> Sql(string sql)
    {
        await using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Db, Pooling = false }.ToString());
        await c.OpenAsync(); using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OriginalInFlightCreateResponseMakesHistoricalEvidenceUnreadable(bool approved)
    {
        var f = new SyntheticCancellation(Db, approved);
        await ApprovalSchema.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance);
        await ApprovalSchema.MigrateV2ToV3Async(Db, SyntheticApproval.Maintenance, f);
        await f.Store().ActivatePolicyAsync();
        using var transport = new RuntimeHttpFixture(f.Runtime.Root); using var http = new HttpClient(transport);
        CancellationEvidence? historical = null; string? originalJson = null;
        transport.OnRequest = async (request, _) =>
        {
            if (request.Method != HttpMethod.Post || request.RequestUri!.AbsolutePath != "/session") return;
            var intended = (await f.Executions.GetAsync(f.Command.TaskId))!;
            Assert.Equal(SubmissionPhase.SessionCreationIntended, intended.SubmissionPhase);
            Assert.Null(intended.Handle.ExternalSessionId);
            historical = (await f.Application().RequestAsync(f.Command, f.Canceller)).Evidence!;
            Assert.Null(historical.Operation.ProviderSessionId);
            Assert.Equal(historical.BindingDigest, (await f.Store().GetCancellationAsync("cancel-one"))!.BindingDigest);
            Assert.Equal(1, (await f.Store().GetControlAsync(f.Command.TaskId))!.Version);
            originalJson = (string?)await Sql("SELECT evidence_json FROM cancellation_requests");
        };
        if (approved)
        {
            var app = new DurableApprovalApplication(f.Runtime.Inputs, new ImmutablePolicySource(f.Policy), f, f.Executions, f.Store(), f.Clock);
            var pending = await app.RequestAsync("cancel-approval", f.Runtime.Intent, f.Runtime.Caller);
            var decision = await app.DecideAsync(new(pending.ApprovalId, "decision", 0, pending.BindingDigest, ApprovalChoice.Approve), f.Approval.Operator);
            await f.Protected(http).ResumeAsync(new(decision.ApprovalId, decision.Version, decision.BindingDigest, f.Runtime.Request), f.Runtime.Caller, f.Approval.Operator);
            Assert.Equal(ApprovalStatus.Consumed, (await f.Store().GetAsync(decision.ApprovalId))!.Status);
        }
        else await f.Protected(http).RunAsync(f.Runtime.Intent, f.Runtime.Caller);

        Assert.NotNull(historical);
        var current = (await f.Executions.GetAsync(f.Command.TaskId))!;
        Assert.Equal("ses_runtime", current.Handle.ExternalSessionId);
        Assert.Equal(SubmissionPhase.SessionKnown, current.SubmissionPhase);
        Assert.Equal(historical.Operation.ExecutionId, current.Handle.ExecutionId);
        Assert.Equal(historical.Operation.WorkflowRunId, current.WorkflowRunId);
        Assert.Equal(historical.Operation.MessageId, current.Handle.ExternalRequestId);
        Assert.Equal(originalJson, await Sql("SELECT evidence_json FROM cancellation_requests"));
        Assert.Equal(historical.BindingDigest, await Sql("SELECT binding_digest FROM cancellation_requests"));
        Assert.Equal("CancellationTargetMismatch", (await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store().GetCancellationAsync("cancel-one"))).Message);
        Assert.Equal("CancellationTargetMismatch", (await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store().GetControlAsync(f.Command.TaskId))).Message);
        // Fresh composition, current independent Inspect rights: fails before any new HTTP.
        var before = transport.Requests.Count;
        await Assert.ThrowsAnyAsync<Exception>(() => f.Protected(http).RunAsync(new(ExternalAdmissionMode.Inspect, f.Command.TaskId, f.Runtime.Request.Timeout), f.Runtime.Caller, true));
        Assert.Equal(before, transport.Requests.Count);
        Assert.Equal(current, await f.Executions.GetAsync(f.Command.TaskId));
        Assert.Equal(1, transport.Requests.Count(x => x == "POST /session"));
        Assert.DoesNotContain(transport.Requests, x => x.Contains("prompt_async") || x.Contains("abort"));
        Assert.False(current.AbortRequested);
        Assert.Equal("ok", await Sql("PRAGMA integrity_check"));
        output.WriteLine($"B0 reproduced approved={approved}; task={current.TaskId}; execution={current.Handle.ExecutionId}; run={current.WorkflowRunId}; message={current.Handle.ExternalRequestId}; historicalSession=null; currentSession={current.Handle.ExternalSessionId}; phase={current.SubmissionPhase}; control=1; Create/Prompt/Abort=1/0/0; GetCancellation/GetControl=CancellationTargetMismatch; Inspect blocked before HTTP; historical evidence unchanged.");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OwnerUpdateCanPersistUnprovenSessionButStrictEvidenceReadRejectsIt(bool precedingPhase)
    {
        var f = new SyntheticCancellation(Db); await f.Setup();
        await using var owner = (await f.Executions.TryAcquireAsync(f.Command.TaskId))!;
        var initial = (await f.Executions.GetAsync(f.Command.TaskId))!;
        var intended = initial with { State = DurableExecutionState.SessionCreating, SubmissionPhase = SubmissionPhase.SessionCreationIntended, Version = initial.Version + 1 };
        if (precedingPhase) await f.Executions.UpdateAsync(initial, intended, owner);
        await f.Application().RequestAsync(f.Command, f.Canceller);
        var before = precedingPhase ? intended : initial;
        var injected = before with { State = DurableExecutionState.ReadyToSubmit, SubmissionPhase = SubmissionPhase.SessionKnown,
            Handle = before.Handle with { ExternalSessionId = "ses_unproven" }, Version = before.Version + 1 };
        if (!precedingPhase)
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Executions.UpdateAsync(before, injected, owner));
        else
        {
            // A phase-valid owned update is not proof of an authorized HTTP response.
            await f.Executions.UpdateAsync(before, injected, owner);
            Assert.Equal("ses_unproven", (await f.Executions.GetAsync(f.Command.TaskId))!.Handle.ExternalSessionId);
            Assert.Equal("CancellationTargetMismatch", (await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store().GetCancellationAsync("cancel-one"))).Message);
        }
        Assert.Equal(0L, await Sql("SELECT count(*) FROM control_audit WHERE code=0 AND version>0"));
        output.WriteLine($"B0 unproven session precedingPhase={precedingPhase}; provider calls=0; no dispatch audit; phase-valid owner write={(precedingPhase ? "accepted" : "rejected")}; strict cancellation evidence stays fail-closed.");
    }

    [Theory]
    [InlineData("session")] [InlineData("task")] [InlineData("execution")] [InlineData("message")] [InlineData("run")]
    public async Task KnownIdentityCannotBeReplacedAfterCancellation(string field)
    {
        var f = new SyntheticCancellation(Db); await f.Setup();
        await using var owner = (await f.Executions.TryAcquireAsync(f.Command.TaskId))!;
        var initial = (await f.Executions.GetAsync(f.Command.TaskId))!;
        var intended = initial with { State = DurableExecutionState.SessionCreating, SubmissionPhase = SubmissionPhase.SessionCreationIntended, Version = 1 };
        await f.Executions.UpdateAsync(initial, intended, owner);
        var known = intended with { State = DurableExecutionState.ReadyToSubmit, SubmissionPhase = SubmissionPhase.SessionKnown,
            Handle = intended.Handle with { ExternalSessionId = "ses_original" }, Version = 2 };
        await f.Executions.UpdateAsync(intended, known, owner);
        var evidence = (await f.Application().RequestAsync(f.Command, f.Canceller)).Evidence!;
        var altered = known with { Version = 3 };
        altered = field switch
        {
            "session" => altered with { Handle = altered.Handle with { ExternalSessionId = "ses_other" } },
            "task" => altered with { TaskId = "other-task" },
            "execution" => altered with { Handle = altered.Handle with { ExecutionId = "other-execution" } },
            "message" => altered with { Handle = altered.Handle with { ExternalRequestId = "msg_other" } },
            _ => altered with { WorkflowRunId = "other-run" }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Executions.UpdateAsync(known, altered, owner));
        Assert.Equal(known, await f.Executions.GetAsync(f.Command.TaskId));
        Assert.Equal(evidence.BindingDigest, (await f.Store().GetCancellationAsync("cancel-one"))!.BindingDigest);
        Assert.Equal("ses_original", (await f.Store().GetCancellationAsync("cancel-one"))!.Operation.ProviderSessionId);
    }

    [Fact]
    public async Task GenuineDispatchAuditCannotProveWhichSessionCameBackAfterLostResponse()
    {
        var f = new SyntheticCancellation(Db);
        await ApprovalSchema.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance);
        await ApprovalSchema.MigrateV2ToV3Async(Db, SyntheticApproval.Maintenance, f);
        await f.Store().ActivatePolicyAsync();
        using var transport = new RuntimeHttpFixture(f.Runtime.Root) { LoseCreateResponse = true };
        using var http = new HttpClient(transport);
        transport.OnRequest = async (request, _) =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/session")
                await f.Application().RequestAsync(f.Command, f.Canceller);
        };
        await f.Protected(http).RunAsync(f.Runtime.Intent, f.Runtime.Caller);
        var before = (await f.Executions.GetAsync(f.Command.TaskId))!;
        Assert.Equal(SubmissionPhase.SessionCreationIntended, before.SubmissionPhase);
        Assert.Null(before.Handle.ExternalSessionId);
        Assert.Equal(1L, await Sql("SELECT count(*) FROM control_audit WHERE code=0 AND version=1"));
        Assert.Equal(1L, await Sql("SELECT (SELECT sequence FROM control_audit WHERE code=0 AND version=1) < (SELECT sequence FROM control_audit WHERE code=6)"));
        Assert.NotNull(await f.Store().GetCancellationAsync("cancel-one"));
        await using (var owner = (await f.Executions.TryAcquireAsync(f.Command.TaskId))!)
        {
            var injected = before with { State = DurableExecutionState.Unknown, SubmissionPhase = SubmissionPhase.SessionKnown,
                Handle = before.Handle with { ExternalSessionId = "ses_unrelated" }, Version = before.Version + 1 };
            await f.Executions.UpdateAsync(before, injected, owner);
        }
        Assert.Equal("CancellationTargetMismatch", (await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store().GetCancellationAsync("cancel-one"))).Message);
        Assert.Equal(1, transport.Requests.Count(x => x == "POST /session"));
        Assert.DoesNotContain(transport.Requests, x => x.Contains("prompt_async") || x.Contains("abort"));
        output.WriteLine("B0 durable genuine Create intent audit precedes Cancel audit, but a phase-valid owner update can still introduce ses_unrelated after a lost response. Audit has no response/session receipt. Strict evidence read rejects it; null wildcard plus prior intent would not distinguish this case.");
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
