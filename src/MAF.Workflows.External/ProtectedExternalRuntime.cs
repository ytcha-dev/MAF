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

    public Task<ExternalWorkflowRun> RunAsync(ExternalAdmissionRequest intent, ITrustedCallerContext caller,
        bool observeOnce = false, CancellationToken token = default)
        => RunCoreAsync(intent, caller, observeOnce, token, null, null);

    public Task<ExternalWorkflowRun> ResumeAsync(ApprovalResumeRequest resume, ITrustedCallerContext caller,
        ITrustedCallerContext operatorContext, CancellationToken token = default)
    {
        if (resume.Input is null) throw new ExternalHarnessException("InputRequired");
        return RunCoreAsync(new(ExternalAdmissionMode.Submit, resume.Input.TaskId, resume.Input.Timeout, resume.Input),
            caller, true, token, resume, operatorContext);
    }

    async Task<ExternalWorkflowRun> RunCoreAsync(ExternalAdmissionRequest intent, ITrustedCallerContext caller,
        bool observeOnce, CancellationToken token, ApprovalResumeRequest? resume, ITrustedCallerContext? operatorContext)
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
        if (enforcement is ISessionReceiptCoordinator { RequiresSessionReceipts: true } && !ReceiptTransport.IsOwned(http))
            throw new ExternalHarnessException("ReceiptTransportRequired");
        if (DurableExecution.NormalizeRoot(new ProjectTools(inputs.Scope.ResourceId).Root) != inputs.Scope.ResourceId
            || DurableExecution.NormalizeRoot(options.ApprovedProjectRoot) != inputs.Scope.ResourceId)
            throw new ExternalHarnessException("RuntimeScopeMismatch");
        await using var owner = await executions.TryAcquireAsync(intent.TaskId, token)
            ?? throw new ExternalHarnessException("TaskAlreadyOwned");
        var existing = await executions.GetAsync(intent.TaskId, token);
        if (resume is not null && existing is not null)
            throw new ExternalHarnessException("ExistingExecutionRecoveryOnly"); // Explicit Inspect is a separate authorization.
        var trusted = inputs with { Executions = existing is null ? [] : [existing] };
        if (resume is not null)
        {
            // Restore identity metadata, never a continuation/grant. Consumption below must still
            // verify the exact row, policy, caller, operator and ownership atomically.
            var approval = await controls.GetAsync(resume.ApprovalId, token) ?? throw new ExternalHarnessException("UnknownApproval");
            var original = approval.Operation;
            if (original.TaskId != intent.TaskId || original.CallerSubject != identity.SubjectId)
                throw new ExternalHarnessException("ResumeOperationMismatch");
            trusted = trusted with { OperationId = original.OperationId, NewWorkflowRunId = original.WorkflowRunId,
                PreparedHandle = new(original.ExecutionId, inputs.ProviderId, ExternalRequestId: original.MessageId) };
        }
        var result = new ExternalExecutionApplication(trusted, policy, identities, clock).Assess(intent, caller);
        var approvedResume = resume is not null && existing is null;
        if (result.Decision.Outcome != (approvedResume ? PolicyOutcome.RequireApproval : PolicyOutcome.Allow) || result.Operation is null)
            throw new ExternalHarnessException(result.Decision.Reason.ToString());
        var control = await controls.GetControlAsync(intent.TaskId, token);
        // Provider configuration validation is local and precedes any approval consumption.
        var projection = new OpenCodeClient(http, options);
        if (projection.Scope != inputs.Scope.ProviderScopeDigest || projection.Provider != inputs.ProviderId)
            throw new ExternalHarnessException("RuntimeProviderMismatch");
        DurableExecution? created = null;
        ConsumedApprovalAuthorization? evidence = null;
        if (existing is null)
        {
            if (approvedResume)
            {
                if (enforcement is not IApprovalResumeCoordinator coordinator) throw new ExternalHarnessException("ResumeNotSupported");
                var op = result.Operation; var at = clock.GetUtcNow();
                var initial = new DurableExecution(op.TaskId, op.RequestFingerprint, op.Scope.ProviderScopeDigest, op.Scope.ResourceId,
                    op.WorkflowRunId, new(op.ExecutionId, inputs.ProviderId, ExternalRequestId: op.MessageId), DurableExecutionState.Created, SubmissionPhase.None, at, at);
                var continuation = await coordinator.ConsumeOwnedAsync(resume!, op, initial, control?.Version ?? 0, caller, operatorContext!, owner, token);
                (created, evidence) = await continuation.ConsumeApprovedAsync(owner, token);
            }
            else
            {
                var continuation = await new AtomicAdmissionApplication(trusted, policy, identities, admission, clock)
                    .AdmitAsync(intent, caller, owner, control?.Version ?? 0, token);
                created = await continuation.ConsumeAsync(owner, token);
            }
        }
        using var access = new ProtectedAccess(trusted, policy, identities, executions, enforcement,
            owner, caller, control?.Version ?? 0, intent, existing is not null, clock, options, evidence);
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
        return await service.LaunchOwnedAsync(intent.Submission!, created!, owner, token);
    }

    sealed class IntentStore(IExecutionStore inner, ProtectedAccess access) : IExecutionStore, IOriginalCreateStore
    {
        public ValueTask<IExecutionOwnership?> TryAcquireAsync(string task, CancellationToken token = default) => throw new ExternalHarnessException("OwnedRuntimeRequired");
        public Task CreateAsync(DurableExecution e, IExecutionOwnership owner, CancellationToken token = default) => throw new ExternalHarnessException("AtomicAdmissionRequired");
        public Task<IReadOnlyList<DurableExecution>> ListRecoverableAsync(CancellationToken token = default) => throw new ExternalHarnessException("MetadataListNotSupported");
        public Task<DurableExecution?> GetAsync(string task, CancellationToken token = default) => inner.GetAsync(task, token);
        public DurableExecution? TakeOriginalCreate(ExternalExecutionHandle handle) => access.TakeOriginalCreate(handle);
        public Task UpdateAsync(DurableExecution before, DurableExecution next, IExecutionOwnership owner, CancellationToken token = default)
            => next.SubmissionPhase != before.SubmissionPhase && next.SubmissionPhase is SubmissionPhase.SessionCreationIntended or SubmissionPhase.PromptIntended
                ? access.VerifyAsync(next.SubmissionPhase == SubmissionPhase.PromptIntended ? ProviderEffect.SubmitPrompt : ProviderEffect.CreateSession, before, next, token)
                : inner.UpdateAsync(before, next, owner, token);
    }

    // Per-owned-run, never exposed as a dispatch permit or shared across executions.
    internal sealed class ProtectedAccess(ExternalAdmissionInputs inputs, IPolicySource policy, ITrustedIdentityResolver identities,
        IExecutionStore store, IRuntimeEnforcementCoordinator enforcement, IExecutionOwnership owner, ITrustedCallerContext caller,
        long controlVersion, ExternalAdmissionRequest intent, bool recovery, TimeProvider clock, OpenCodeOptions? optionsForReceipt = null,
        ConsumedApprovalAuthorization? approval = null) : IRuntimeAccess, IOriginalCreateAccess, IDisposable
    {
        sealed record EffectCall(ProviderEffect Kind, ExternalExecutionHandle? Handle);
        readonly AsyncLocal<EffectCall?> current = new();
        bool disposed;
        public bool SuppressImplicitAbort => true;
        public void DemandEntry() { if (disposed) throw new ExternalHarnessException("ProtectedRuntimeExpired"); }
        SessionReceiptRegistration? registration;
        HttpClient? transport; HttpRequestMessage? originalRequest; Uri? originalUri;
        bool captured;
        DurableExecution? finalized;
        OpenCodeOptions ReceiptOptions => optionsForReceipt ?? throw new ExternalHarnessException("ReceiptConfigurationRequired");
        public bool ReceiptEnabled => enforcement is ISessionReceiptCoordinator { RequiresSessionReceipts: true };
        public void Dispose() { registration?.Burn(); disposed = true; }
        public DurableExecution? TakeOriginalCreate(ExternalExecutionHandle handle)
        {
            if (finalized is null) return null;
            if (finalized.Handle != handle) throw new ExternalHarnessException("ReceiptHandleMismatch");
            var record = finalized; finalized = null; return record;
        }
        public void ArmOriginalCreate(HttpClient client, HttpRequestMessage request)
        {
            DemandEntry();
            if (!ReceiptEnabled || current.Value?.Kind != ProviderEffect.CreateSession) return;
            if (!ReceiptTransport.IsOwned(client) || registration is null || originalRequest is not null
                || request.Method != HttpMethod.Post || request.RequestUri is null
                || request.RequestUri.AbsolutePath != "/session"
                || request.RequestUri.GetLeftPart(UriPartial.Authority) != ReceiptOptions.BaseUri.GetLeftPart(UriPartial.Authority)
                || request.RequestUri.Query != "?directory=" + Uri.EscapeDataString(Path.TrimEndingDirectorySeparator(Path.GetFullPath(ReceiptOptions.ApprovedProjectRoot))))
                throw new ExternalHarnessException("ReceiptExchangeRequired");
            registration.Arm(); transport = client; originalRequest = request; originalUri = request.RequestUri;
        }
        public void CaptureOriginalResponse(HttpClient client, HttpRequestMessage request, HttpResponseMessage response)
        {
            DemandEntry();
            if (!ReceiptEnabled || current.Value?.Kind != ProviderEffect.CreateSession) return;
            if (captured || !ReferenceEquals(transport, client) || !ReferenceEquals(originalRequest, request)
                || request.RequestUri != originalUri || !ReferenceEquals(response.RequestMessage, request)
                || !ReceiptTransport.IsOriginalResponse(client, request, response)
                || !response.IsSuccessStatusCode) { registration?.Burn(); throw new ExternalHarnessException("ReceiptResponseMismatch"); }
            captured = true;
        }
        public async Task FinalizeOriginalCreate(ExternalExecutionHandle handle, bool verified, string diagnostic)
        {
            if (!ReceiptEnabled) return;
            DemandEntry();
            if (!captured || registration is null || current.Value?.Kind != ProviderEffect.CreateSession
                || handle.ExecutionId != inputs.PreparedHandle.ExecutionId || handle.ExternalRequestId != inputs.PreparedHandle.ExternalRequestId
                || handle.Provider != inputs.ProviderId || handle.ExternalSessionId is null)
                throw new ExternalHarnessException("ReceiptResponseRequired");
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { finalized = await registration.Complete(handle.ExternalSessionId, verified, diagnostic, clock.GetUtcNow().UtcTicks, budget.Token); }
            catch (Exception e) when (e is InvalidOperationException or System.Data.Common.DbException or OperationCanceledException)
            { throw new ExternalHarnessException("ReceiptPersistenceUnavailable", true); }
        }
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
            if (assessment.Decision.Outcome != (approval is not null && !inspecting ? PolicyOutcome.RequireApproval : PolicyOutcome.Allow) || assessment.Operation is null)
                throw new ExternalHarnessException(assessment.Decision.Reason.ToString());
            var op = assessment.Operation with { ProviderSessionId = record.Handle.ExternalSessionId };
            var descriptor = inspecting ? inputs.InspectionCapability : inputs.SubmissionCapability;
            var decision = assessment.Decision with { BindingDigest = OperationBinding.Digest(op, descriptor) };
            try
            {
                if (ReceiptEnabled && effect == ProviderEffect.CreateSession && next is not null)
                {
                    registration = await ((ISessionReceiptCoordinator)enforcement).CommitCreateIntentAsync(op, decision, descriptor,
                        record, next, controlVersion, Math.Min(intent.Timeout.Ticks, TimeSpan.FromSeconds(ReceiptOptions.RequestTimeoutSeconds).Ticks),
                        ReceiptOptions.ResponseLimitBytes, caller, owner, approval, token);
                    return;
                }
                if (approval is not null)
                {
                    if (enforcement is not IApprovalResumeCoordinator coordinator) throw new ExternalHarnessException("ResumeNotSupported");
                    await coordinator.VerifyApprovedEffectAsync(approval, op, decision, record, next, controlVersion, effect, caller, owner, token);
                }
                else await enforcement.VerifyEffectAsync(op, decision, record, next, controlVersion, effect, caller, owner, token);
            }
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
