using System.Text.Json;
using MAF.Core;
using MAF.Core.Policy;
using Microsoft.Data.Sqlite;
using static MAF.Persistence.Sqlite.DeliveryStorageFormat;

namespace MAF.Persistence.Sqlite;

public sealed partial class SqliteApprovalStore : IAbortDeliveryCoordinator
{
    sealed class DeliveryOwner(string database, string cancel, FileStream handle) : IDeliveryOwnership
    {
        internal readonly string Database = database;
        public string CancelRequestId { get; } = cancel;
        internal readonly SemaphoreSlim Gate = new(1);
        internal bool Disposed;
        public async ValueTask DisposeAsync()
        { await Gate.WaitAsync(); try { Disposed = true; await handle.DisposeAsync(); } finally { Gate.Release(); } }
    }
    public ValueTask<IDeliveryOwnership?> TryAcquireDeliveryAsync(string cancelRequestId, CancellationToken token = default)
    {
        Require(RequiresSessionReceipts, "ApprovalSchemaV4Required"); Id(cancelRequestId); token.ThrowIfCancellationRequested();
        using var schema = ApprovalDatabase.SharedGate(path);
        var directory = path + ".delivery-locks"; Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, DurableExecution.Hash(cancelRequestId) + ".lock");
        foreach (var p in new[] { directory, file })
            Require(!(File.Exists(p) || Directory.Exists(p)) || (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0, "LinkedDeliveryLock");
        try { return ValueTask.FromResult<IDeliveryOwnership?>(new DeliveryOwner(path, cancelRequestId,
            new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))); }
        catch (IOException) { return ValueTask.FromResult<IDeliveryOwnership?>(null); }
    }
    async Task<T> WithDeliveryOwner<T>(IDeliveryOwnership owner, string cancel, Func<Task<T>> action, CancellationToken token)
    {
        Require(owner is DeliveryOwner d && d.Database == path && d.CancelRequestId == cancel, "DeliveryOwnershipMismatch");
        var actual = (DeliveryOwner)owner; await actual.Gate.WaitAsync(token);
        try { Require(!actual.Disposed, "DeliveryOwnershipReleased"); return await action(); }
        finally { actual.Gate.Release(); }
    }
    static JsonElement DeliveryElement<T>(T value) => JsonDocument.Parse(SessionReceiptFormat.Write(value, 16384)).RootElement.Clone();

    public Task<AbortDeliveryContinuation> ReserveAbortAsync(AbortDeliveryCommand command, AbortDeliveryInputs inputs,
        ITrustedCallerContext caller, IDeliveryOwnership owner, CancellationToken token = default)
        => WithDeliveryOwner(owner, command.CancelRequestId, async () =>
        {
            Require(RequiresSessionReceipts, "ApprovalSchemaV4Required");
            foreach (var id in new[] { command.DeliveryId, command.CancelRequestId, command.TaskId, command.ExecutionId, inputs.OperationId }) Id(id);
            if (caller is null) throw new InvalidOperationException("AbortNotAuthorized");
            var actor = Actor(caller);
            await using var lease = await authority.AcquireAsync(token); var policy = Candidate();
            var reserved = await GuardedTransaction(policy, async (c, tx) =>
            {
                // No target information leaves the transaction until exact independent authorization.
                DurableExecution? execution;
                try { (execution, _, _) = await CancelTarget(c, tx, command.TaskId, token); }
                catch (Exception error) when (error is InvalidOperationException or JsonException)
                { throw new InvalidOperationException("AbortNotAuthorized"); }
                Require(execution is not null && execution.Handle.ExecutionId == command.ExecutionId, "AbortNotAuthorized");
                var e = execution!;
                var op = new PolicyRequest(inputs.AuthorityId, actor, inputs.OperationId, e.TaskId, e.WorkflowRunId,
                    e.Handle.ExecutionId, e.Handle.ExternalRequestId!, e.RequestFingerprint, inputs.Capability.ConnectorId,
                    inputs.Capability.Capability, PolicyOperation.AbortExistingExecution, inputs.Scope, inputs.Epoch, inputs.Version,
                    ProviderSessionId: e.Handle.ExternalSessionId);
                AuthorizeAbort(policy, op, inputs.Capability, caller, e);
                var (receipt, cancel, control) = await AbortTarget(c, tx, command, op, token);
                Require(Convert.ToInt64(await ApprovalDatabase.Scalar(c, tx,
                    "SELECT count(*) FROM cancellation_deliveries WHERE cancel_request_id=$id OR task_id=$t OR delivery_id=$d OR abort_operation_id=$o", token,
                    ("$id", command.CancelRequestId), ("$t", command.TaskId), ("$d", command.DeliveryId), ("$o", op.OperationId))) == 0, "DeliveryAlreadyReserved");
                Require(Convert.ToInt64(await ApprovalDatabase.Scalar(c, tx, "SELECT count(*) FROM authorization_operations WHERE operation_id=$id", token, ("$id", op.OperationId))) == 0, "DeliveryOperationConflict");
                var at = clock.GetUtcNow().UtcTicks;
                var audit = await AbortAudit(c, tx, op, receipt.Intent.ApprovalId, 0, at, token);
                var intent = new Intent(DeliveryElement(op), DeliveryElement(inputs.Capability), command.DeliveryId, command.CancelRequestId,
                    cancel.BindingDigest, receipt.Intent.ReceiptId, SessionReceiptFormat.Link(receipt), receipt.Intent.ProviderId,
                    receipt.Response!.ProviderSessionId, caller.AuthenticationContextId, PolicySnapshotBinding.Digest(policy), control.Version,
                    audit, at, op.Scope.BudgetTicks);
                var digest = Digest(intent);
                await ApprovalDatabase.Execute(c, tx, """
                    INSERT INTO cancellation_deliveries(delivery_id,cancel_request_id,receipt_id,task_id,execution_id,abort_operation_id,
                    captured_control_version,receipt_digest,intent_audit_sequence,intent_at_ticks,budget_ticks,format_version,binding_version,intent_digest,intent_json,
                    version,delivery_stage,remote_outcome,last_audit_sequence)
                    VALUES($d,$cancel,$r,$t,$e,$o,$cv,$rd,$a,$at,$budget,1,1,$digest,$json,0,0,0,$a)
                    """, token, ("$d", intent.DeliveryId), ("$cancel", intent.CancelRequestId), ("$r", intent.ReceiptId), ("$t", op.TaskId),
                    ("$e", op.ExecutionId), ("$o", op.OperationId), ("$cv", intent.CapturedControlVersion), ("$rd", intent.ReceiptDigest),
                    ("$a", audit), ("$at", at), ("$budget", intent.BudgetTicks), ("$digest", digest), ("$json", SessionReceiptFormat.Write(intent, 16384)));
                cancellationCheckpoint?.Invoke("DeliveryIntentWritten");
                _ = await ReadDeliveryStorage(c, tx, command.DeliveryId, token);
                AuthorizeAbort(policy, op, inputs.Capability, caller, e);
                return (op, intent, digest, receipt.Intent.ApprovalId);
            }, token);
            cancellationCheckpoint?.Invoke("DeliveryIntentCommitted");
            return new AbortDeliveryContinuation(reserved.op, reserved.intent.ProviderId,
                t => PreflightAbort(command, inputs, caller, owner, reserved.op, reserved.digest, reserved.intent.CallerAuthenticationContextId, t),
                (stage, diagnostic, t) => CompleteAbort(command, owner, reserved.intent, reserved.digest, reserved.ApprovalId, stage, diagnostic, t));
        }, token);

    void AuthorizeAbort(PolicySnapshot policy, PolicyRequest op, CapabilityDescriptor capability, ITrustedCallerContext caller, DurableExecution e)
    {
        Metadata(op);
        Require(op.Operation == PolicyOperation.AbortExistingExecution && op.Capability == PolicyCapability.ExecutionAbort
            && capability.Capability == PolicyCapability.ExecutionAbort && capability.Boundary == EnforcementBoundary.TaskAdmission
            && capability.SupportedOperations.SequenceEqual([PolicyOperation.AbortExistingExecution])
            && op.Scope.Access == ResourceAccess.ReadOnly && op.Scope.BudgetTicks is > 0 and <= 50000000, "AbortNotSupported");
        var decision = new DeterministicPolicyEvaluator(new ImmutablePolicySource(policy), clock).Evaluate(op, caller);
        Require(decision.Outcome != PolicyOutcome.RequireApproval, "AbortApprovalNotSupported");
        Require(decision.Outcome == PolicyOutcome.Allow && decision.Reason == PolicyReason.Allowed
            && decision.BindingDigest == OperationBinding.Digest(op, capability) && authority.CanControl(caller, op), "AbortNotAuthorized");
        Require(e.Handle.Provider == "OpenCode" && e.TaskId == op.TaskId && e.Handle.ExecutionId == op.ExecutionId
            && e.WorkflowRunId == op.WorkflowRunId && e.Handle.ExternalRequestId == op.MessageId
            && e.Handle.ExternalSessionId == op.ProviderSessionId && e.RequestFingerprint == op.RequestFingerprint
            && e.ProjectRoot == op.Scope.ResourceId && e.ProviderScope == op.Scope.ProviderScopeDigest
            && authority.MatchesExecutionBinding(op, e), "AbortNotAuthorized");
    }
    async Task<(SessionReceiptRecord Receipt, CancellationEvidence Cancel, TaskControl Control)> AbortTarget(SqliteConnection c, SqliteTransaction tx,
        AbortDeliveryCommand command, PolicyRequest op, CancellationToken token)
    {
        var receipt = await ReadSessionReceipt(c, tx, op.TaskId, token);
        var cancel = await ReadCancellation(c, tx, command.CancelRequestId, token);
        var control = await Control(c, tx, op.TaskId, token);
        Require(receipt?.Status == SessionReceiptStatus.Verified && receipt.Response is not null && cancel is not null && control is not null
            && cancel.Operation.TaskId == op.TaskId && cancel.Operation.ExecutionId == op.ExecutionId
            && control.ExecutionId == op.ExecutionId && control.CancelRequestId == command.CancelRequestId
            && control.Version == cancel.CommittedControlVersion && receipt.Response.ProviderSessionId == op.ProviderSessionId, "AbortNotEligible");
        var original = receipt!.Intent.Operation;
        Require(original.TaskId == op.TaskId && original.ExecutionId == op.ExecutionId && original.WorkflowRunId == op.WorkflowRunId
            && original.MessageId == op.MessageId && original.RequestFingerprint == op.RequestFingerprint && original.ConnectorId == op.ConnectorId
            && original.Scope with { BudgetTicks = op.Scope.BudgetTicks } == op.Scope, "AbortNotEligible");
        var (e, _, _) = await CancelTarget(c, tx, op.TaskId, token);
        Require(e is not null && !e.IsTerminal && e.SubmissionPhase >= SubmissionPhase.PromptIntended, "AbortNoActiveSubmission");
        return (receipt, cancel!, control!);
    }
    Task<bool> PreflightAbort(AbortDeliveryCommand command, AbortDeliveryInputs inputs, ITrustedCallerContext caller,
        IDeliveryOwnership owner, PolicyRequest op, string digest, string authenticationContext, CancellationToken token)
        => WithDeliveryOwner(owner, command.CancelRequestId, async () =>
        {
            await using var lease = await authority.AcquireAsync(token); var policy = Candidate();
            return await GuardedTransaction(policy, async (c, tx) =>
            {
                Require(caller.AuthenticationContextId == authenticationContext, "AbortCallerChanged");
                var (e, _, _) = await CancelTarget(c, tx, op.TaskId, token);
                Require(e is not null, "AbortNotAuthorized"); AuthorizeAbort(policy, op, inputs.Capability, caller, e!);
                _ = await AbortTarget(c, tx, command, op, token);
                var row = await ReadDeliveryStorage(c, tx, command.DeliveryId, token);
                Require(row is { Version: 0, Stage: 0, Outcome: 0 } && row.IntentDigest == digest, "DeliveryReservationChanged");
                cancellationCheckpoint?.Invoke("DeliveryPreflightVerified");
                AuthorizeAbort(policy, op, inputs.Capability, caller, e!);
                return true;
            }, token);
        }, token);
    async Task<long> AbortAudit(SqliteConnection c, SqliteTransaction tx, PolicyRequest op, string? approval, long version, long ticks, CancellationToken token)
    {
        Require(Convert.ToInt64(await ApprovalDatabase.Scalar(c, tx, "SELECT count(*) FROM control_audit", token)) < auditCapacity, "AuditCapacityReached");
        await ApprovalDatabase.Execute(c, tx, """
            INSERT INTO control_audit(code,actor,task_id,approval_id,version,recorded_at,policy_epoch,policy_version)
            VALUES(0,$actor,$t,$approval,$v,$at,$epoch,$policy)
            """, token, ("$actor", op.CallerSubject), ("$t", op.TaskId), ("$approval", approval), ("$v", version),
            ("$at", Utc(new DateTimeOffset(ticks, TimeSpan.Zero))), ("$epoch", op.Epoch.Value), ("$policy", op.Version.Value));
        return Convert.ToInt64(await ApprovalDatabase.Scalar(c, tx, "SELECT last_insert_rowid()", token));
    }
    Task<AbortDeliveryEvidence> CompleteAbort(AbortDeliveryCommand command, IDeliveryOwnership owner, Intent intent, string digest,
        string? approval, string stage, string diagnostic, CancellationToken token)
        => WithDeliveryOwner(owner, command.CancelRequestId, () => Transaction(async (c, tx) =>
        {
            // Historical response capture: no current authorization is inferred or extended.
            var before = await ReadDeliveryStorage(c, tx, command.DeliveryId, token);
            Require(before is { Version: 0, Stage: 0, Outcome: 0 } && before.IntentDigest == digest, "DeliveryReservationChanged");
            Require(stage is "Acknowledged" or "SentUncertain" or "SuppressedBeforeWire", "DeliveryStageInvalid");
            Require(new[] { "None", "ResponseLost", "ProviderUnavailable", "InvalidAcknowledgement", "AuthorizationChanged" }.Contains(diagnostic), "DeliveryDiagnosticInvalid");
            var op = SessionReceiptFormat.Read<PolicyRequest>(intent.Operation.GetRawText(), 16384);
            var at = Math.Max(clock.GetUtcNow().UtcTicks, intent.IntentAtTicks);
            var audit = await AbortAudit(c, tx, op, approval, 1, at, token);
            var observation = new Observation(command.DeliveryId, digest, intent.Operation, intent.Capability,
                intent.CallerAuthenticationContextId, intent.PolicyDigest, stage, "None", diagnostic, at,
                stage == "Acknowledged" ? at : null, op.MessageId, null, null, audit);
            Require(await ApprovalDatabase.Execute(c, tx, """
                UPDATE cancellation_deliveries SET version=1,delivery_stage=$s,acknowledgement_at_ticks=$ack,last_observed_at_ticks=$at,
                observation_digest=$hash,observation_json=$json,last_audit_sequence=$audit
                WHERE delivery_id=$d AND version=0 AND delivery_stage=0 AND intent_digest=$digest
                """, token, ("$s", Array.IndexOf(Stages, stage)), ("$ack", observation.AcknowledgementAtTicks), ("$at", at),
                ("$hash", Digest(observation)), ("$json", SessionReceiptFormat.Write(observation, 8192)), ("$audit", audit),
                ("$d", command.DeliveryId), ("$digest", digest)) == 1, "DeliveryReservationChanged");
            cancellationCheckpoint?.Invoke("DeliveryEvidenceWritten");
            _ = await ReadDeliveryStorage(c, tx, command.DeliveryId, token);
            return new AbortDeliveryEvidence(command.DeliveryId, op.TaskId, op.ExecutionId, op.ProviderSessionId!, 1, stage, "None", diagnostic);
        }, token), token);
}
