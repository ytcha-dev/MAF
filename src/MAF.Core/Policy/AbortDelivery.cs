using System.Text.Json;
using System.Text.Json.Serialization;

namespace MAF.Core.Policy;

public interface IDeliveryOwnership : IAsyncDisposable { string CancelRequestId { get; } }
public sealed record AbortDeliveryCommand(string DeliveryId, string CancelRequestId, string TaskId, string ExecutionId);
// Trusted application configuration, not caller-supplied claims. The authority verifies it again.
public sealed record AbortDeliveryInputs(string AuthorityId, PolicyEpoch Epoch, PolicyVersion Version,
    AuthorizationScope Scope, CapabilityDescriptor Capability, string OperationId);
public sealed record AbortDeliveryEvidence(string DeliveryId, string TaskId, string ExecutionId, string SessionId,
    long Version, string Stage, string RemoteOutcome, string Diagnostic);
public interface IAbortDeliveryCoordinator
{
    ValueTask<IDeliveryOwnership?> TryAcquireDeliveryAsync(string cancelRequestId, CancellationToken token = default);
    Task<AbortDeliveryContinuation> ReserveAbortAsync(AbortDeliveryCommand command, AbortDeliveryInputs inputs,
        ITrustedCallerContext caller, IDeliveryOwnership owner, CancellationToken token = default);
}

// Only a successful committing coordinator issues this process-local capability. It is not
// recoverable from a row and does not claim isolation from malicious same-principal code.
[JsonConverter(typeof(AbortDeliveryContinuationConverter))]
public sealed class AbortDeliveryContinuation
{
    internal readonly PolicyRequest Operation;
    internal readonly string Provider;
    internal readonly Func<CancellationToken, Task> Preflight;
    internal readonly Func<string, string, CancellationToken, Task<AbortDeliveryEvidence>> Complete;
    int consumed;
    internal AbortDeliveryContinuation(PolicyRequest operation, string provider, Func<CancellationToken, Task> preflight,
        Func<string, string, CancellationToken, Task<AbortDeliveryEvidence>> complete)
    { Operation = operation; Provider = provider; Preflight = preflight; Complete = complete; }
    internal void Consume()
    { if (Interlocked.Exchange(ref consumed, 1) != 0) throw new InvalidOperationException("DeliveryContinuationUnavailable"); }
}
internal sealed class AbortDeliveryContinuationConverter : JsonConverter<AbortDeliveryContinuation>
{
    public override AbortDeliveryContinuation Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        => throw new NotSupportedException("DeliveryCannotBeRestored");
    public override void Write(Utf8JsonWriter writer, AbortDeliveryContinuation value, JsonSerializerOptions options)
        => throw new NotSupportedException("DeliveryCannotBePersisted");
}
