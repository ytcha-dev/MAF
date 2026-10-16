using System.Diagnostics;
using System.Text.Json;
using MAF.ApprovalAcceptance;
using MAF.Core;
using MAF.Core.Policy;
using MAF.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace MAF.Tests;

public sealed class SessionReceiptMigrationTests(ITestOutputHelper output) : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "maf-v4-migration-" + Guid.NewGuid().ToString("N"));
    string Db => Path.Combine(root, "synthetic.db");
    async Task<object?> Sql(string sql)
    { await using var c = new SqliteConnection(ApprovalDatabase.ConnectionString(Db, SqliteOpenMode.ReadWrite)); await c.OpenAsync(); return await ApprovalDatabase.Scalar(c, null, sql, default); }
    async Task<string> Bytes()
    { await using var c = new SqliteConnection(ApprovalDatabase.ConnectionString(Db, SqliteOpenMode.ReadWrite)); await c.OpenAsync(); return await ApprovalSchema.V3Bytes(c, null, default); }
    async Task<SyntheticCancellation> V3(bool approval = false, bool cancel = false)
    {
        var f = new SyntheticCancellation(Db, approval); await f.Setup(pending: approval);
        if (cancel) await f.Application().RequestAsync(f.Command, f.Canceller);
        return f;
    }
    [Theory] [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task MigrationPreservesAllV3ColumnsAndDoesNotInventReceipts(bool approval, bool cancel)
    {
        var f = await V3(approval, cancel); var before = await Bytes();
        await AuthorizedApprovalMaintenance.MigrateV3ToV4Async(Db, SyntheticApproval.Maintenance, f);
        Assert.Equal(before, await Bytes()); Assert.Equal(4L, await Sql("PRAGMA user_version"));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM execution_session_receipts")); Assert.Equal(0L, await Sql("SELECT count(*) FROM cancellation_deliveries"));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM executions WHERE session_receipt_required<>0")); Assert.Equal("ok", await Sql("PRAGMA integrity_check"));
        var newStore = new SqliteApprovalStore(Db, f.Authority(), f.Clock, ApprovalStoreProfile.ExplicitV4);
        if (cancel) Assert.NotNull(await newStore.GetCancellationAsync("cancel-one"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store().GetControlAsync(f.Command.TaskId));
    }
    [Theory]
    [InlineData("V4Validated")] [InlineData("V4ProfileAdded")] [InlineData("V4ReceiptCreated")]
    [InlineData("V4DeliveryCreated")] [InlineData("V4VersionWritten")]
    public async Task EveryMigrationFaultRollsBackWholeDelta(string point)
    {
        var f = await V3(cancel: true); var before = await Bytes();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApprovalSchema.MigrateV4Async(Db, SyntheticApproval.Maintenance, f,
            p => { if (p == point) throw new InvalidOperationException("SyntheticMigrationFault"); }));
        Assert.Equal(3L, await Sql("PRAGMA user_version")); Assert.Equal(before, await Bytes());
        Assert.Equal(0L, await Sql("SELECT count(*) FROM sqlite_master WHERE name='execution_session_receipts'"));
    }
    public static IEnumerable<object[]> States() => Enum.GetValues<DurableExecutionState>().Select(s => new object[] { s });
    [Theory] [MemberData(nameof(States))]
    public async Task EveryHistoricalExecutionStateKeepsExactSnapshotBytes(DurableExecutionState state)
    {
        var f = await V3(); var e = (await f.Executions.GetAsync(f.Command.TaskId))!;
        var phase = state switch
        {
            DurableExecutionState.Created or DurableExecutionState.Failed => SubmissionPhase.None,
            DurableExecutionState.SessionCreating => SubmissionPhase.SessionCreationIntended,
            DurableExecutionState.ReadyToSubmit or DurableExecutionState.Unknown => SubmissionPhase.SessionKnown,
            DurableExecutionState.SubmissionIntended or DurableExecutionState.SubmissionUncertain => SubmissionPhase.PromptIntended,
            _ => SubmissionPhase.Acknowledged
        };
        var snapshot = e with { State = state, SubmissionPhase = phase, Version = (long)phase,
            Handle = e.Handle with { ExternalSessionId = phase >= SubmissionPhase.SessionKnown ? "ses_legacy" : null },
            CompletedAt = state is DurableExecutionState.Completed or DurableExecutionState.Partial or DurableExecutionState.Failed or DurableExecutionState.Cancelled ? f.Clock.Now : null };
        await using (var c = new SqliteConnection(ApprovalDatabase.ConnectionString(Db, SqliteOpenMode.ReadWrite)))
        {
            await c.OpenAsync(); await ApprovalDatabase.Execute(c, null, "UPDATE executions SET version=$v,terminal=$terminal,snapshot=$json", default,
                ("$v", snapshot.Version), ("$terminal", snapshot.IsTerminal), ("$json", JsonSerializer.Serialize(snapshot)));
        }
        var before = await Bytes(); await ApprovalSchema.MigrateV3ToV4Async(Db, SyntheticApproval.Maintenance, f);
        Assert.Equal(before, await Bytes()); Assert.Equal(0L, await Sql("SELECT count(*) FROM execution_session_receipts"));
    }
    [Theory] [InlineData(ApprovalStatus.Pending)] [InlineData(ApprovalStatus.Approved)] [InlineData(ApprovalStatus.Rejected)]
    [InlineData(ApprovalStatus.Expired)] [InlineData(ApprovalStatus.Revoked)] [InlineData(ApprovalStatus.Consumed)]
    public async Task EveryApprovalLifecycleSurvivesMigrationWithoutNewAuthorization(ApprovalStatus status)
    {
        var f = await V3(approval: true); var record = (await f.Store().GetAsync("cancel-approval"))!;
        if (status is ApprovalStatus.Approved or ApprovalStatus.Consumed or ApprovalStatus.Rejected)
            record = await f.Store().DecideAsync(new(record.ApprovalId, "decision", 0, record.BindingDigest,
                status == ApprovalStatus.Rejected ? ApprovalChoice.Reject : ApprovalChoice.Approve), f.Approval.Operator);
        if (status == ApprovalStatus.Consumed)
        {
            var op = record.Operation;
            var initial = new DurableExecution(op.TaskId, op.RequestFingerprint, op.Scope.ProviderScopeDigest, op.Scope.ResourceId, op.WorkflowRunId,
                new(op.ExecutionId, "OpenCode", ExternalRequestId: op.MessageId), DurableExecutionState.Created, SubmissionPhase.None, f.Clock.Now, f.Clock.Now);
            await f.Store().ConsumeAsync(record.ApprovalId, record.Version, 0, record.BindingDigest, initial, f.Runtime.Caller);
        }
        if (status == ApprovalStatus.Expired) { f.Clock.Now = record.ExpiresAt; await f.Store().ExpireAsync(record.ApprovalId, record.Version); }
        if (status == ApprovalStatus.Revoked) await f.Store().RevokeAsync(record.ApprovalId, record.Version, f.Approval.Operator);
        var before = await Bytes(); await ApprovalSchema.MigrateV3ToV4Async(Db, SyntheticApproval.Maintenance, f);
        Assert.Equal(before, await Bytes());
        Assert.Equal(status, (await new SqliteApprovalStore(Db, f.Authority(), f.Clock, ApprovalStoreProfile.ExplicitV4).GetAsync(record.ApprovalId))!.Status);
    }
    [Theory] [InlineData("auth")] [InlineData("attestation")] [InlineData("owner")]
    [InlineData("future")] [InlineData("partial")] [InlineData("extra")]
    [InlineData("audit")] [InlineData("cancellation")]
    public async Task UnsafeSourceNeverMigratesOrRepairs(string mutation)
    {
        var f = await V3(cancel: true); await using var owner = mutation == "owner" ? await f.Executions.TryAcquireAsync(f.Command.TaskId) : null;
        if (mutation == "future") await Sql("PRAGMA user_version=5");
        if (mutation == "partial") await Sql("ALTER TABLE executions ADD COLUMN session_receipt_required INTEGER NOT NULL DEFAULT 0");
        if (mutation == "extra") await Sql("CREATE TABLE unrecognized(x TEXT)");
        if (mutation == "audit") await Sql("UPDATE control_audit SET actor='forged' WHERE cancel_request_id IS NOT NULL");
        if (mutation == "cancellation") await Sql("DELETE FROM control_audit WHERE cancel_request_id IS NOT NULL");
        var before = await Bytes();
        await Assert.ThrowsAnyAsync<Exception>(() => ApprovalSchema.MigrateV3ToV4Async(Db,
            mutation == "attestation" ? new(false, true, true) : SyntheticApproval.Maintenance, mutation == "auth" ? null : f));
        Assert.Equal(before, await Bytes()); Assert.Equal(mutation == "future" ? 5L : 3L, await Sql("PRAGMA user_version"));
    }
    [Theory]
    [InlineData("V4ProfileAdded", false)] [InlineData("V4ReceiptCreated", false)]
    [InlineData("V4DeliveryCreated", false)] [InlineData("V4VersionWritten", false)] [InlineData("V4Committed", true)]
    public async Task RealMigrationProcessCrashLeavesOnlyExactOldOrNewSchema(string point, bool committed)
    {
        await V3(cancel: true); var before = await Bytes(); using var child = CancellationSchemaTests.Child("receipt-migrate", Db, point); var start = child.StartTime;
        try
        {
            Assert.Equal(point, await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.Equal(start, child.StartTime); await Kill(child);
            Assert.Equal(committed ? 4L : 3L, await Sql("PRAGMA user_version")); Assert.Equal(before, await Bytes());
            Assert.Equal("ok", await Sql("PRAGMA integrity_check"));
            var f = new SyntheticCancellation(Db, v4: committed);
            Assert.NotNull(await f.Store().GetCancellationAsync("cancel-one"));
            using var reader = CancellationSchemaTests.Child("receipt-verify", Db);
            var json = await reader.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await reader.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, reader.ExitCode);
            using var result = JsonDocument.Parse(json);
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(before))), result.RootElement.GetProperty("OriginalDigest").GetString());
            Assert.Equal(committed ? 4 : 3, result.RootElement.GetProperty("Version").GetInt32());
            Assert.Equal("ok", result.RootElement.GetProperty("Integrity").GetString());
            output.WriteLine($"v4 migration PID={child.Id}, startUTC={start.ToUniversalTime():O}, restartPID={reader.Id}, checkpoint={point}, schema={(committed ? 4 : 3)}, V3BytesUnchanged=true, integrity=ok.");
        }
        finally { await Kill(child); }
    }
    [Theory]
    [InlineData("ReceiptIntentWritten", false, false)] [InlineData("ReceiptIntentCommitted", false, false)]
    [InlineData("ReceiptSnapshotWritten", false, false)] [InlineData("ReceiptResponseWritten", false, false)]
    [InlineData("ReceiptResponseCommitted", true, false)] [InlineData("ReceiptResponseCommitted", true, true)]
    public async Task RealOwnerTerminationNeverReconstructsReceiptOrSubmission(string point, bool committed, bool approved)
    {
        var f = new SyntheticCancellation(Db, approved, v4: true);
        await ApprovalSchema.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance);
        await ApprovalSchema.MigrateV2ToV3Async(Db, SyntheticApproval.Maintenance, f);
        await ApprovalSchema.MigrateV3ToV4Async(Db, SyntheticApproval.Maintenance, f); await f.Store().ActivatePolicyAsync();
        using var child = CancellationSchemaTests.Child("receipt-run", Db, point, approved ? "approved" : "allow"); var start = child.StartTime;
        try
        {
            Assert.Equal(point, await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20))); await Kill(child);
            var e = (await f.Executions.GetAsync(f.Command.TaskId))!;
            Assert.Equal(committed ? "ses_runtime" : null, e.Handle.ExternalSessionId);
            var receipt = await f.Store().GetSessionReceiptAsync(f.Command.TaskId);
            Assert.Equal(committed ? SessionReceiptStatus.Verified : point == "ReceiptIntentWritten" ? null : SessionReceiptStatus.Pending, receipt?.Status);
            f.Policy = f.Policy with { Epoch = new(2), Version = new(2), Rules = [..f.Policy.Rules.Where(r => r.Operation == PolicyOperation.InspectExistingExecution)] };
            await f.Store().ActivatePolicyAsync();
            using var restart = CancellationSchemaTests.Child("receipt-recover", Db, "unused", approved ? "approved" : "allow");
            var json = await restart.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(20));
            await restart.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, restart.ExitCode);
            using var data = JsonDocument.Parse(json); var r = data.RootElement;
            Assert.Equal("runtime-task", r.GetProperty("TaskId").GetString()); Assert.Equal("runtime-execution", r.GetProperty("ExecutionId").GetString());
            Assert.Equal("runtime-run", r.GetProperty("WorkflowRunId").GetString()); Assert.Equal("msg_runtime", r.GetProperty("ExternalRequestId").GetString());
            Assert.Equal("Unknown", r.GetProperty("State").GetString());
            Assert.All(r.GetProperty("Calls").EnumerateArray(), call => Assert.StartsWith("GET ", call.GetString()));
            if (committed) Assert.NotEmpty(r.GetProperty("Calls").EnumerateArray());
            var requests = File.Exists(Db + ".http") ? File.ReadAllLines(Db + ".http") : [];
            Assert.Equal(point is "ReceiptIntentWritten" or "ReceiptIntentCommitted" ? 0 : 1, requests.Count(x => x == "POST /session"));
            Assert.DoesNotContain(requests, x => x.Contains("prompt_async") || x.Contains("abort"));
            output.WriteLine($"Receipt approved={approved}, ownerPID={child.Id}, startUTC={start.ToUniversalTime():O}, restartPID={restart.Id}, checkpoint={point}, receipt={receipt?.Status.ToString() ?? "none"}, original={e.TaskId}/{e.Handle.ExecutionId}/{e.WorkflowRunId}/{e.Handle.ExternalRequestId}/{e.Handle.ExternalSessionId ?? "null"}, recovery=Unknown, Create={requests.Count(x => x == "POST /session")}/Prompt=0/Abort=0, current Inspect-only epoch2, integrity={await Sql("PRAGMA integrity_check")}.");
        }
        finally { await Kill(child); }
    }
    static async Task Kill(Process p) { if (!p.HasExited) { p.Kill(entireProcessTree: false); await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
