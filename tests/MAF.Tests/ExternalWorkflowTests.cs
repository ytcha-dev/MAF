using System.Collections.Concurrent;
using System.Text.Json;
using MAF.Core;
using MAF.Workflows.External;

namespace MAF.Tests;

public sealed class ExternalWorkflowTests
{
    private static ExternalTaskRequest Request(string id = "task-1", int timeoutMilliseconds = 2000)
        => new(id, "fixture", "Summarize the fixture without editing.", TimeSpan.FromMilliseconds(timeoutMilliseconds));

    [Fact]
    public async Task OfficialWorkflowEmitsTypedResultAndSerializes()
    {
        var fake = new FakeHarness();
        var run = await new ExternalWorkflow(fake, TimeSpan.FromMilliseconds(5)).RunAsync(Request());
        Assert.Equal(ExternalExecutionStatus.Completed, run.Result.Status);
        Assert.Contains("ExecutorInvokedEvent", run.WorkflowEvents);
        Assert.Contains("WorkflowOutputEvent", run.WorkflowEvents);
        Assert.NotEqual(run.Result.WorkflowRunId, run.Result.Handle.ExternalSessionId);
        var result = JsonSerializer.Deserialize<ExternalTaskResult>(run.Result.ToJson(), ExternalTaskResult.JsonOptions)!;
        Assert.Equal(run.Result.Handle, result.Handle);
        Assert.Contains(result.Trace, item => item.Kind == "Running");
    }

    [Fact]
    public async Task ConcurrentDuplicateRequestsSubmitOnceAndKeepIdentity()
    {
        var fake = new FakeHarness();
        var workflow = new ExternalWorkflow(fake, TimeSpan.FromMilliseconds(5));
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => workflow.RunAsync(Request())));
        Assert.Equal(1, fake.Submissions);
        Assert.Single(results.Select(result => result.Result.Handle.ExternalSessionId).Distinct());
        await Assert.ThrowsAsync<ArgumentException>(() => workflow.RunAsync(Request() with { TaskText = "different" }));
        var other = await workflow.RunAsync(Request("task-2"));
        Assert.NotEqual(results[0].Result.Handle.ExternalSessionId, other.Result.Handle.ExternalSessionId);
    }

    [Fact]
    public async Task DistinctConcurrentTasksRespectLimitAndKeepSeparateSessions()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var twoStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var observed = new ConcurrentBag<int>();
        var fake = new FakeHarness
        {
            BeforeSubmit = async cancellationToken =>
            {
                var count = Interlocked.Increment(ref active);
                observed.Add(count);
                if (count == 2)
                {
                    twoStarted.TrySetResult();
                }
                await release.Task.WaitAsync(cancellationToken);
                Interlocked.Decrement(ref active);
            }
        };
        var workflow = new ExternalWorkflow(fake, TimeSpan.FromMilliseconds(5), maxConcurrency: 2);
        var tasks = Enumerable.Range(0, 6).Select(index => workflow.RunAsync(Request("parallel-" + index, 5000))).ToArray();
        try
        {
            await twoStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, fake.Submissions);
        }
        finally
        {
            release.TrySetResult();
        }
        var runs = await Task.WhenAll(tasks);
        Assert.Equal(6, fake.Submissions);
        Assert.All(observed, count => Assert.InRange(count, 1, 2));
        Assert.All(runs, run => Assert.Equal(ExternalExecutionStatus.Completed, run.Result.Status));
        Assert.Equal(6, runs.Select(run => run.Result.Handle.ExternalSessionId).Distinct().Count());
        Assert.Equal(6, runs.Select(run => run.Result.WorkflowRunId).Distinct().Count());
    }

    [Theory]
    [InlineData(ExternalExecutionStatus.WaitingForApproval)]
    [InlineData(ExternalExecutionStatus.Partial)]
    [InlineData(ExternalExecutionStatus.Failed)]
    [InlineData(ExternalExecutionStatus.Unknown)]
    public async Task ProviderStatesRemainExplicit(ExternalExecutionStatus terminal)
    {
        var fake = new FakeHarness { Terminal = terminal };
        var result = (await new ExternalWorkflow(fake, TimeSpan.FromMilliseconds(5)).RunAsync(Request())).Result;
        Assert.Equal(terminal, result.Status);
        if (terminal == ExternalExecutionStatus.WaitingForApproval)
        {
            Assert.Contains(result.Limitations, item => item.Contains("Nothing was approved"));
        }
    }

    [Fact]
    public async Task AmbiguousSubmissionAndReadDisconnectReconcileWithoutResubmit()
    {
        var fake = new FakeHarness { AmbiguousSubmit = true, FailFirstRead = true };
        var result = (await new ExternalWorkflow(fake, TimeSpan.FromMilliseconds(5)).RunAsync(Request())).Result;
        Assert.Equal(ExternalExecutionStatus.Completed, result.Status);
        Assert.Equal(1, fake.Submissions);
        Assert.Contains(result.Trace, item => item.Kind == "ReconnectOriginalSession");
        Assert.Contains(result.Trace, item => item.Kind == "SubmissionUncertain");
    }

    [Fact]
    public async Task OfflineProviderDoesNotSubmit()
    {
        var fake = new FakeHarness { Offline = true };
        var result = (await new ExternalWorkflow(fake).RunAsync(Request())).Result;
        Assert.Equal(ExternalExecutionStatus.Failed, result.Status);
        Assert.Equal("Unreachable", result.ErrorCode);
        Assert.Equal(0, fake.Submissions);
    }

    [Theory]
    [InlineData(true, ExternalExecutionStatus.Cancelled)]
    [InlineData(false, ExternalExecutionStatus.Unknown)]
    public async Task TimeoutAbortRequiresTerminalEvidence(bool confirmed, ExternalExecutionStatus expected)
    {
        var fake = new FakeHarness { NeverComplete = true, ConfirmAbort = confirmed };
        var result = (await new ExternalWorkflow(fake, TimeSpan.FromMilliseconds(10)).RunAsync(Request(timeoutMilliseconds: 80))).Result;
        Assert.True(result.AbortRequested);
        Assert.Equal(expected, result.Status);
        Assert.Equal(1, fake.Aborts);
    }

    [Fact]
    public async Task CallerCancellationReachesProviderAndProducesTypedResult()
    {
        var fake = new FakeHarness { NeverComplete = true, ConfirmAbort = true };
        using var stop = new CancellationTokenSource();
        var run = new ExternalWorkflow(fake, TimeSpan.FromMilliseconds(5)).RunAsync(Request(), stop.Token);
        await fake.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        stop.Cancel();
        Assert.Equal(ExternalExecutionStatus.Cancelled, (await run).Result.Status);
    }

    [Fact]
    public async Task CancellationBeforeLaunchDoesNotCreateRemoteWork()
    {
        var fake = new FakeHarness();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var run = await new ExternalWorkflow(fake).RunAsync(Request(), stop.Token);
        Assert.Equal(ExternalExecutionStatus.Cancelled, run.Result.Status);
        Assert.False(run.Result.AbortRequested);
        Assert.Equal(0, fake.Submissions);
        Assert.Empty(run.WorkflowEvents);
    }
}

