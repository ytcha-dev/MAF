using MAF.Core;
using MAF.Core.Policy;
using MAF.Core.Tools;
using MAF.Workflows.External.OpenCode;

namespace MAF.Workflows.External;

// Explicit trusted composition only. Host installs no source/resolver and cannot fall back here.
public sealed class ProtectedExternalRuntime(ExternalAdmissionInputs inputs, IPolicySource policy,
    ITrustedIdentityResolver identities, IExecutionStore executions, IApprovalStore controls,
    IExecutionAdmissionCoordinator admission, IRuntimeEnforcementCoordinator enforcement,
    HttpClient http, OpenCodeOptions options, TimeProvider clock)
{
    public Task<IReadOnlyList<DurableExecution>> ListAsync(CancellationToken token = default)
        => throw new ExternalHarnessException("MetadataListNotSupported");

    public async Task<ExternalWorkflowRun> RunAsync(ExternalAdmissionRequest intent, ITrustedCallerContext caller,
        bool observeOnce = false, CancellationToken token = default)
    {
        if (intent.Mode is not (ExternalAdmissionMode.Submit or ExternalAdmissionMode.Inspect))
            throw new ExternalHarnessException("OperationNotSupported");
        if (string.IsNullOrWhiteSpace(intent.TaskId) || intent.TaskId.Length > 100
            || intent.Timeout < TimeSpan.FromMilliseconds(10) || intent.Timeout > TimeSpan.FromMinutes(10))
            throw new ExternalHarnessException("InvalidRequest");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(intent.Timeout);
        token = budget.Token; // One protected budget across admission, reattach and Workflow observation.
        // Authenticate before acquiring/reading TaskId metadata; reads are internal, never disclosed on denial.
        var identity = identities.Resolve(caller);
        var now = clock.GetUtcNow();
        if (identity is null || identity.SubjectId != caller.SubjectId || identity.AuthenticationContextId != caller.AuthenticationContextId
            || identity.Kind == IdentityKind.Unknown || !Enum.IsDefined(identity.Kind) || now < identity.NotBefore || now >= identity.NotAfter)
            throw new ExternalHarnessException("InvalidCaller");
        if (DurableExecution.NormalizeRoot(new ProjectTools(inputs.Scope.ResourceId).Root) != inputs.Scope.ResourceId
            || DurableExecution.NormalizeRoot(options.ApprovedProjectRoot) != inputs.Scope.ResourceId)
            throw new ExternalHarnessException("RuntimeScopeMismatch");
        await using var owner = await executions.TryAcquireAsync(intent.TaskId, token)
            ?? throw new ExternalHarnessException("TaskAlreadyOwned");
        var existing = await executions.GetAsync(intent.TaskId, token);
        var trusted = inputs with { Executions = existing is null ? [] : [existing] };
        var result = new ExternalExecutionApplication(trusted, policy, identities, clock).Assess(intent, caller);
        if (result.Decision.Outcome != PolicyOutcome.Allow || result.Operation is null)
            throw new ExternalHarnessException(result.Decision.Reason.ToString());
        var control = await controls.GetControlAsync(intent.TaskId, token);
        using var access = new ProtectedAccess(trusted, policy, identities, executions, enforcement,
            owner, caller, control?.Version ?? 0, intent, existing is not null, clock);
        var client = new OpenCodeClient(access, http, options);
        if (client.Scope != inputs.Scope.ProviderScopeDigest || client.Provider != inputs.ProviderId)
            throw new ExternalHarnessException("RuntimeProviderMismatch");
        var store = new IntentStore(executions, access);
        using var service = new DurableExecutionService(access, store, client, TimeSpan.FromMilliseconds(5));
        if (existing is not null)
        {
            await access.VerifyAsync(ProviderEffect.Metadata, existing, null, token);
            return await service.InspectOwnedAsync(existing, inputs.Scope.ResourceId, intent.Timeout, owner, observeOnce, token);
        }
        var continuation = await new AtomicAdmissionApplication(trusted, policy, identities, admission, clock)
            .AdmitAsync(intent, caller, owner, control?.Version ?? 0, token);
        var created = await continuation.ConsumeAsync(owner, token);
        return await service.LaunchOwnedAsync(intent.Submission!, created, owner, token);
    }

    sealed class IntentStore(IExecutionStore inner, ProtectedAccess access) : IExecutionStore
    {
        public ValueTask<IExecutionOwnership?> TryAcquireAsync(string task, CancellationToken token = default) => throw new ExternalHarnessException("OwnedRuntimeRequired");
        public Task CreateAsync(DurableExecution e, IExecutionOwnership owner, CancellationToken token = default) => throw new ExternalHarnessException("AtomicAdmissionRequired");
        public Task<IReadOnlyList<DurableExecution>> ListRecoverableAsync(CancellationToken token = default) => throw new ExternalHarnessException("MetadataListNotSupported");
        public Task<DurableExecution?> GetAsync(string task, CancellationToken token = default) => inner.GetAsync(task, token);
        public Task UpdateAsync(DurableExecution before, DurableExecution next, IExecutionOwnership owner, CancellationToken token = default)
            => next.SubmissionPhase != before.SubmissionPhase && next.SubmissionPhase is SubmissionPhase.SessionCreationIntended or SubmissionPhase.PromptIntended
                ? access.VerifyAsync(next.SubmissionPhase == SubmissionPhase.PromptIntended ? ProviderEffect.SubmitPrompt : ProviderEffect.CreateSession, before, next, token)
                : inner.UpdateAsync(before, next, owner, token);
    }

    // Per-owned-run, never exposed as a dispatch permit or shared across executions.
    internal sealed class ProtectedAccess(ExternalAdmissionInputs inputs, IPolicySource policy, ITrustedIdentityResolver identities,
        IExecutionStore store, IRuntimeEnforcementCoordinator enforcement, IExecutionOwnership owner, ITrustedCallerContext caller,
        long controlVersion, ExternalAdmissionRequest intent, bool recovery, TimeProvider clock) : IRuntimeAccess, IDisposable
    {
        sealed record EffectCall(ProviderEffect Kind, ExternalExecutionHandle? Handle);
        readonly AsyncLocal<EffectCall?> current = new();
        bool disposed;
        public bool SuppressImplicitAbort => true;
        public void DemandEntry() { if (disposed) throw new ExternalHarnessException("ProtectedRuntimeExpired"); }
        public void Dispose() => disposed = true;
        public IDisposable Enter(ProviderEffect effect, ExternalTaskRequest? request = null, string? run = null, ExternalExecutionHandle? handle = null)
        {
            DemandEntry();
            if (effect == ProviderEffect.Abort) throw new ExternalHarnessException("AbortNotSupported");
            if (recovery && effect is ProviderEffect.CreateSession or ProviderEffect.SubmitPrompt)
                throw new ExternalHarnessException("RecoveryOnly");
            if (request is not null && (request.TaskId != intent.TaskId || DurableExecution.NormalizeRoot(request.ProjectRoot) != inputs.Scope.ResourceId
                || request.Policy != ExternalExecutionPolicy.ReadOnly
                || effect != ProviderEffect.Validate && DurableExecution.Fingerprint(request) != DurableExecution.Fingerprint(intent.Submission!)))
                throw new ExternalHarnessException("RuntimeRequestMismatch");
            if (run is not null && run != inputs.NewWorkflowRunId) throw new ExternalHarnessException("RuntimeRunMismatch");
            var previous = current.Value; current.Value = new(effect, handle);
            return new EffectScope(() => current.Value = previous);
        }
        sealed class EffectScope(Action release) : IDisposable { public void Dispose() => release(); }
        public async Task VerifyAsync(ProviderEffect effect, DurableExecution record, DurableExecution? next, CancellationToken token)
        {
            DemandEntry();
            var inspecting = effect is ProviderEffect.Observe or ProviderEffect.Reattach or ProviderEffect.Metadata || recovery;
            var assessmentInputs = inputs with { Executions = inspecting ? [record] : [] };
            var request = inspecting ? new ExternalAdmissionRequest(ExternalAdmissionMode.Inspect, record.TaskId, intent.Timeout) : intent;
            var assessment = new ExternalExecutionApplication(assessmentInputs, policy, identities, clock).Assess(request, caller);
            if (assessment.Decision.Outcome != PolicyOutcome.Allow || assessment.Operation is null)
                throw new ExternalHarnessException(assessment.Decision.Reason.ToString());
            var op = assessment.Operation with { ProviderSessionId = record.Handle.ExternalSessionId };
            var descriptor = inspecting ? inputs.InspectionCapability : inputs.SubmissionCapability;
            var decision = assessment.Decision with { BindingDigest = OperationBinding.Digest(op, descriptor) };
            try { await enforcement.VerifyEffectAsync(op, decision, record, next, controlVersion, effect, caller, owner, token); }
            catch (InvalidOperationException) { throw new ExternalHarnessException("RuntimeAuthorizationDenied"); }
        }
        public async Task BeforeWireAsync(HttpMethod method, string path, string task, CancellationToken token)
        {
            DemandEntry(); token.ThrowIfCancellationRequested();
            var call = current.Value ?? throw new ExternalHarnessException("EffectContextRequired");
            var record = await store.GetAsync(intent.TaskId, token) ?? throw new ExternalHarnessException("MissingExecution");
            if (task != record.TaskId || call.Handle is not null && call.Handle != record.Handle)
                throw new ExternalHarnessException("RuntimeHandleMismatch");
            var route = path.Split('?')[0];
            var session = "session/" + Uri.EscapeDataString(record.Handle.ExternalSessionId ?? "");
            var permitted = call.Kind switch
            {
                ProviderEffect.CreateSession => method == HttpMethod.Post && route == "session",
                ProviderEffect.SubmitPrompt => method == HttpMethod.Get && route == session || method == HttpMethod.Post && route == session + "/prompt_async",
                ProviderEffect.Validate => method == HttpMethod.Get && route is "global/health" or "doc" or "agent",
                ProviderEffect.Reattach => method == HttpMethod.Get && route == session,
                ProviderEffect.Observe => method == HttpMethod.Get && (route == session || route == session + "/message" || route is "permission" or "question" or "session/status"),
                _ => false
            };
            if (!permitted) throw new ExternalHarnessException("ProviderRouteDenied");
            // Last local preflight: no SQLite transaction spans the HTTP call that follows.
            await VerifyAsync(call.Kind, record, null, token);
        }
    }
}
