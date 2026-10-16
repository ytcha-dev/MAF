using System.Text.Json;
using System.Text.Json.Serialization;

namespace MAF.Core.Policy;

public sealed record ApprovalResumeRequest(string ApprovalId, long ApprovalVersion, string BindingDigest, ExternalTaskRequest? Input);

public interface IApprovalResumeCoordinator
{
    Task<AdmissionContinuation> ConsumeOwnedAsync(ApprovalResumeRequest request, PolicyRequest operation, DurableExecution initial,
        long controlVersion, ITrustedCallerContext caller, ITrustedCallerContext operatorContext, IExecutionOwnership owner, CancellationToken token = default);
    Task VerifyApprovedEffectAsync(ConsumedApprovalAuthorization evidence, PolicyRequest operation, PolicyDecision assessment,
        DurableExecution expected, DurableExecution? intent, long controlVersion, ProviderEffect effect,
        ITrustedCallerContext caller, IExecutionOwnership owner, CancellationToken token = default);
}

// Minted only by persistence after successful atomic consumption. Neither a serialized row nor
// an ID is this evidence. The committing owner and fresh operator context must still verify.
[JsonConverter(typeof(ConsumedApprovalAuthorizationConverter))]
public sealed class ConsumedApprovalAuthorization
{
    public ApprovalRecord Approval { get; }
    internal string Database { get; }
    internal IExecutionOwnership Owner { get; }
    internal ITrustedCallerContext Operator { get; }
    internal ConsumedApprovalAuthorization(ApprovalRecord approval, string database, IExecutionOwnership owner, ITrustedCallerContext operatorContext)
    { Approval = approval; Database = database; Owner = owner; Operator = operatorContext; }
}
internal sealed class ConsumedApprovalAuthorizationConverter : JsonConverter<ConsumedApprovalAuthorization>
{
    public ConsumedApprovalAuthorizationConverter() { }
    public override ConsumedApprovalAuthorization Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        => throw new NotSupportedException("ApprovalAuthorizationCannotBeRestored");
    public override void Write(Utf8JsonWriter writer, ConsumedApprovalAuthorization value, JsonSerializerOptions options)
        => throw new NotSupportedException("ApprovalAuthorizationCannotBePersisted");
}
