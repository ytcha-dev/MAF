using System.Collections.Concurrent;
using MAF.Core;
using Microsoft.Agents.AI.Workflows;

namespace MAF.Workflows.External;

public sealed record ExternalWorkflowRun(ExternalTaskResult Result, IReadOnlyList<string> WorkflowEvents);

public sealed class ExternalWorkflow
{
    readonly IExternalHarnessClient client;
    readonly TimeSpan? pollInterval;
    readonly IRuntimeAccess access;
    public ExternalWorkflow(IExternalHarnessClient client, TimeSpan? pollInterval = null, int maxConcurrency = 2)
        : this(UnavailableRuntimeAccess.Instance, client, pollInterval, maxConcurrency) { }
    internal ExternalWorkflow(IRuntimeAccess access, IExternalHarnessClient client, TimeSpan? pollInterval = null, int maxConcurrency = 2)
    { this.access = access; this.client = client; this.pollInterval = pollInterval; slots = new(maxConcurrency, maxConcurrency); }
    private sealed record Entry(ExternalTaskRequest Request, Lazy<Task<ExternalWorkflowRun>> Run);
    private readonly ConcurrentDictionary<string, Entry> runs = new(StringComparer.Ordinal);
    private readonly object identityLock = new();
    private readonly SemaphoreSlim slots;

    public Task<ExternalWorkflowRun> RunAsync(ExternalTaskRequest request, CancellationToken cancellationToken = default)
    {
        access.DemandEntry();
        if (string.IsNullOrWhiteSpace(request.TaskId) || request.TaskId.Length > 100
            || string.IsNullOrWhiteSpace(request.TaskText) || request.TaskText.Length > 4000
            || request.Timeout < TimeSpan.FromMilliseconds(10) || request.Timeout > TimeSpan.FromMinutes(10)
            || request.Policy != ExternalExecutionPolicy.ReadOnly)
        {
            throw new ArgumentException("Invalid bounded read-only external request.");
        }
        Entry entry;
        lock (identityLock)
        {
            if (runs.Count >= 100 && !runs.ContainsKey(request.TaskId))
            {
                throw new InvalidOperationException("In-memory run capacity reached; restart only after reconciling active external sessions.");
            }
            entry = runs.GetOrAdd(request.TaskId, _ => new(request,
                new(() => ExecuteAsync(request, cancellationToken), LazyThreadSafetyMode.ExecutionAndPublication)));
        }
        if (entry.Request != request)
        {
            throw new ArgumentException("TaskId is already bound to a different request.");
        }
        return entry.Run.Value;
    }

    private async Task<ExternalWorkflowRun> ExecuteAsync(ExternalTaskRequest request, CancellationToken cancellationToken)
    {
        var workflowRunId = Guid.NewGuid().ToString("N");
        try
        {
            await slots.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new(new(request.TaskId, workflowRunId, ExternalExecutionStatus.Cancelled,
                new(Guid.NewGuid().ToString("N"), client.Provider), "Cancelled before external execution started.",
                [], [new("CancelledBeforeLaunch", DateTimeOffset.UtcNow)], ErrorCode: "CancelledBeforeLaunch"), []);
        }
        try
        {
            var executor = new OpenCodeExecutor(access, client, workflowRunId, pollInterval ?? TimeSpan.FromSeconds(1), cancellationToken);
            var workflow = new WorkflowBuilder(executor).WithOutputFrom(executor).Build();
            // Cancellation reaches the provider through the executor; keep the engine alive to emit the typed abort result.
            await using var run = await InProcessExecution.RunAsync(workflow, request, workflowRunId, CancellationToken.None);
            var events = run.NewEvents.ToArray();
            var result = events.OfType<WorkflowOutputEvent>().Select(output => output.Data).OfType<ExternalTaskResult>().Single();
            if (run.SessionId != result.WorkflowRunId)
            {
                throw new InvalidOperationException("Official workflow identity did not match the result.");
            }
            return new(result, events.Select(item => item.GetType().Name).ToArray());
        }
        finally
        {
            slots.Release();
        }
    }
}
