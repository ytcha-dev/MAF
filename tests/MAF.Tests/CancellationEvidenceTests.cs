using System.Text.Json;
using MAF.ApprovalAcceptance;
using MAF.Core;
using MAF.Core.Policy;
using MAF.Persistence.Sqlite;
using MAF.Workflows.External;
using Microsoft.Data.Sqlite;

namespace MAF.Tests;

public sealed class CancellationEvidenceTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "maf-cancel-evidence-" + Guid.NewGuid().ToString("N"));
    string Db => Path.Combine(root, "synthetic.db");
    async Task<object?> Sql(string sql)
    {
        await using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Db, Pooling = false, ForeignKeys = true }.ToString());
        await c.OpenAsync(); using var cmd = c.CreateCommand(); cmd.CommandText = sql; return await cmd.ExecuteScalarAsync();
    }
    [Theory]
    [InlineData("update")] [InlineData("delete")] [InlineData("audit-duplicate")]
    public async Task ImmutableEvidenceAndOneRequiredAuditEnforcedBySql(string mutation)
    {
        var f = new SyntheticCancellation(Db); await f.Setup(); await f.Application().RequestAsync(f.Command, f.Canceller);
        var sql = mutation switch
        {
            "update" => "UPDATE cancellation_requests SET caller_subject='wrong'",
            "delete" => "DELETE FROM cancellation_requests",
            _ => "INSERT INTO control_audit(code,actor,task_id,approval_id,version,recorded_at,policy_epoch,policy_version,cancel_request_id) SELECT code,actor,task_id,approval_id,version,recorded_at,policy_epoch,policy_version,cancel_request_id FROM control_audit WHERE code=6"
        };
        await Assert.ThrowsAsync<SqliteException>(() => Sql(sql)); Assert.NotNull(await f.Store().GetCancellationAsync("cancel-one"));
    }
    [Theory]
    [InlineData("length")] [InlineData("digest")] [InlineData("version")] [InlineData("binding-version")]
    [InlineData("foreign-key")] [InlineData("same-task")] [InlineData("same-id")] [InlineData("same-operation")]
    public async Task SchemaRejectsInvalidEvidenceShapeAndDuplicateIdentities(string mutation)
    {
        var f = new SyntheticCancellation(Db); await f.Setup(); await f.Application().RequestAsync(f.Command, f.Canceller);
        await Sql("INSERT INTO task_controls VALUES('task-two','execution-two',0,NULL,NULL,NULL); INSERT INTO authorization_operations VALUES('cancel-two','task-two','execution-two',3,1)");
        var id = mutation == "length" ? "'" + new string('a', 161) + "'" : mutation == "same-id" ? "cancel_request_id" : "'candidate'";
        var op = mutation == "foreign-key" ? "'missing-operation'" : mutation == "same-operation" ? "operation_id" : "'cancel-two'";
        var task = mutation == "same-task" ? "task_id" : "'task-two'";
        var digest = mutation == "digest" ? "'" + new string('G', 64) + "'" : "binding_digest";
        var version = mutation == "version" ? "expected_control_version+2" : "committed_control_version";
        var encoding = mutation == "binding-version" ? "2" : "binding_version";
        await Assert.ThrowsAsync<SqliteException>(() => Sql("INSERT INTO cancellation_requests SELECT " + id + "," + op + "," + task
            + ",'execution-two',NULL,caller_subject,authentication_context_id,authority_id,policy_epoch,policy_version,policy_digest,evidence_format,"
            + encoding + "," + digest + ",expected_control_version," + version + ",evaluated_at_ticks,expires_at_ticks,requested_at_ticks,evidence_json FROM cancellation_requests WHERE cancel_request_id='cancel-one'"));
        Assert.Equal(1L, await Sql("SELECT count(*) FROM cancellation_requests"));
    }
    [Theory]
    [InlineData("index")] [InlineData("trigger")] [InlineData("column")] [InlineData("future")]
    public async Task BothExplicitV3ProfilesRejectAlteredSchemaBeforeReadingRows(string mutation)
    {
        var f = new SyntheticCancellation(Db); await f.Setup();
        await Sql(mutation switch
        {
            "index" => "DROP INDEX one_cancellation_requested_audit",
            "trigger" => "DROP TRIGGER cancellation_requests_no_update",
            "column" => "ALTER TABLE cancellation_requests ADD COLUMN unexpected TEXT",
            _ => "PRAGMA user_version=4"
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store().GetControlAsync(f.Command.TaskId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Executions.GetAsync(f.Command.TaskId));
        if (mutation == "future") Assert.Equal(4L, await Sql("PRAGMA user_version"));
    }
    [Theory]
    [InlineData("operation")] [InlineData("control")] [InlineData("target")] [InlineData("missing-evidence")]
    public async Task ReadsValidateJoinsAndMandatoryEvidence(string mutation)
    {
        var f = new SyntheticCancellation(Db); await f.Setup(); await f.Application().RequestAsync(f.Command, f.Canceller);
        if (mutation == "operation") await Sql("UPDATE authorization_operations SET execution_id='wrong' WHERE kind=3");
        if (mutation == "control") await Sql("UPDATE task_controls SET actor='wrong'");
        if (mutation == "target") await Sql("UPDATE authorization_operations SET kind=2 WHERE kind=1");
        if (mutation == "missing-evidence")
        {
            var trigger = (string)(await Sql("SELECT sql FROM sqlite_master WHERE name='cancellation_requests_no_delete'"))!;
            await Sql("DELETE FROM control_audit WHERE cancel_request_id='cancel-one'; DROP TRIGGER cancellation_requests_no_delete; DELETE FROM cancellation_requests; " + trigger);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store().GetControlAsync(f.Command.TaskId));
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store().GetCancellationAsync("cancel-one")); return;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store().GetCancellationAsync("cancel-one"));
    }
    [Theory]
    [InlineData("duplicate")] [InlineData("unknown")] [InlineData("absent")] [InlineData("oversize")]
    [InlineData("enum-number")] [InlineData("enum-case")] [InlineData("enum-unknown")] [InlineData("epoch-extra")]
    public async Task EvidenceParserRejectsAmbiguousOrUnboundedPayload(string variant)
    {
        var f = new SyntheticCancellation(Db); await f.Setup(); var e = (await f.Application().RequestAsync(f.Command, f.Canceller)).Evidence!;
        var json = CancellationEvidenceFormat.Write(e.Operation, e.Capability);
        json = variant switch
        {
            "duplicate" => json.Replace("\"AuthorityId\":", "\"AuthorityId\":\"duplicate\",\"AuthorityId\":"),
            "unknown" => json.Insert(1, "\"Unexpected\":1,"),
            "absent" => "{\"Capability\":" + JsonSerializer.Serialize(new { }) + "}",
            "oversize" => new string(' ', 16385),
            "enum-number" => json.Replace("\"RequestCancelExecution\"", "3"),
            "enum-case" => json.Replace("\"RequestCancelExecution\"", "\"requestCancelExecution\""),
            "enum-unknown" => json.Replace("\"RequestCancelExecution\"", "\"Unknown\""),
            _ => json.Replace("\"Epoch\":{", "\"Epoch\":{\"IsValid\":true,")
        };
        Assert.ThrowsAny<Exception>(() => CancellationEvidenceFormat.Read(json));
    }
    [Fact]
    public async Task JsonPropertyOrderIsNotBindingEqualityAndRenewedContextDoesNotRewriteEvidence()
    {
        var f = new SyntheticCancellation(Db); await f.Setup(); var e = (await f.Application().RequestAsync(f.Command, f.Canceller)).Evidence!;
        var json = CancellationEvidenceFormat.Write(e.Operation, e.Capability);
        using var doc = JsonDocument.Parse(json);
        var reordered = "{\"Capability\":" + doc.RootElement.GetProperty("Capability").GetRawText()
            + ",\"Operation\":" + doc.RootElement.GetProperty("Operation").GetRawText() + "}";
        var read = CancellationEvidenceFormat.Read(reordered); Assert.Equal(e.BindingDigest, OperationBinding.Digest(read.Operation, read.Capability));
        f.Canceller = new(f.Canceller.SubjectId, "renewed-context");
        var replay = await f.Application().RequestAsync(f.Command, f.Canceller);
        Assert.Equal(CancellationAdmissionStatus.AlreadyRecorded, replay.Status); Assert.Equal(e.AuthenticationContextId, replay.Evidence!.AuthenticationContextId);
    }
    [Fact]
    public async Task AuditCapacityPreventsPartialCancellationAndNoSensitiveInputIsStored()
    {
        var f = new SyntheticCancellation(Db); await f.Setup();
        var count = Convert.ToInt32(await Sql("SELECT count(*) FROM control_audit"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Application(f.Store(capacity: count)).RequestAsync(f.Command, f.Canceller));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM cancellation_requests")); Assert.Equal(0L, await Sql("SELECT version FROM task_controls"));
        await f.Application().RequestAsync(f.Command, f.Canceller);
        Assert.DoesNotContain(f.Runtime.Request.TaskText, (string)(await Sql("SELECT evidence_json FROM cancellation_requests"))!);
        Assert.Equal(0L, await Sql("SELECT count(*) FROM approval_requests"));
    }
    [Fact]
    public async Task MissingProtectedProfileCannotCreateExecutionOrConsumeContinuation()
    {
        var f = new SyntheticCancellation(Db); await f.Setup();
        await using var owner = (await f.Executions.TryAcquireAsync(f.Command.TaskId))!;
        var e = (await f.Executions.GetAsync(f.Command.TaskId))!;
        Assert.Equal("AtomicAdmissionRequired", (await Assert.ThrowsAsync<InvalidOperationException>(() => f.Executions.CreateAsync(e, owner))).Message);
        var old = new SqliteApprovalStore(Db, f.Authority(), f.Clock);
        var assessment = new ExternalExecutionApplication(f.Runtime.Inputs, new ImmutablePolicySource(f.Policy), f, f.Clock).Assess(f.Runtime.Intent, f.Runtime.Caller);
        await Assert.ThrowsAsync<InvalidOperationException>(() => old.AdmitAllowedAsync(assessment.Operation!, assessment.Decision, f.Runtime.Request, e, 0, f.Runtime.Caller, owner));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store().RequestCancellationAsync("irrelevant", "no", 0, f.Runtime.Caller));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
