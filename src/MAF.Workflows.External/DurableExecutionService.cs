using MAF.Core;
using Microsoft.Agents.AI.Workflows;

namespace MAF.Workflows.External;

public sealed class DurableExecutionService : IDisposable
{
    readonly IExecutionStore store;
    readonly IResumableExternalHarnessClient client;
    readonly TimeSpan? pollInterval;
    readonly IRuntimeAccess access;
    public DurableExecutionService(IExecutionStore store, IResumableExternalHarnessClient client, TimeSpan? pollInterval = null)
        : this(UnavailableRuntimeAccess.Instance, store, client, pollInterval) { }
    internal DurableExecutionService(IRuntimeAccess access, IExecutionStore store, IResumableExternalHarnessClient client, TimeSpan? pollInterval = null)
    { this.access = access; this.store = store; this.client = client; this.pollInterval = pollInterval; }
    private readonly SemaphoreSlim slots = new(2, 2);
    public void Dispose() => slots.Dispose();
    private static void Validate(ExternalTaskRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TaskId) || request.TaskId.Length > 100
            || string.IsNullOrWhiteSpace(request.TaskText) || request.TaskText.Length > 4000
            || request.Timeout < TimeSpan.FromMilliseconds(10) || request.Timeout > TimeSpan.FromMinutes(10)
            || request.Policy != ExternalExecutionPolicy.ReadOnly)
            throw new ArgumentException("Invalid bounded read-only durable request.");
    }

    public async Task<ExternalWorkflowRun> RunAsync(ExternalTaskRequest request, CancellationToken cancellationToken = default)
    {
        access.DemandEntry();
        Validate(request);
        await using var ownership = await store.TryAcquireAsync(request.TaskId, cancellationToken)
            ?? throw new InvalidOperationException("TaskId already has an active local owner.");
        var record = await store.GetAsync(request.TaskId, cancellationToken);
        if (record is not null)
        {
            if (record.RequestFingerprint != DurableExecution.Fingerprint(request))
                throw new ArgumentException("TaskId is already bound to a different request.");
            return await RecoverOwnedAsync(record, request.ProjectRoot, request.Timeout, ownership, false, cancellationToken);
        }
        var now = DateTimeOffset.UtcNow;
        record = new(request.TaskId, DurableExecution.Fingerprint(request), client.Scope,
            DurableExecution.NormalizeRoot(request.ProjectRoot), Guid.NewGuid().ToString("N"), client.PrepareExecution(),
            DurableExecutionState.Created, SubmissionPhase.None, now, now);
        await store.CreateAsync(record, ownership, cancellationToken);
        var durableClient = new PersistingClient(store, client, record, ownership);
        return await ExecuteAsync(request, durableClient, false, false, cancellationToken);
    }

    public async Task<ExternalWorkflowRun> RecoverAsync(string taskId, string projectRoot, TimeSpan timeout,
        bool observeOnce = false, CancellationToken cancellationToken = default)
    {
        access.DemandEntry();
        Validate(new(taskId, projectRoot, "Recovery inspection", timeout));
        await using var ownership = await store.TryAcquireAsync(taskId, cancellationToken)
            ?? throw new InvalidOperationException("TaskId already has an active local owner.");
        var record = await store.GetAsync(taskId, cancellationToken)
            ?? throw new ArgumentException("Unknown durable TaskId.");
        return await RecoverOwnedAsync(record, projectRoot, timeout, ownership, observeOnce, cancellationToken);
    }

    public Task<IReadOnlyList<DurableExecution>> ListRecoverableAsync(CancellationToken cancellationToken = default)
    { access.DemandEntry(); return store.ListRecoverableAsync(cancellationToken); }

    internal Task<ExternalWorkflowRun> LaunchOwnedAsync(ExternalTaskRequest request, DurableExecution record,
        IExecutionOwnership owner, CancellationToken token)
    { access.DemandEntry(); return ExecuteAsync(request, new PersistingClient(store, client, record, owner), false, false, token); }
    internal Task<ExternalWorkflowRun> InspectOwnedAsync(DurableExecution record, string root, TimeSpan timeout,
        IExecutionOwnership owner, bool observeOnce, CancellationToken token)
    { access.DemandEntry(); return RecoverOwnedAsync(record, root, timeout, owner, observeOnce, token); }

    private async Task<ExternalWorkflowRun> RecoverOwnedAsync(DurableExecution record, string root, TimeSpan timeout,
        IExecutionOwnership ownership, bool observeOnce, CancellationToken cancellationToken)
    {
        if (record.ProviderScope != client.Scope || record.ProjectRoot != DurableExecution.NormalizeRoot(root))
            throw new ArgumentException("Recovery provider scope or workspace differs from the stored execution.");
        var durableClient = new PersistingClient(store, client, record, ownership);
        if (record.IsTerminal && record.SubmissionPhase < SubmissionPhase.PromptIntended && record.State == DurableExecutionState.Failed)
        {
            var failure = Metadata(record, ExternalExecutionStatus.Failed, "StoredLocalLaunchFailure");
            return failure with { Result = failure.Result with { Summary = "Stored local launch failure; no external prompt was submitted." } };
        }
        if (record.Handle.ExternalSessionId is null)
        {
            await durableClient.SaveAsync(DurableExecutionState.Unknown, code: "MissingSubmissionEvidence");
            return Metadata(durableClient.Record, ExternalExecutionStatus.Unknown, "MissingSubmissionEvidence");
        }
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(timeout);
            await client.ReattachAsync(record.TaskId, root, record.Handle, budget.Token);
        }
        catch (Exception error) when (error is ExternalHarnessException or OperationCanceledException)
        {
            // Unreachable, missing or policy-rejected provider evidence is not confirmed task failure.
            await durableClient.SaveAsync(DurableExecutionState.Unknown, code: "ReconciliationUnavailable", reconciled: true);
            return Metadata(durableClient.Record, ExternalExecutionStatus.Unknown, "ReconciliationUnavailable");
        }
        if (record.SubmissionPhase < SubmissionPhase.PromptIntended)
        {
            // Inspect the known session, but never infer permission to send a prompt from its existence.
            await durableClient.SaveAsync(DurableExecutionState.Unknown, code: "MissingSubmissionEvidence", reconciled: true);
            return Metadata(durableClient.Record, ExternalExecutionStatus.Unknown, "MissingSubmissionEvidence");
        }
        return await ExecuteAsync(new(record.TaskId, root, "Recovery inspection", timeout), durableClient,
            true, observeOnce, cancellationToken);
    }

    private async Task<ExternalWorkflowRun> ExecuteAsync(ExternalTaskRequest request, PersistingClient durableClient,
        bool recovery, bool observeOnce, CancellationToken cancellationToken)
    {
        await slots.WaitAsync(cancellationToken);
        try
        {
            var record = durableClient.Record;
            var executor = new OpenCodeExecutor(access, durableClient, record.WorkflowRunId, pollInterval ?? TimeSpan.FromSeconds(1),
                cancellationToken, record.Handle, recovery, abortOnTimeout: false, observeOnce: observeOnce);
            var workflow = new WorkflowBuilder(executor).WithOutputFrom(executor).Build();
            await using var run = await InProcessExecution.RunAsync(workflow, request, record.WorkflowRunId, CancellationToken.None);
            var events = run.NewEvents.ToArray();
            var result = events.OfType<WorkflowOutputEvent>().Select(e => e.Data).OfType<ExternalTaskResult>().Single();
            if (run.SessionId != result.WorkflowRunId) throw new InvalidOperationException("Workflow identity mismatch.");
            // Provider error text and arbitrary exception messages never enter storage.
            var state = ToState(result.Status);
            try
            {
                await durableClient.SaveAsync(state, code: result.ErrorCode is null ? null : "ExecutionObservationIncomplete", reconciled: recovery);
            }
            catch (ExternalHarnessException)
            {
                result = result with { Status = ExternalExecutionStatus.Unknown, Summary = "Durable outcome could not be recorded; reconcile the original identity.",
                    ErrorCode = "PersistenceUnavailable", Limitations = ["Remote work may still be active. No resubmission is permitted."] };
            }
            result = result with { AbortRequested = result.AbortRequested || durableClient.Record.AbortRequested };
            return new(result, events.Select(e => e.GetType().Name).ToArray());
        }
        finally { slots.Release(); }
    }

    private static ExternalWorkflowRun Metadata(DurableExecution record, ExternalExecutionStatus status, string code)
        => new(new(record.TaskId, record.WorkflowRunId, status, record.Handle,
            "Remote result is not established by this recovery attempt.",
            ["Local storage contains identity and state only; no prompt or result body is stored."],
            [new("RecoveryInspection", DateTimeOffset.UtcNow)], record.AbortRequested, code), []);

    private static DurableExecutionState ToState(ExternalExecutionStatus status) => status switch
    {
        ExternalExecutionStatus.Pending or ExternalExecutionStatus.Running => DurableExecutionState.Running,
        ExternalExecutionStatus.WaitingForApproval => DurableExecutionState.WaitingForApproval,
        ExternalExecutionStatus.Completed => DurableExecutionState.Completed,
        ExternalExecutionStatus.Partial => DurableExecutionState.Partial,
        ExternalExecutionStatus.Failed => DurableExecutionState.Failed,
        ExternalExecutionStatus.Cancelled => DurableExecutionState.Cancelled,
        _ => DurableExecutionState.Unknown
    };

    private sealed class PersistingClient(IExecutionStore store, IResumableExternalHarnessClient inner,
        DurableExecution initial, IExecutionOwnership ownership) : IExternalHarnessClient
    {
        public DurableExecution Record { get; private set; } = initial;
        public string Provider => inner.Provider;
        public int GetRequestCount(string taskId) => inner.GetRequestCount(taskId);
        public Task ValidateAsync(ExternalTaskRequest request, CancellationToken cancellationToken) => inner.ValidateAsync(request, cancellationToken);

        public async Task SaveAsync(DurableExecutionState state, SubmissionPhase? phase = null,
            ExternalExecutionHandle? handle = null, string? code = null, bool reconciled = false, bool abort = false)
        {
            if (Record.IsTerminal) return; // Confirmed terminal evidence cannot be overwritten by a later read failure.
            var now = DateTimeOffset.UtcNow;
            if (now < Record.UpdatedAt) now = Record.UpdatedAt;
            var terminal = state is DurableExecutionState.Completed or DurableExecutionState.Partial or DurableExecutionState.Failed or DurableExecutionState.Cancelled;
            var next = Record with
            {
                State = state, SubmissionPhase = phase ?? Record.SubmissionPhase, Handle = handle ?? Record.Handle,
                ErrorCode = code, AbortRequested = Record.AbortRequested || abort, Version = Record.Version + 1,
                UpdatedAt = now < Record.UpdatedAt ? Record.UpdatedAt : now,
                CompletedAt = terminal ? now : null, ReconciledAt = reconciled ? now : Record.ReconciledAt
            };
            try { await store.UpdateAsync(Record, next, ownership, CancellationToken.None); Record = next; }
            catch (Exception error) when (error is IOException or InvalidOperationException or System.Data.Common.DbException)
            { throw new ExternalHarnessException("PersistenceUnavailable", true, Record.Handle); }
        }

        public async Task<ExternalExecutionHandle> CreateAsync(ExternalTaskRequest request, string workflowRunId,
            ExternalExecutionHandle handle, CancellationToken cancellationToken)
        {
            await SaveAsync(DurableExecutionState.SessionCreating, SubmissionPhase.SessionCreationIntended);
            ExternalExecutionHandle created;
            try { created = await inner.CreateAsync(request, workflowRunId, handle, cancellationToken); }
            catch (ExternalHarnessException error) when (error.Handle is not null)
            {
                if (store is IOriginalCreateStore bridge && bridge.TakeOriginalCreate(error.Handle) is { } committed)
                { Record = committed; throw; }
                await SaveAsync(DurableExecutionState.Unknown, SubmissionPhase.SessionKnown, error.Handle, "SessionCreationUnconfirmed");
                throw;
            }
            if (store is IOriginalCreateStore original && original.TakeOriginalCreate(created) is { } finalized) Record = finalized;
            else await SaveAsync(DurableExecutionState.ReadyToSubmit, SubmissionPhase.SessionKnown, created);
            return created;
        }

        public async Task SubmitAsync(ExternalTaskRequest request, ExternalExecutionHandle handle, CancellationToken cancellationToken)
        {
            await SaveAsync(DurableExecutionState.SubmissionIntended, SubmissionPhase.PromptIntended);
            try { await inner.SubmitAsync(request, handle, cancellationToken); }
            catch (ExternalHarnessException error) when (error.Ambiguous)
            {
                await SaveAsync(DurableExecutionState.SubmissionUncertain, code: "SubmissionAcknowledgmentLost");
                throw;
            }
            await SaveAsync(DurableExecutionState.Running, SubmissionPhase.Acknowledged);
        }

        public async Task<ExternalObservation> ObserveAsync(ExternalExecutionHandle handle, CancellationToken cancellationToken)
        {
            var observed = await inner.ObserveAsync(handle, cancellationToken);
            var state = ToState(observed.Status);
            if (Record.IsTerminal && state != Record.State)
                return new(ExternalExecutionStatus.Unknown, "Provider evidence differs from the stored terminal outcome.", "TerminalEvidenceChanged");
            if (state != Record.State || Record.ReconciledAt is null)
                await SaveAsync(state, code: observed.ErrorCode is null ? null : "ProviderReportedError", reconciled: true);
            return observed;
        }

        public async Task<bool> AbortAsync(ExternalExecutionHandle handle, CancellationToken cancellationToken)
        {
            await SaveAsync(Record.State, abort: true);
            return await inner.AbortAsync(handle, cancellationToken);
        }
    }
}
