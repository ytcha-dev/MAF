using System.Diagnostics;
using MAF.ApprovalAcceptance;
using MAF.Core;
using MAF.Core.Policy;
using MAF.Persistence.Sqlite;
using MAF.Workflows.External;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace MAF.Tests;

public sealed class CancellationSchemaTests(ITestOutputHelper output) : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "maf-cancel-schema-" + Guid.NewGuid().ToString("N"));
    string Db => Path.Combine(root, "synthetic.db");
    SyntheticCancellation F(bool approved = true) => new(Db, approved);
    async Task<object?> Sql(string sql)
    {
        await using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Db, Pooling = false }.ToString());
        await c.OpenAsync(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return await cmd.ExecuteScalarAsync();
    }
    async Task<string> Bytes()
    {
        await using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Db, Pooling = false }.ToString());
        await c.OpenAsync(); return await ApprovalSchema.OldBytes(c, null, default);
    }
    async Task<SyntheticCancellation> V2(bool legacy = false)
    {
        var f = F(); await ApprovalSchema.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance);
        var old = new SqliteApprovalStore(Db, f.Authority(), f.Clock);
        await old.ActivatePolicyAsync();
        await new DurableApprovalApplication(f.Runtime.Inputs, new ImmutablePolicySource(f.Policy), f,
            new SqliteExecutionStore(Db, ExecutionStoreProfile.ExplicitV2), old, f.Clock).RequestAsync("cancel-approval", f.Runtime.Intent, f.Runtime.Caller);
        if (legacy) await old.RequestCancellationAsync("cancel-approval", "legacy-cancel", 0, f.Runtime.Caller);
        return f;
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MigrationPreservesAllOriginalValuesIncludingLegacyIntent(bool legacy)
    {
        var f = await V2(legacy); var before = await Bytes();
        await ApprovalSchema.MigrateV2ToV3Async(Db, SyntheticApproval.Maintenance, f);
        Assert.Equal(before, await Bytes()); Assert.Equal(3L, await Sql("PRAGMA user_version"));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM cancellation_requests"));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM control_audit WHERE cancel_request_id IS NOT NULL"));
        if (legacy)
        {
            Assert.Equal(CancellationAdmissionStatus.LegacyIntentUnverified, (await f.Application().RequestAsync(f.Command, f.Canceller)).Status);
            Assert.Equal("legacy-cancel", (await f.Store().GetControlAsync(f.Command.TaskId))!.CancelRequestId);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store().RequestCancellationAsync("cancel-approval", "new", 0, f.Runtime.Caller));
    }
    [Theory]
    [InlineData(ApprovalStatus.Approved)] [InlineData(ApprovalStatus.Rejected)] [InlineData(ApprovalStatus.Expired)]
    [InlineData(ApprovalStatus.Revoked)] [InlineData(ApprovalStatus.Consumed)]
    public async Task MigrationPreservesEveryApprovalLifecycle(ApprovalStatus status)
    {
        var f = await V2(); var old = new SqliteApprovalStore(Db, f.Authority(), f.Clock); var pending = (await old.GetAsync("cancel-approval"))!;
        var row = pending;
        if (status is ApprovalStatus.Approved or ApprovalStatus.Consumed or ApprovalStatus.Rejected)
            row = await old.DecideAsync(new(pending.ApprovalId, "original-decision", 0, pending.BindingDigest,
                status == ApprovalStatus.Rejected ? ApprovalChoice.Reject : ApprovalChoice.Approve), f.Approval.Operator);
        if (status == ApprovalStatus.Expired) { f.Clock.Now = pending.ExpiresAt; row = await old.ExpireAsync(pending.ApprovalId, 0); }
        if (status == ApprovalStatus.Revoked) row = await old.RevokeAsync(pending.ApprovalId, 0, f.Approval.Operator);
        if (status == ApprovalStatus.Consumed)
        {
            var op = row.Operation; var at = f.Clock.Now;
            await old.ConsumeAsync(row.ApprovalId, row.Version, 0, row.BindingDigest, new(op.TaskId, op.RequestFingerprint,
                op.Scope.ProviderScopeDigest, op.Scope.ResourceId, op.WorkflowRunId, new(op.ExecutionId, "OpenCode", ExternalRequestId: op.MessageId),
                DurableExecutionState.Created, SubmissionPhase.None, at, at), f.Runtime.Caller);
        }
        var bytes = await Bytes(); await ApprovalSchema.MigrateV2ToV3Async(Db, SyntheticApproval.Maintenance, f);
        Assert.Equal(bytes, await Bytes()); Assert.Equal(status, (await f.Store().GetAsync(pending.ApprovalId))!.Status);
    }
    [Theory]
    [InlineData(DurableExecutionState.Created)] [InlineData(DurableExecutionState.SessionCreating)] [InlineData(DurableExecutionState.ReadyToSubmit)]
    [InlineData(DurableExecutionState.SubmissionIntended)] [InlineData(DurableExecutionState.SubmissionUncertain)] [InlineData(DurableExecutionState.Running)]
    [InlineData(DurableExecutionState.WaitingForApproval)] [InlineData(DurableExecutionState.Unknown)] [InlineData(DurableExecutionState.Completed)]
    [InlineData(DurableExecutionState.Partial)] [InlineData(DurableExecutionState.Failed)] [InlineData(DurableExecutionState.Cancelled)]
    public async Task MigrationPreservesExecutionSnapshotBytesAndTerminalTruth(DurableExecutionState target)
    {
        var f = F(false); await ApprovalSchema.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance);
        var sql = new SqliteApprovalStore(Db, f.Authority(), f.Clock); await sql.ActivatePolicyAsync();
        var executions = new SqliteExecutionStore(Db, ExecutionStoreProfile.ExplicitV2);
        await using (var owner = (await executions.TryAcquireAsync(f.Command.TaskId))!)
        {
            var continuation = await new AtomicAdmissionApplication(f.Runtime.Inputs, new ImmutablePolicySource(f.Policy), f, sql, f.Clock).AdmitAsync(f.Runtime.Intent, f.Runtime.Caller, owner);
            var current = await continuation.ConsumeAsync(owner);
            async Task Next(DurableExecutionState state, SubmissionPhase phase, bool session = false)
            {
                var next = current with { State = state, SubmissionPhase = phase, Version = current.Version + 1,
                    Handle = session ? current.Handle with { ExternalSessionId = "ses_runtime" } : current.Handle,
                    CompletedAt = state is DurableExecutionState.Completed or DurableExecutionState.Partial or DurableExecutionState.Failed or DurableExecutionState.Cancelled ? f.Clock.Now : null };
                await executions.UpdateAsync(current, next, owner); current = next;
            }
            if (target != DurableExecutionState.Created)
            {
                await Next(DurableExecutionState.SessionCreating, SubmissionPhase.SessionCreationIntended);
                if (target != DurableExecutionState.SessionCreating)
                {
                    await Next(DurableExecutionState.ReadyToSubmit, SubmissionPhase.SessionKnown, true);
                    if (target != DurableExecutionState.ReadyToSubmit)
                    {
                        await Next(DurableExecutionState.SubmissionIntended, SubmissionPhase.PromptIntended);
                        if (target != DurableExecutionState.SubmissionIntended) await Next(target, SubmissionPhase.PromptIntended);
                    }
                }
            }
        }
        var before = await Bytes(); await ApprovalSchema.MigrateV2ToV3Async(Db, SyntheticApproval.Maintenance, f);
        Assert.Equal(before, await Bytes()); Assert.Equal(target, (await f.Executions.GetAsync(f.Command.TaskId))!.State);
    }
    [Theory]
    [InlineData("V3Validated")] [InlineData("V3TableCreated")] [InlineData("V3AuditAltered")] [InlineData("V3VersionWritten")]
    public async Task MigrationFaultRollsBackEverySchemaChange(string point)
    {
        var f = await V2(); var before = await Bytes();
        await Assert.ThrowsAsync<IOException>(() => ApprovalSchema.MigrateV3Async(Db, SyntheticApproval.Maintenance, f,
            p => { if (p == point) throw new IOException("synthetic"); }));
        Assert.Equal(2L, await Sql("PRAGMA user_version")); Assert.Equal(before, await Bytes());
        Assert.Equal(0L, await Sql("SELECT count(*) FROM sqlite_master WHERE name='cancellation_requests'"));
        Assert.Equal("ok", await Sql("PRAGMA integrity_check"));
    }
    [Theory]
    [InlineData("auth")] [InlineData("attestations")] [InlineData("owner")] [InlineData("schema-lock")]
    [InlineData("future")] [InlineData("partial")] [InlineData("extra-object")] [InlineData("corrupt-control")]
    [InlineData("corrupt-approval")] [InlineData("cancel-operation")]
    public async Task MigrationRejectsUnsafeOrIncompatibleDatabaseWithoutRepair(string variant)
    {
        var f = await V2(); IAsyncDisposable? owner = null; FileStream? held = null;
        try
        {
            if (variant == "owner") owner = await new SqliteExecutionStore(Db, ExecutionStoreProfile.ExplicitV2).TryAcquireAsync(f.Command.TaskId);
            if (variant == "schema-lock") held = new FileStream(Path.Combine(Db + ".locks", ".schema.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (variant == "future") await Sql("PRAGMA user_version=99");
            if (variant == "partial") await Sql("ALTER TABLE control_audit ADD COLUMN cancel_request_id TEXT");
            if (variant == "extra-object") await Sql("CREATE TABLE unexpected(value TEXT)");
            if (variant == "corrupt-control") await Sql("UPDATE task_controls SET cancel_id='incomplete'");
            if (variant == "corrupt-approval") await Sql("UPDATE approval_requests SET version=99");
            if (variant == "cancel-operation") await Sql("UPDATE authorization_operations SET kind=3");
            var before = await Bytes();
            await Assert.ThrowsAnyAsync<Exception>(() => ApprovalSchema.MigrateV2ToV3Async(Db,
                variant == "attestations" ? new(false, true, true) : SyntheticApproval.Maintenance, variant == "auth" ? null : f));
            Assert.Equal(before, await Bytes()); Assert.Equal(0L, await Sql("SELECT count(*) FROM sqlite_master WHERE name='cancellation_requests'"));
        }
        finally { held?.Dispose(); if (owner is not null) await owner.DisposeAsync(); }
    }
    [Theory]
    [InlineData(2, ExecutionStoreProfile.LegacyV1, false)] [InlineData(2, ExecutionStoreProfile.ExplicitV2, true)] [InlineData(2, ExecutionStoreProfile.ExplicitV3, false)]
    [InlineData(3, ExecutionStoreProfile.LegacyV1, false)] [InlineData(3, ExecutionStoreProfile.ExplicitV2, false)] [InlineData(3, ExecutionStoreProfile.ExplicitV3, true)]
    public async Task ExecutionProfilesAreExact(int version, ExecutionStoreProfile profile, bool allowed)
    {
        var f = await V2(); if (version == 3) await ApprovalSchema.MigrateV2ToV3Async(Db, SyntheticApproval.Maintenance, f);
        var store = new SqliteExecutionStore(Db, profile);
        if (allowed) Assert.Null(await store.GetAsync(f.Command.TaskId));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAsync(f.Command.TaskId));
    }
    [Theory]
    [InlineData(2, ApprovalStoreProfile.ExplicitV2, true)] [InlineData(2, ApprovalStoreProfile.ExplicitV3, false)]
    [InlineData(3, ApprovalStoreProfile.ExplicitV2, false)] [InlineData(3, ApprovalStoreProfile.ExplicitV3, true)]
    public async Task ApprovalProfilesAreExact(int version, ApprovalStoreProfile profile, bool allowed)
    {
        var f = await V2(); if (version == 3) await ApprovalSchema.MigrateV2ToV3Async(Db, SyntheticApproval.Maintenance, f);
        var store = new SqliteApprovalStore(Db, f.Authority(), f.Clock, profile);
        if (allowed) Assert.NotNull(await store.GetAsync("cancel-approval"));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAsync("cancel-approval"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FullProtectedDirectAllowAndApprovedResumeUseV3(bool approved)
    {
        var f = F(approved); await ApprovalSchema.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance);
        await ApprovalSchema.MigrateV2ToV3Async(Db, SyntheticApproval.Maintenance, f); await f.Store().ActivatePolicyAsync();
        using var transport = new RuntimeHttpFixture(f.Runtime.Root); using var http = new HttpClient(transport);
        ExternalWorkflowRun result;
        if (approved)
        {
            var app = new DurableApprovalApplication(f.Runtime.Inputs, new ImmutablePolicySource(f.Policy), f, f.Executions, f.Store(), f.Clock);
            var pending = await app.RequestAsync("cancel-approval", f.Runtime.Intent, f.Runtime.Caller);
            var decision = await app.DecideAsync(new(pending.ApprovalId, "decision", 0, pending.BindingDigest, ApprovalChoice.Approve), f.Approval.Operator);
            result = await f.Protected(http).ResumeAsync(new(decision.ApprovalId, decision.Version, decision.BindingDigest, f.Runtime.Request), f.Runtime.Caller, f.Approval.Operator);
            Assert.Equal(ApprovalStatus.Consumed, (await f.Store().GetAsync(decision.ApprovalId))!.Status);
        }
        else result = await f.Protected(http).RunAsync(f.Runtime.Intent, f.Runtime.Caller);
        Assert.Equal(ExternalExecutionStatus.Completed, result.Result.Status);
        Assert.Equal(1, transport.Requests.Count(x => x == "POST /session"));
        Assert.Equal(1, transport.Requests.Count(x => x.EndsWith("/prompt_async"))); Assert.DoesNotContain(transport.Requests, x => x.Contains("abort"));
        var count = transport.Requests.Count;
        await f.Application().RequestAsync(f.Command, f.Canceller); Assert.Equal(count, transport.Requests.Count);
        var recovery = await f.Protected(http).RunAsync(new(ExternalAdmissionMode.Inspect, f.Command.TaskId, f.Runtime.Request.Timeout), f.Runtime.Caller, true);
        Assert.Equal(result.Result.Handle, recovery.Result.Handle);
        Assert.Equal(2, transport.Requests.Count(x => x.StartsWith("POST ")));
    }
    [Theory]
    [InlineData("V3TableCreated", false)] [InlineData("V3AuditAltered", false)] [InlineData("V3VersionWritten", false)] [InlineData("V3Committed", true)]
    public async Task ActualMigrationProcessTerminationIsAtomic(string checkpoint, bool committed)
    {
        await V2(); var before = await Bytes(); using var child = Child("m4-migrate", Db, checkpoint); var start = child.StartTime;
        try
        {
            Assert.Equal(checkpoint, await Line(child)); Assert.Equal(start, child.StartTime); await Kill(child);
            Assert.Equal(committed ? 3L : 2L, await Sql("PRAGMA user_version")); Assert.Equal(before, await Bytes());
            Assert.Equal("ok", await Sql("PRAGMA integrity_check"));
            using var reader = Child("m4-verify", Db);
            var json = await reader.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await reader.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, reader.ExitCode);
            using var result = System.Text.Json.JsonDocument.Parse(json);
            Assert.Equal(committed ? 3 : 2, result.RootElement.GetProperty("Version").GetInt32());
            Assert.Equal("ok", result.RootElement.GetProperty("Integrity").GetString());
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(before))), result.RootElement.GetProperty("OriginalDigest").GetString());
            output.WriteLine($"Migration crash PID={child.Id}, startUTC={start.ToUniversalTime():O}, restartPID={reader.Id}, checkpoint={checkpoint}, version={(committed ? 3 : 2)}, originalBytes=true, integrity=ok");
        }
        finally { await Kill(child); }
    }
    internal static Process Child(params string[] args)
    {
        var config = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.Name;
        var dll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../MAF.ApprovalAcceptance/bin", config, "net10.0/MAF.ApprovalAcceptance.dll"));
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(dll); foreach (var arg in args) info.ArgumentList.Add(arg); return Process.Start(info)!;
    }
    internal static async Task<string?> Line(Process process) => await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
    internal static async Task Kill(Process process)
    { if (!process.HasExited) process.Kill(entireProcessTree: false); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
