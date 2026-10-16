using System.Text.Json;
using MAF.Core.Policy;

namespace MAF.Persistence.Sqlite;

// Closed evidence format, not an authorization grant. B2's typed operation is golden-tested
// against these B1b bytes; no existing binding encoding is changed.
internal static class DeliveryStorageFormat
{
    internal sealed record Intent(JsonElement Operation, JsonElement Capability, string DeliveryId, string CancelRequestId,
        string CancellationBindingDigest, string ReceiptId, string ReceiptDigest, string ProviderId, string ProviderSessionId,
        string CallerAuthenticationContextId, string PolicyDigest, long CapturedControlVersion, long IntentAuditSequence,
        long IntentAtTicks, long BudgetTicks);
    internal sealed record Terminal(string ProviderId, string ProviderScope, string SessionId, string OriginalMessageId,
        string? ObservedMessageId, string? ParentMessageId, string? FinishReason, string? ErrorCode, string Outcome, long ObservedAtTicks);
    internal sealed record Observation(string DeliveryId, string IntentDigest, JsonElement ObserverOperation,
        JsonElement ObserverCapability, string ObserverAuthenticationContextId, string ObserverPolicyDigest, string Stage,
        string RemoteOutcome, string Diagnostic, long ObservedAtTicks, long? AcknowledgementAtTicks, string CorrelatedMessageId,
        Terminal? TerminalEvidence, string? TerminalEvidenceDigest, long AuditSequence);
    internal static readonly string[] Stages = ["Intended", "SentUncertain", "Acknowledged", "SuppressedBeforeWire"];
    internal static readonly string[] Outcomes = ["None", "Completed", "Partial", "Failed", "Cancelled"];
    internal static string S(JsonElement e, string key) => e.GetProperty(key).GetString() ?? throw new InvalidOperationException("DeliveryFieldMissing");
    internal static long N(JsonElement e, string key) => e.GetProperty(key).GetInt64();
    static void Fields(JsonElement e, string fields)
    { if (e.ValueKind != JsonValueKind.Object || !e.EnumerateObject().Select(x => x.Name).Order().SequenceEqual(fields.Split(' ').Order())) throw new InvalidOperationException("DeliveryFieldsInvalid"); }
    static void Id(string s)
    { if (s.Length is < 1 or > 160 || !s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':')) throw new InvalidOperationException("DeliveryMetadataInvalid"); }
    internal static void HashText(string s)
    { if (s.Length != 64 || !s.All(c => char.IsAsciiHexDigit(c) && !char.IsLower(c))) throw new InvalidOperationException("DeliveryHashInvalid"); }
    // Layout deliberately matches existing OperationBinding v1; only closed storage
    // names are accepted. A golden test compares the supported Inspect layout to Core.
    internal static byte[] Pair(JsonElement op, JsonElement cap)
    {
        Fields(op, "AuthorityId CallerSubject OperationId TaskId WorkflowRunId ExecutionId MessageId RequestFingerprint ConnectorId Capability Operation Scope Epoch Version IntentGeneration ProviderSessionId");
        Fields(cap, "ConnectorId AdapterVersion Capability Boundary SupportedOperations Enabled Healthy");
        var scope = op.GetProperty("Scope"); Fields(scope, "ResourceId ProviderScopeDigest AgentPolicyDigest ConfigurationRevision ScopeRevision Access BudgetTicks");
        Fields(op.GetProperty("Epoch"), "Value"); Fields(op.GetProperty("Version"), "Value");
        var kind = S(op, "Operation");
        var capability = kind switch { "AbortExistingExecution" => "ExecutionAbort", "InspectExistingExecution" => "ExecutionInspection", _ => throw new InvalidOperationException("DeliveryOperationNotSupported") };
        foreach (var key in "AuthorityId CallerSubject OperationId TaskId WorkflowRunId ExecutionId MessageId ConnectorId ProviderSessionId".Split(' ')) Id(S(op, key));
        foreach (var value in new[] { S(op, "RequestFingerprint"), S(scope, "ProviderScopeDigest"), S(scope, "AgentPolicyDigest") }) HashText(value);
        if (S(op, "Capability") != capability || S(cap, "Capability") != capability || S(cap, "ConnectorId") != S(op, "ConnectorId")
            || S(cap, "Boundary") != "TaskAdmission" || !cap.GetProperty("Enabled").GetBoolean() || !cap.GetProperty("Healthy").GetBoolean()
            || !cap.GetProperty("SupportedOperations").EnumerateArray().Select(x => x.GetString()).SequenceEqual([kind])
            || S(scope, "Access") != "ReadOnly" || N(scope, "ConfigurationRevision") <= 0 || N(scope, "ScopeRevision") <= 0
            || N(scope, "BudgetTicks") <= 0 || N(op.GetProperty("Epoch"), "Value") <= 0 || N(op.GetProperty("Version"), "Value") <= 0
            || N(op, "IntentGeneration") != 1 || !PolicyValidation.Text(S(scope, "ResourceId")) || !PolicyValidation.Text(S(cap, "AdapterVersion")))
            throw new InvalidOperationException("DeliveryOperationInvalid");
        using var w = new SessionReceiptFormat.Writer(); w.Text("MAF.OperationBinding"); w.Number(1);
        w.Text(S(op, "AuthorityId")); w.Number(N(op.GetProperty("Epoch"), "Value")); w.Number(N(op.GetProperty("Version"), "Value"));
        foreach (var key in "CallerSubject OperationId TaskId WorkflowRunId ExecutionId MessageId RequestFingerprint ConnectorId Capability Operation".Split(' ')) w.Text(S(op, key));
        w.Text(S(scope, "ResourceId")); w.Text(S(scope, "ProviderScopeDigest")); w.Text(S(scope, "AgentPolicyDigest"));
        w.Number(N(scope, "ConfigurationRevision")); w.Number(N(scope, "ScopeRevision")); w.Text("ReadOnly"); w.Number(N(scope, "BudgetTicks"));
        w.Number(1); w.Text(S(op, "ProviderSessionId")); w.Text(S(cap, "ConnectorId")); w.Text(S(cap, "AdapterVersion")); w.Text(capability);
        w.Text("TaskAdmission"); w.Number(1); w.Number(1); w.Number(1); w.Text(kind); return w.Stream.ToArray();
    }
    internal static string Digest(Intent i) => SessionReceiptFormat.Hash("CancellationDeliveryBinding", w =>
    { w.Bytes(Pair(i.Operation, i.Capability)); w.Text(i.DeliveryId); w.Text(i.CancelRequestId); w.Text(i.CancellationBindingDigest); w.Text(i.ReceiptId); w.Text(i.ReceiptDigest); w.Text(i.ProviderId); w.Text(i.ProviderSessionId); w.Text(i.CallerAuthenticationContextId); w.Text(i.PolicyDigest); w.Number(i.CapturedControlVersion); w.Number(i.IntentAuditSequence); w.Number(i.IntentAtTicks); w.Number(i.BudgetTicks); });
    internal static byte[] TerminalBytes(Terminal t)
    {
        using var w = new SessionReceiptFormat.Writer(); w.Text("MAF.TerminalObservationBinding"); w.Number(1);
        foreach (var s in new[] { t.ProviderId, t.ProviderScope, t.SessionId, t.OriginalMessageId, t.ObservedMessageId, t.ParentMessageId, t.FinishReason, t.ErrorCode, t.Outcome }) w.Text(s);
        w.Number(t.ObservedAtTicks); return w.Stream.ToArray();
    }
    internal static string TerminalDigest(Terminal t) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(TerminalBytes(t)));
    internal static string Digest(Observation o) => SessionReceiptFormat.Hash("CancellationObservationBinding", w =>
    { w.Text(o.DeliveryId); w.Text(o.IntentDigest); w.Bytes(Pair(o.ObserverOperation, o.ObserverCapability)); w.Text(o.ObserverAuthenticationContextId); w.Text(o.ObserverPolicyDigest); w.Text(o.Stage); w.Text(o.RemoteOutcome); w.Text(o.Diagnostic); w.Number(o.ObservedAtTicks); w.Nullable(o.AcknowledgementAtTicks); w.Text(o.CorrelatedMessageId); w.Bytes(o.TerminalEvidence is null ? null : TerminalBytes(o.TerminalEvidence)); w.Text(o.TerminalEvidenceDigest); w.Number(o.AuditSequence); });
}
