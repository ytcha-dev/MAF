using System.Text.Json;
using System.Text.Json.Serialization;

namespace MAF.Core.Policy;

public enum SessionReceiptStatus { Pending, Verified, Quarantined }
public sealed record SessionReceiptIntent(PolicyRequest Operation, CapabilityDescriptor Capability, string ProviderId,
    string ReceiptId, string? ApprovalId, long? ApprovalVersion, string? ApprovalBindingDigest,
    string CallerAuthenticationContextId, string PolicyDigest, long PriorExecutionVersion, long IntentExecutionVersion,
    long IntentControlVersion, long IntentAuditSequence, long IntentAtTicks, long RequestTimeoutTicks, long ResponseLimitBytes);
public sealed record SessionReceiptResponse(string ReceiptId, string IntentDigest, string ProviderSessionId,
    SessionReceiptStatus Verification, string Diagnostic, long ObservedAtTicks, long FinalizedAtTicks, long ResponseAuditSequence);
public sealed record SessionReceiptRecord(SessionReceiptIntent Intent, string IntentDigest, SessionReceiptResponse? Response,
    string? ResponseDigest, long Version, SessionReceiptStatus Status);

// Only the committing persistence coordinator issues this. The response bridge is trusted code,
// not a security boundary against malicious same-principal assemblies/reflection.
[JsonConverter(typeof(SessionReceiptRegistrationConverter))]
public sealed class SessionReceiptRegistration
{
    internal readonly IExecutionOwnership Owner;
    internal readonly SessionReceiptRecord Receipt;
    readonly Func<SessionReceiptRegistration, string, bool, string, long, CancellationToken, Task<DurableExecution>> finalize;
    int state;
    internal SessionReceiptRegistration(IExecutionOwnership owner, SessionReceiptRecord receipt,
        Func<SessionReceiptRegistration, string, bool, string, long, CancellationToken, Task<DurableExecution>> finalize)
    { Owner = owner; Receipt = receipt; this.finalize = finalize; }
    internal void Arm()
    { if (Interlocked.CompareExchange(ref state, 1, 0) != 0) throw new InvalidOperationException("ReceiptRegistrationNotArmable"); }
    internal Task<DurableExecution> Complete(string session, bool verified, string diagnostic, long observed, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref state, 2, 1) != 1) throw new InvalidOperationException("ReceiptRegistrationUnavailable");
        return finalize(this, session, verified, diagnostic, observed, token);
    }
    internal void Burn() => Interlocked.Exchange(ref state, 2);
}
internal sealed class SessionReceiptRegistrationConverter : JsonConverter<SessionReceiptRegistration>
{
    public override SessionReceiptRegistration Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        => throw new NotSupportedException("SessionReceiptCannotBeRestored");
    public override void Write(Utf8JsonWriter writer, SessionReceiptRegistration value, JsonSerializerOptions options)
        => throw new NotSupportedException("SessionReceiptCannotBePersisted");
}

public interface ISessionReceiptCoordinator
{
    bool RequiresSessionReceipts { get; }
    Task<SessionReceiptRegistration> CommitCreateIntentAsync(PolicyRequest operation, PolicyDecision assessment,
        CapabilityDescriptor capability, DurableExecution expected, DurableExecution intent, long controlVersion,
        long requestTimeoutTicks, long responseLimitBytes, ITrustedCallerContext caller, IExecutionOwnership owner,
        ConsumedApprovalAuthorization? approval, CancellationToken token = default);
    Task<SessionReceiptRecord?> GetSessionReceiptAsync(string taskId, CancellationToken token = default);
}
