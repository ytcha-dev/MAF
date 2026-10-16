using System.Text.Json;
using System.Text.Json.Nodes;
using MAF.ApprovalAcceptance;
using MAF.Core.Policy;
using MAF.Persistence.Sqlite;
using MAF.Workflows.External;
using Microsoft.Data.Sqlite;
using static MAF.Persistence.Sqlite.DeliveryStorageFormat;

namespace MAF.Tests;

// Explicit raw storage fixtures, never synthetic executable Abort permission.
public sealed class DeliveryStorageTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "maf-delivery-data-" + Guid.NewGuid().ToString("N"));
    string Db => Path.Combine(root, "synthetic.db");
    async Task<T> Connection<T>(Func<SqliteConnection, Task<T>> action)
    { await using var c = new SqliteConnection(ApprovalDatabase.ConnectionString(Db, SqliteOpenMode.ReadWrite)); await c.OpenAsync(); return await action(c); }
    async Task<object?> Sql(string sql) => await Connection(c => ApprovalDatabase.Scalar(c, null, sql, default));
    static JsonElement Element<T>(T value) => JsonDocument.Parse(SessionReceiptFormat.Write(value, 16384)).RootElement.Clone();
    async Task<(SyntheticCancellation F, Intent Intent)> Seed(string? malformed = null)
    {
        var f = new SyntheticCancellation(Db, v4: true);
        await ApprovalSchema.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance);
        await ApprovalSchema.MigrateV2ToV3Async(Db, SyntheticApproval.Maintenance, f);
        await ApprovalSchema.MigrateV3ToV4Async(Db, SyntheticApproval.Maintenance, f); await f.Store().ActivatePolicyAsync();
        using var fake = new RuntimeHttpFixture(f.Runtime.Root); using var http = ReceiptTransport.ForTests(fake);
        fake.OnRequest = async (request, _) => { if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/session") await f.Application().RequestAsync(f.Command, f.Canceller); };
        await f.Protected(http).RunAsync(f.Runtime.Intent, f.Runtime.Caller);
        var receipt = (await f.Store().GetSessionReceiptAsync(f.Command.TaskId))!;
        var cancellation = (await f.Store().GetCancellationAsync("cancel-one"))!;
        var op = JsonNode.Parse(SessionReceiptFormat.Write(receipt.Intent.Operation, 16384))!;
        op["Operation"] = "AbortExistingExecution"; op["Capability"] = "ExecutionAbort"; op["OperationId"] = "abort-operation";
        op["CallerSubject"] = "synthetic-delivery"; op["ProviderSessionId"] = "ses_runtime"; op["Scope"]!["BudgetTicks"] = 50000000L;
        if (malformed == "scope") op["Scope"]!["ResourceId"] = "UNRELATED";
        var cap = JsonNode.Parse(SessionReceiptFormat.Write(receipt.Intent.Capability, 16384))!;
        cap["Capability"] = "ExecutionAbort"; cap["SupportedOperations"] = new JsonArray("AbortExistingExecution");
        var sequence = await Audit("synthetic-delivery", 0, f.Clock.Now.UtcTicks);
        var intent = new Intent(JsonDocument.Parse(op.ToJsonString()).RootElement.Clone(), JsonDocument.Parse(cap.ToJsonString()).RootElement.Clone(),
            "delivery-one", "cancel-one", cancellation.BindingDigest, receipt.Intent.ReceiptId, SessionReceiptFormat.Link(receipt),
            "OpenCode", "ses_runtime", "synthetic-context", receipt.Intent.PolicyDigest, 1, sequence, f.Clock.Now.UtcTicks, 50000000);
        var json = SessionReceiptFormat.Write(intent, 16384);
        if (malformed == "duplicate") json = json.Replace("\"DeliveryId\":", "\"DeliveryId\":\"duplicate\",\"DeliveryId\":", StringComparison.Ordinal);
        await Connection(c => ApprovalDatabase.Execute(c, null, """
            INSERT INTO cancellation_deliveries(delivery_id,cancel_request_id,receipt_id,task_id,execution_id,abort_operation_id,
            captured_control_version,receipt_digest,intent_audit_sequence,intent_at_ticks,budget_ticks,format_version,binding_version,intent_digest,intent_json,
            version,delivery_stage,remote_outcome,last_audit_sequence) VALUES('delivery-one','cancel-one',$receipt,'runtime-task','runtime-execution','abort-operation',1,$link,$audit,$at,50000000,1,1,$digest,$json,0,0,0,$audit)
            """, default, ("$receipt", receipt.Intent.ReceiptId), ("$link", intent.ReceiptDigest), ("$audit", sequence), ("$at", intent.IntentAtTicks), ("$digest", Digest(intent)), ("$json", json)));
        Assert.DoesNotContain(fake.Requests, r => r.Contains("prompt_async") || r.Contains("abort"));
        return (f, intent);
    }
    async Task<long> Audit(string actor, long version, long ticks)
    {
        return await Connection(async c =>
        {
            await ApprovalDatabase.Execute(c, null, "INSERT INTO control_audit(code,actor,task_id,version,recorded_at,policy_epoch,policy_version) VALUES(0,$a,'runtime-task',$v,$at,1,1)", default,
                ("$a", actor), ("$v", version), ("$at", new DateTimeOffset(ticks, TimeSpan.Zero).ToString("O")));
            return Convert.ToInt64(await ApprovalDatabase.Scalar(c, null, "SELECT last_insert_rowid()", default));
        });
    }
    [Fact]
    public async Task ReservedEvidenceIsReadableButDoesNotIssueDeliveryContinuation()
    {
        var (f, _) = await Seed(); var row = (await f.Store().ReadDeliveryStorageAsync("delivery-one"))!;
        Assert.Equal(0, row.Version); Assert.Equal(0, row.Stage); Assert.Equal(0, row.Outcome);
        Assert.Equal(8, (int)PolicyOperation.AbortExistingExecution); Assert.Equal(4, (int)PolicyCapability.ExecutionAbort);
        Assert.Equal(0L, await Sql("SELECT count(*) FROM authorization_operations WHERE kind=8"));
    }
    [Theory] [InlineData("scope")] [InlineData("duplicate")] [InlineData("audit")]
    public async Task DataValidationRejectsForgedBindingAndAudit(string mutation)
    {
        var (f, _) = await Seed(mutation == "audit" ? null : mutation);
        if (mutation == "audit") await Sql("UPDATE control_audit SET actor='forged' WHERE sequence=(SELECT intent_audit_sequence FROM cancellation_deliveries)");
        await Assert.ThrowsAnyAsync<Exception>(() => f.Store().ReadDeliveryStorageAsync("delivery-one"));
    }
    [Theory] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task DataOnlyCasAllowsOneStageChangeAndAckIsNotTerminal(int stage)
    {
        var (f, intent) = await Seed(); var sequence = await Audit("synthetic-delivery", 1, f.Clock.Now.UtcTicks);
        var before = (await f.Store().ReadDeliveryStorageAsync("delivery-one"))!;
        var observation = new Observation(intent.DeliveryId, Digest(intent), intent.Operation, intent.Capability,
            "synthetic-context", intent.PolicyDigest, Stages[stage], "None", stage == 1 ? "ResponseLost" : stage == 3 ? "AuthorizationChanged" : "None",
            f.Clock.Now.UtcTicks, stage == 2 ? f.Clock.Now.UtcTicks : null, "msg_runtime", null, null, sequence);
        var changed = await Connection(c => ApprovalDatabase.Execute(c, null, """
            UPDATE cancellation_deliveries SET version=1,delivery_stage=$stage,acknowledgement_at_ticks=$ack,last_observed_at_ticks=$at,
            observation_digest=$digest,observation_json=$json,last_audit_sequence=$audit WHERE delivery_id='delivery-one' AND version=0
            """, default, ("$stage", stage), ("$ack", observation.AcknowledgementAtTicks), ("$at", observation.ObservedAtTicks),
            ("$digest", Digest(observation)), ("$json", SessionReceiptFormat.Write(observation, 8192)), ("$audit", sequence)));
        Assert.Equal(1, changed);
        var row = (await f.Store().ReadDeliveryStorageAsync("delivery-one"))!;
        Assert.Equal(stage, row.Stage); Assert.Equal(0, row.Outcome);
        SqliteApprovalStore.ValidateDeliveryStorageTransition(before, row);
        Assert.Throws<InvalidOperationException>(() => SqliteApprovalStore.ValidateDeliveryStorageTransition(row,
            row with { Version = 2, Stage = 0, Observation = observation with { AuditSequence = sequence + 1 } }));
        var confirmed = row with { Outcome = 4, Observation = observation with { RemoteOutcome = "Cancelled" } };
        Assert.Throws<InvalidOperationException>(() => SqliteApprovalStore.ValidateDeliveryStorageTransition(confirmed,
            confirmed with { Version = 2, Outcome = 1, Observation = observation with { RemoteOutcome = "Completed", AuditSequence = sequence + 1 } }));
        Assert.Equal(0, await Connection(c => ApprovalDatabase.Execute(c, null, "UPDATE cancellation_deliveries SET version=2 WHERE delivery_id='delivery-one' AND version=0", default)));
        await Assert.ThrowsAsync<SqliteException>(() => Sql("UPDATE cancellation_deliveries SET version=2,delivery_stage=0,last_audit_sequence=last_audit_sequence+1"));
        await Assert.ThrowsAsync<SqliteException>(() => Sql("DELETE FROM cancellation_deliveries"));
    }
    [Fact]
    public async Task DuplicateReservationAndTransactionFailureCannotCreateSecondAttempt()
    {
        var (f, _) = await Seed();
        await Assert.ThrowsAsync<SqliteException>(() => Sql("INSERT INTO cancellation_deliveries SELECT * FROM cancellation_deliveries"));
        await Connection(async c =>
        {
            using var tx = c.BeginTransaction();
            await ApprovalDatabase.Execute(c, tx, "INSERT INTO control_audit(code,actor,task_id,version,recorded_at,policy_epoch,policy_version) VALUES(0,'test','runtime-task',1,'2026-10-16T00:00:00.0000000+00:00',1,1)", default);
            tx.Rollback(); return true;
        });
        Assert.NotNull(await f.Store().ReadDeliveryStorageAsync("delivery-one")); Assert.Equal(1L, await Sql("SELECT count(*) FROM cancellation_deliveries"));
    }
    [Fact]
    public async Task StoredOperationLayoutMatchesUnchangedCoreInspectBinding()
    {
        var f = new SyntheticCancellation(Db);
        var op = new PolicyRequest("runtime-authority", "runtime-agent", "inspect-op", "runtime-task", "runtime-run", "runtime-execution", "msg_runtime",
            new('A',64), "runtime-connector", PolicyCapability.ExecutionInspection, PolicyOperation.InspectExistingExecution, f.Runtime.Scope, new(1), new(1), ProviderSessionId: "ses_runtime");
        Assert.Equal(OperationBinding.Encode(op, f.Runtime.Inspect), Pair(Element(op), Element(f.Runtime.Inspect)));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
