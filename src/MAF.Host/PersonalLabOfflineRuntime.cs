using System.Diagnostics;
using MAF.Core;
using MAF.Core.Policy;
using MAF.Core.Tools;
using MAF.Workflows.External;
using MAF.Workflows.External.OpenCode;
using Microsoft.Agents.AI.Workflows;

namespace MAF.Host;

// Independent cooperative Lab ownership. No protected authority, store, caller or receipt coordinator.
// Only reviewed friend-test offline transport construction exists. Not a same-principal sandbox.
// Counts describe local task completion only, never remote cancellation acknowledgement.
internal sealed record LabRuntimeCompletion(int FaultedOperations, int CancelledOperations);

internal sealed class PersonalLabOfflineRuntime : IOwnedHandoffDelegation, IDisposable
{
    readonly PersonalLabOfflineOperation operation;
    readonly HttpClient http;
    readonly OpenCodeClient client;
    readonly Access access;
    readonly ExternalTaskRequest request;
    readonly object sync = new();
    Task<ExternalTaskResult>? child;
    Task<HandoffRun>? journey;
    readonly CancellationTokenSource shutdown = new();
    readonly List<CancellationTokenSource> links = [];
    readonly HashSet<Task> ownedWork = [];
    Task? cancellation;
    bool disposed, stopping;
    PersonalLabOfflineRuntime(PersonalLabOfflineOperation operation, HttpMessageHandler transport, Uri origin)
    {
        this.operation = operation;
        var task = operation.Preparation.Task;
        request = new(task.TaskId, task.Workspace, task.Objective, task.StageTimeout);
        access = new(operation, request);
        var boundary = LabOpenCodeBoundary.ForTests(operation, origin);
        boundary.DemandIdentity(task, operation.OriginalHandle);
        boundary.InnerHandler = transport;
        http = new(boundary) { Timeout = task.StageTimeout };
        try { client = new(access, http, new(origin, task.Workspace, AllowLoopbackHttp: origin.Scheme == "http")); }
        catch { http.Dispose(); throw; }
    }
    internal static PersonalLabOfflineRuntime ForTests(PersonalLabOfflineOperation operation, HttpMessageHandler transport,
        Uri origin, LabEffectTarget target = LabEffectTarget.ControlledOfflineFixture)
    {
        if (target != LabEffectTarget.ControlledOfflineFixture) throw new ExternalHarnessException("LabNetworkNotAuthorized");
        operation.DemandActiveSession();
        return new(operation, transport, origin); // Owns transport after successful construction.
    }
    internal Task<HandoffRun> RunAsync(BoundNativeRecipient pca, BoundNativeRecipient reviewer, CancellationToken token = default)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (journey is not null) return journey;
            if (stopping) throw new InvalidOperationException("LabRuntimeStopping");
            var lifetime = Link(token);
            var adapters = BoundNativeRecipient.BindAdapters(operation, pca, reviewer, Track);
            var source = new VerifiedSourceMode(new BoundedSourceReader(operation.Preparation.Task.Workspace, TimeProvider.System));
            var flow = new ControlledHandoffWorkflow(adapters.Pca, intent =>
            {
                access.DemandEntry();
                return intent == request ? PolicyOutcome.Allow : PolicyOutcome.Deny; // Exact offline intent filter, NEVER production authorization.
            }, this, adapters.Reviewer.ReviewAsync, operation.OriginalHandle, TimeProvider.System, source, Track);
            return journey = flow.RunAsync(operation.Preparation.Task, lifetime);
        }
    }
    public Task<ExternalTaskResult> DelegateAsync(ExternalTaskRequest original, string workflowRunId, CancellationToken token)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (original != request || workflowRunId != operation.Preparation.Task.WorkflowRunId)
                throw new ExternalHarnessException("LabOriginalIdentityMismatch");
            if (child is not null) return child;
            if (stopping) throw new ExternalHarnessException("LabRuntimeStopping");
            return child = Execute(Link(token));
        }
    }
    async Task<ExternalTaskResult> Execute(CancellationToken token)
    {
        access.DemandEntry();
        var task = operation.Preparation.Task;
        var executor = new OpenCodeExecutor(access, client, task.WorkflowRunId, TimeSpan.FromMilliseconds(10),
            token, operation.OriginalHandle, abortOnTimeout: false, observeOnce: true);
        var graph = new WorkflowBuilder(executor).WithOutputFrom(executor).Build();
        await using var run = await InProcessExecution.RunAsync(graph, request, task.WorkflowRunId, CancellationToken.None);
        var result = run.NewEvents.OfType<WorkflowOutputEvent>().Select(e => e.Data).OfType<ExternalTaskResult>().Single();
        if (run.SessionId != task.WorkflowRunId || result.WorkflowRunId != task.WorkflowRunId)
            throw new ExternalHarnessException("LabOriginalIdentityMismatch");
        return result;
    }
    // The caller still owns native recipients and journal; keep them alive until this runtime is quiescent.
    CancellationToken Link(CancellationToken token)
    {
        var link = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
        links.Add(link); return link.Token;
    }
    void Track(Task work) { lock (sync) ownedWork.Add(work); }
    Task[] Snapshot()
        => ownedWork.Concat(new Task?[] { journey, child, cancellation }.OfType<Task>()).Distinct().ToArray();
    internal bool CancellationRequested { get { lock (sync) return stopping; } }
    internal bool CancellationCallbacksCompleted { get { lock (sync) return cancellation?.IsCompletedSuccessfully == true; } }
    internal bool IsQuiescent { get { lock (sync) return Snapshot().All(t => t.IsCompleted); } }
    internal Task RequestCancellationAsync()
    {
        lock (sync)
        {
            if (cancellation is not null) return cancellation;
            if (disposed) return Task.CompletedTask;
            stopping = true; // Seal new starts before delivering cancellation outside user callback code.
            return cancellation = shutdown.CancelAsync(); // Callback failure remains observable; no provider Abort.
        }
    }
    internal async Task<LabRuntimeCompletion> WaitForCompletionAsync(TimeSpan timeout, CancellationToken token = default)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(timeout));
        var watch = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task[] tasks;
            lock (sync)
            {
                tasks = Snapshot();
                if (tasks.All(t => t.IsCompleted))
                {
                    // Observe all errors, including abandoned bounded waits; outcomes remain on the original run tasks.
                    foreach (var task in tasks) if (task.IsFaulted) _ = task.Exception;
                    return new(tasks.Count(t => t.IsFaulted), tasks.Count(t => t.IsCanceled));
                }
            }
            var remaining = timeout - watch.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("LabRuntimeDrainTimedOut");
            await Task.WhenAll(tasks.Select(Observe)).WaitAsync(remaining, token);
            // Parent completion may have registered more work; recheck under the same ownership lock.
        }
        static async Task Observe(Task work) { try { await work; } catch { /* Reported after quiescence, not promoted to success. */ } }
    }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            if (Snapshot().Any(t => !t.IsCompleted)) throw new InvalidOperationException("AwaitLabRuntimeBeforeDisposal");
            disposed = true; access.Disposed = true;
            try { http.Dispose(); }
            finally
            {
                foreach (var link in links) link.Dispose();
                shutdown.Dispose();
            }
        }
    }
    sealed class Access(PersonalLabOfflineOperation operation, ExternalTaskRequest original) : IRuntimeAccess
    {
        public bool Disposed;
        public bool SuppressImplicitAbort => true;
        public void DemandEntry() { ObjectDisposedException.ThrowIf(Disposed, this); operation.DemandActiveSession(); }
        public IDisposable Enter(ProviderEffect effect, ExternalTaskRequest? request = null, string? run = null, ExternalExecutionHandle? handle = null)
        {
            DemandEntry();
            if (effect is not (ProviderEffect.Validate or ProviderEffect.CreateSession or ProviderEffect.SubmitPrompt or ProviderEffect.Observe)
                || request is not null && request != original || run is not null && run != operation.Preparation.Task.WorkflowRunId)
                throw new ExternalHarnessException("LabEffectBindingMismatch");
            var expected = operation.OriginalHandle;
            if (handle is not null && (handle.ExecutionId != expected.ExecutionId || handle.Provider != expected.Provider
                || handle.ExternalRequestId != expected.ExternalRequestId
                || handle.ExternalSessionId != (effect == ProviderEffect.CreateSession ? null : operation.DemandActiveSession())))
                throw new ExternalHarnessException("LabOriginalIdentityMismatch");
            return new Scope();
        }
        public Task BeforeWireAsync(HttpMethod method, string path, string task, CancellationToken token)
        {
            DemandEntry(); token.ThrowIfCancellationRequested();
            if (task != original.TaskId) throw new ExternalHarnessException("LabOriginalIdentityMismatch");
            // LabOpenCodeBoundary performs route/model/receipt checks and flushed Reserve BEFORE inner transport.
            return Task.CompletedTask;
        }
        sealed class Scope : IDisposable { public void Dispose() { } }
    }
}
