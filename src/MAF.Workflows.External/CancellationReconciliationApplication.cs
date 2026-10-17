using MAF.Core;
using MAF.Core.Policy;
using MAF.Workflows.External.OpenCode;

namespace MAF.Workflows.External;

// Explicit one-shot inspection only. Host does not register production trust/composition.
public sealed class CancellationReconciliationApplication(ExternalAdmissionInputs inputs, IPolicySource policy,
    ITrustedIdentityResolver identities, IExecutionStore executions, IReconciliationCoordinator coordinator,
    IRuntimeEnforcementCoordinator enforcement, HttpClient http, OpenCodeOptions options, TimeProvider clock)
{
    internal Action<string>? Checkpoint { get; init; }
    public async Task<ReconciliationResult> ReconcileAsync(string deliveryId, string taskId, ITrustedCallerContext caller, CancellationToken token = default)
    {
        try
        {
            var identity = caller is null || identities is null ? null : identities.Resolve(caller); var now = clock.GetUtcNow();
            if (identity is null || identity.SubjectId != caller!.SubjectId || identity.AuthenticationContextId != caller.AuthenticationContextId
                || !Enum.IsDefined(identity.Kind) || identity.Kind == IdentityKind.Unknown || now < identity.NotBefore || now >= identity.NotAfter)
                throw new InvalidOperationException("InvalidCaller");
        }
        catch { throw new ExternalHarnessException("InvalidCaller"); }
        options.Validate();
        if (!ReceiptTransport.IsOwned(http) || DurableExecution.NormalizeRoot(options.ApprovedProjectRoot) != inputs.Scope.ResourceId
            || new OpenCodeClient(http, options).Scope != inputs.Scope.ProviderScopeDigest || inputs.Scope.BudgetTicks <= 0
            || inputs.Scope.BudgetTicks > TimeSpan.FromSeconds(60).Ticks)
            throw new ExternalHarnessException("InspectConfigurationMismatch");
        await using var owner = await executions.TryAcquireAsync(taskId, token) ?? throw new ExternalHarnessException("ExecutionOwned");
        var session = await coordinator.BeginReconciliationAsync(deliveryId, taskId,
            new(inputs.AuthorityId, inputs.Epoch, inputs.Version, inputs.Scope, inputs.InspectionCapability, inputs.OperationId), caller, owner, token);
        var e = session.Execution;
        var intent = new ExternalAdmissionRequest(ExternalAdmissionMode.Inspect, taskId, TimeSpan.FromTicks(inputs.Scope.BudgetTicks));
        var actual = inputs with { Executions = [e], PreparedHandle = e.Handle, NewWorkflowRunId = e.WorkflowRunId };
        using var access = new ProtectedExternalRuntime.ProtectedAccess(actual, policy, identities!, executions, enforcement, owner, caller,
            session.ControlVersion, intent, true, clock, options);
        var client = new OpenCodeClient(access, http, options, clock);
        ExternalObservation observation;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(intent.Timeout);
        try
        {
            Checkpoint?.Invoke("ReconciliationBeforeGet");
            await client.ReattachAsync(taskId, inputs.Scope.ResourceId, e.Handle, budget.Token);
            observation = await client.ObserveAsync(e.Handle, budget.Token); // Same parser/GETs; no durable-service independent write.
        }
        catch (Exception error) when (error is ExternalHarnessException or OperationCanceledException or HttpRequestException)
        { return new(ExternalExecutionStatus.Unknown, false, "ObservationUnavailable"); }
        Checkpoint?.Invoke("ReconciliationAfterGet");
        return await session.Capture(observation, token);
    }
}
