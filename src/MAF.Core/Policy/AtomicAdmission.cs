using System.Text.Json;
using System.Text.Json.Serialization;

namespace MAF.Core.Policy;

// A fixed local transaction, not a provider command or an approval-consumption replacement.
// All inputs are revalidated against the injected protected authority and durable active tuple;
// a caller-created PolicyDecision never constitutes authenticated admission evidence by itself.
public interface IExecutionAdmissionCoordinator
{
    Task<AdmissionContinuation> AdmitAllowedAsync(PolicyRequest operation, PolicyDecision assessment,
        ExternalTaskRequest request, DurableExecution initialExecution, long expectedControlVersion,
        ITrustedCallerContext caller, IExecutionOwnership ownership, CancellationToken token = default);
}

// Only the trusted persistence implementation can construct this after commit. No public factory,
// persisted representation or restoration API. Consumption is a metadata handoff, NOT dispatch
// authorization: future runtime enforcement must still revalidate policy/control before effects.
[JsonConverter(typeof(AdmissionContinuationJsonConverter))]
public sealed class AdmissionContinuation
{
    readonly DurableExecution execution;
    readonly IExecutionOwnership owner;
    readonly Func<IExecutionOwnership, CancellationToken, ValueTask<IAsyncDisposable>> guard;
    int consumed;
    internal AdmissionContinuation(DurableExecution execution, IExecutionOwnership owner,
        Func<IExecutionOwnership, CancellationToken, ValueTask<IAsyncDisposable>> guard)
    { this.execution = execution; this.owner = owner; this.guard = guard; }

    public async ValueTask<DurableExecution> ConsumeAsync(IExecutionOwnership ownership, CancellationToken token = default)
    {
        if (!ReferenceEquals(owner, ownership)) throw new InvalidOperationException("ContinuationOwnerMismatch");
        await using var use = await guard(ownership, token);
        if (Interlocked.CompareExchange(ref consumed, 1, 0) != 0)
            throw new InvalidOperationException("ContinuationAlreadyConsumed");
        return execution;
    }
}

internal sealed class AdmissionContinuationJsonConverter : JsonConverter<AdmissionContinuation>
{
    public AdmissionContinuationJsonConverter() { }
    public override AdmissionContinuation Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        => throw new NotSupportedException("AdmissionContinuationCannotBeRestored");
    public override void Write(Utf8JsonWriter writer, AdmissionContinuation value, JsonSerializerOptions options)
        => throw new NotSupportedException("AdmissionContinuationCannotBePersisted");
}
