using System.Text.Json;
using MAF.Core.Policy;
using Microsoft.Data.Sqlite;
using static MAF.Persistence.Sqlite.DeliveryStorageFormat;

namespace MAF.Persistence.Sqlite;

public sealed partial class SqliteApprovalStore
{
    internal sealed record DeliveryStorageRecord(Intent Intent, string IntentDigest, long Version, int Stage, int Outcome, Observation? Observation);
    internal static void ValidateDeliveryStorageTransition(DeliveryStorageRecord before, DeliveryStorageRecord after)
    {
        Require(before.IntentDigest == after.IntentDigest && Digest(before.Intent) == Digest(after.Intent)
            && after.Version == before.Version + 1 && after.Observation is not null
            && after.Observation.AuditSequence > (before.Observation?.AuditSequence ?? before.Intent.IntentAuditSequence)
            && (after.Stage == before.Stage || before.Stage == 0 && after.Stage is 1 or 2 or 3)
            && (before.Stage != 2 || after.Observation.AcknowledgementAtTicks == before.Observation?.AcknowledgementAtTicks)
            && (before.Outcome == 0 || after.Outcome == before.Outcome
                && after.Observation.TerminalEvidence == before.Observation?.TerminalEvidence
                && after.Observation.TerminalEvidenceDigest == before.Observation?.TerminalEvidenceDigest), "DeliveryStorageTransitionInvalid");
    }
    // Data-only diagnostic seam; never reconstructs a delivery continuation.
    internal Task<DeliveryStorageRecord?> ReadDeliveryStorageAsync(string deliveryId, CancellationToken token = default)
    {
        Require(RequiresSessionReceipts, "ApprovalSchemaV4Required"); Id(deliveryId);
        return Transaction((c, tx) => ReadDeliveryStorage(c, tx, deliveryId, token), token);
    }
    async Task<DeliveryStorageRecord?> ReadDeliveryStorage(SqliteConnection c, SqliteTransaction tx, string deliveryId, CancellationToken token)
    {
            DeliveryStorageRecord result; long lastAudit;
            using (var cmd = ApprovalDatabase.Command(c, tx, "SELECT *,length(CAST(intent_json AS BLOB)),length(CAST(observation_json AS BLOB)) FROM cancellation_deliveries WHERE delivery_id=$id", ("$id", deliveryId)))
            using (var r = await cmd.ExecuteReaderAsync(token))
            {
                if (!await r.ReadAsync(token)) return null;
                Require(r.GetInt64(23) is > 0 and <= 16384 && (r.IsDBNull(24) || r.GetInt64(24) is > 0 and <= 8192), "DeliveryEvidenceSize");
                var i = SessionReceiptFormat.Read<Intent>(r.GetString(14), 16384);
                var observation = r.IsDBNull(21) ? null : SessionReceiptFormat.Read<Observation>(r.GetString(21), 8192);
                result = new(i, r.GetString(13), r.GetInt64(15), r.GetInt32(16), r.GetInt32(18), observation); lastAudit = r.GetInt64(22);
                foreach (var id in new[] { i.DeliveryId, i.CancelRequestId, i.ReceiptId, i.ProviderId, i.ProviderSessionId, i.CallerAuthenticationContextId }) Id(id);
                foreach (var hash in new[] { i.CancellationBindingDigest, i.ReceiptDigest, i.PolicyDigest }) HashText(hash);
                Require(i.DeliveryId == deliveryId && i.CancelRequestId == r.GetString(1) && i.ReceiptId == r.GetString(2)
                    && S(i.Operation, "TaskId") == r.GetString(3) && S(i.Operation, "ExecutionId") == r.GetString(4)
                    && S(i.Operation, "OperationId") == r.GetString(5) && i.CapturedControlVersion == r.GetInt64(6)
                    && i.ReceiptDigest == r.GetString(7) && i.IntentAuditSequence == r.GetInt64(8) && i.IntentAtTicks == r.GetInt64(9)
                    && i.BudgetTicks == r.GetInt64(10) && i.BudgetTicks is > 0 and <= 50000000
                    && r.GetInt64(11) == 1 && r.GetInt64(12) == 1 && Digest(i) == result.IntentDigest
                    && S(i.Operation, "Operation") == "AbortExistingExecution" && N(i.Operation.GetProperty("Scope"), "BudgetTicks") == i.BudgetTicks,
                    "DeliveryEvidenceMismatch");
                if (observation is null) Require(result.Version == 0 && result.Stage == 0 && result.Outcome == 0 && lastAudit == i.IntentAuditSequence, "DeliveryObservationMissing");
                else
                {
                    Id(observation.ObserverAuthenticationContextId); HashText(observation.ObserverPolicyDigest);
                    Require(observation.DeliveryId == i.DeliveryId && observation.IntentDigest == result.IntentDigest
                        && Digest(observation) == r.GetString(20) && result.Version > 0
                        && observation.Stage == Stages[result.Stage] && observation.RemoteOutcome == Outcomes[result.Outcome]
                        && observation.ObservedAtTicks == r.GetInt64(19) && observation.ObservedAtTicks >= i.IntentAtTicks
                        && observation.AuditSequence == lastAudit && lastAudit > i.IntentAuditSequence
                        && observation.AcknowledgementAtTicks == (r.IsDBNull(17) ? null : r.GetInt64(17))
                        && new[] { "None", "ResponseLost", "ProviderUnavailable", "InvalidAcknowledgement", "AuthorizationChanged", "NoSubmissionEvidence", "Running", "PermissionPending", "EvidenceInsufficient", "TerminalEvidenceChanged" }.Contains(observation.Diagnostic), "DeliveryObservationMismatch");
                    _ = Pair(observation.ObserverOperation, observation.ObserverCapability);
                    Require(result.Stage != 2 || observation.AcknowledgementAtTicks >= i.IntentAtTicks, "DeliveryAcknowledgementMismatch");
                }
            }
            var payload = result.Intent; var op = payload.Operation; var task = S(op, "TaskId");
            var receipt = await ReadSessionReceipt(c, tx, task, token) ?? throw new InvalidOperationException("DeliveryReceiptRequired");
            var cancellation = await ReadCancellation(c, tx, payload.CancelRequestId, token) ?? throw new InvalidOperationException("DeliveryCancellationRequired");
            var control = await Control(c, tx, task, token);
            Require(receipt.Status == SessionReceiptStatus.Verified && receipt.Response is not null && receipt.Intent.ReceiptId == payload.ReceiptId
                && SessionReceiptFormat.Link(receipt) == payload.ReceiptDigest && receipt.Intent.ProviderId == payload.ProviderId
                && receipt.Response.ProviderSessionId == payload.ProviderSessionId && payload.ProviderSessionId == S(op, "ProviderSessionId")
                && cancellation.BindingDigest == payload.CancellationBindingDigest && cancellation.CommittedControlVersion == payload.CapturedControlVersion
                && cancellation.Operation.TaskId == task && cancellation.Operation.ExecutionId == S(op, "ExecutionId")
                && control is not null && control.CancelRequestId == payload.CancelRequestId && control.Version >= payload.CapturedControlVersion,
                "DeliveryTargetMismatch");
            var original = receipt.Intent.Operation;
            void Target(JsonElement operation)
            {
                var scope = operation.GetProperty("Scope");
                Require(S(operation, "TaskId") == original.TaskId && S(operation, "ExecutionId") == original.ExecutionId
                    && S(operation, "WorkflowRunId") == original.WorkflowRunId && S(operation, "MessageId") == original.MessageId
                    && S(operation, "RequestFingerprint") == original.RequestFingerprint && S(operation, "ConnectorId") == original.ConnectorId
                    && S(operation, "ProviderSessionId") == receipt.Response!.ProviderSessionId && S(scope, "ResourceId") == original.Scope.ResourceId
                    && S(scope, "ProviderScopeDigest") == original.Scope.ProviderScopeDigest && S(scope, "AgentPolicyDigest") == original.Scope.AgentPolicyDigest
                    && N(scope, "ConfigurationRevision") == original.Scope.ConfigurationRevision && N(scope, "ScopeRevision") == original.Scope.ScopeRevision,
                    "DeliveryScopeMismatch");
            }
            Target(op);
            Require(Convert.ToInt64(await ApprovalDatabase.Scalar(c, tx, "SELECT count(*) FROM authorization_operations WHERE operation_id=$id", token, ("$id", S(op, "OperationId")))) == 0, "DeliveryOperationConflict");
            var cancelSequence = Convert.ToInt64(await ApprovalDatabase.Scalar(c, tx, "SELECT sequence FROM control_audit WHERE code=6 AND cancel_request_id=$id", token, ("$id", payload.CancelRequestId)));
            Require(payload.IntentAuditSequence > cancelSequence, "DeliveryOrderMismatch");
            await DeliveryAudit(c, tx, payload.IntentAuditSequence, 0, payload.IntentAtTicks, op, receipt.Intent.ApprovalId, token);
            if (result.Observation is { } o)
            {
                Target(o.ObserverOperation); Require(o.CorrelatedMessageId == original.MessageId, "DeliveryMessageMismatch");
                if (result.Outcome != 0)
                {
                    var t = o.TerminalEvidence;
                    Require(t is not null && S(o.ObserverOperation, "Operation") == "InspectExistingExecution"
                        && t.Outcome == o.RemoteOutcome && t.ProviderId == payload.ProviderId && t.ProviderScope == original.Scope.ProviderScopeDigest
                        && t.SessionId == payload.ProviderSessionId && t.OriginalMessageId == original.MessageId && t.ParentMessageId == original.MessageId
                        && !string.IsNullOrEmpty(t.ObservedMessageId) && t.ObservedAtTicks <= o.ObservedAtTicks && t.ObservedAtTicks >= payload.IntentAtTicks
                        && TerminalDigest(t) == o.TerminalEvidenceDigest, "DeliveryTerminalMismatch");
                    Id(t!.ObservedMessageId!);
                    foreach (var code in new[] { t.FinishReason, t.ErrorCode }) Require(code is null || code.Length is > 0 and <= 80 && code.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_'), "DeliveryTerminalCodeInvalid");
                }
                else Require(o.TerminalEvidence is null && o.TerminalEvidenceDigest is null, "DeliveryTerminalMismatch");
                await DeliveryAudit(c, tx, lastAudit, result.Version, o.ObservedAtTicks, o.ObserverOperation, receipt.Intent.ApprovalId, token);
            }
            return result;
    }
    static async Task DeliveryAudit(SqliteConnection c, SqliteTransaction tx, long sequence, long version, long ticks, JsonElement op, string? approval, CancellationToken token)
    {
        using var cmd = ApprovalDatabase.Command(c, tx, "SELECT code,actor,task_id,approval_id,version,recorded_at,policy_epoch,policy_version,cancel_request_id FROM control_audit WHERE sequence=$s", ("$s", sequence));
        using var r = await cmd.ExecuteReaderAsync(token);
        Require(await r.ReadAsync(token) && r.GetInt64(0) == 0 && r.GetString(1) == S(op, "CallerSubject") && r.GetString(2) == S(op, "TaskId")
            && (r.IsDBNull(3) ? null : r.GetString(3)) == approval && r.GetInt64(4) == version
            && DateTimeOffset.Parse(r.GetString(5)).UtcTicks == ticks && r.GetInt64(6) == N(op.GetProperty("Epoch"), "Value")
            && r.GetInt64(7) == N(op.GetProperty("Version"), "Value") && r.IsDBNull(8), "DeliveryAuditMismatch");
        await r.DisposeAsync();
        Require(Convert.ToInt64(await ApprovalDatabase.Scalar(c, tx, "SELECT count(*) FROM execution_session_receipts WHERE intent_audit_sequence=$s OR response_audit_sequence=$s", token, ("$s", sequence))) == 0
            && Convert.ToInt64(await ApprovalDatabase.Scalar(c, tx, "SELECT count(*) FROM cancellation_deliveries WHERE intent_audit_sequence=$s OR last_audit_sequence=$s", token, ("$s", sequence))) == 1,
            "DeliveryAuditReuse");
    }
}