internal sealed class FakeHarness : IExternalHarnessClient
{
    private readonly ConcurrentDictionary<string, int> observations = new();
    private readonly ConcurrentDictionary<string, bool> aborted = new();
    public string Provider => "FakeOpenCode";
    public int GetRequestCount(string taskId) => 0;
    public int Submissions;
    public int Aborts;
    public ExternalExecutionStatus Terminal { get; init; } = ExternalExecutionStatus.Completed;
    public bool Offline { get; init; }
    public bool AmbiguousSubmit { get; init; }
    public bool FailFirstRead { get; init; }
    public bool NeverComplete { get; init; }
    public bool ConfirmAbort { get; init; }
    public Func<CancellationToken, Task>? BeforeSubmit { get; init; }
    public TaskCompletionSource Submitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task ValidateAsync(ExternalTaskRequest request, CancellationToken cancellationToken)
    {
        if (Offline)
        {
            throw new ExternalHarnessException("Unreachable");
        }
        return Task.CompletedTask;
    }

    public Task<ExternalExecutionHandle> CreateAsync(ExternalTaskRequest request, string workflowRunId,
        ExternalExecutionHandle handle, CancellationToken cancellationToken)
        => Task.FromResult(handle with { ExternalSessionId = "ses_" + Guid.NewGuid().ToString("N") });

    public async Task SubmitAsync(ExternalTaskRequest request, ExternalExecutionHandle handle, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Submissions);
        Submitted.TrySetResult();
        if (BeforeSubmit is not null)
        {
            await BeforeSubmit(cancellationToken);
        }
        if (AmbiguousSubmit)
        {
            throw new ExternalHarnessException("LostSubmitResponse", true);
        }
    }

    public Task<ExternalObservation> ObserveAsync(ExternalExecutionHandle handle, CancellationToken cancellationToken)
    {
        var count = observations.AddOrUpdate(handle.ExternalSessionId!, 1, (_, value) => value + 1);
        if (FailFirstRead && count == 1)
        {
            throw new ExternalHarnessException("ReadDisconnected", true);
        }
        if (aborted.ContainsKey(handle.ExternalSessionId!) && ConfirmAbort)
        {
            return Task.FromResult(new ExternalObservation(ExternalExecutionStatus.Cancelled));
        }
        return Task.FromResult(new ExternalObservation(NeverComplete || count < 2 ? ExternalExecutionStatus.Running : Terminal,
            "Fixture completion", OutputTruncated: Terminal == ExternalExecutionStatus.Partial));
    }

    public Task<bool> AbortAsync(ExternalExecutionHandle handle, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Aborts);
        aborted[handle.ExternalSessionId!] = true;
        return Task.FromResult(true);
    }
}
