using MAF.Core;
using MAF.Host;

namespace MAF.Tests;

public sealed class RuntimeLifecycleTests
{
    static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    // Deterministic handshake, not a sleep: the test owns release and observes cancellation separately.
    sealed class Gate(HttpMessageHandler inner, bool cooperative = true, bool failDispose = false, string? pathSuffix = null, bool failCancellation = false) : DelegatingHandler(inner)
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource CancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Disposals;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (pathSuffix is not null && !request.RequestUri!.AbsolutePath.EndsWith(pathSuffix, StringComparison.Ordinal))
                return await base.SendAsync(request, token);
            using var registration = token.Register(() =>
            {
                CancellationObserved.TrySetResult();
                if (failCancellation) throw new InvalidOperationException("ControlledCancellationCallbackFailure");
            });
            Entered.TrySetResult();
            try
            {
                if (cooperative) await Release.Task.WaitAsync(token);
                else await Release.Task; // Deliberately uncooperative offline task; ownership must outlive the outer wait.
                return await base.SendAsync(request, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult(); // WaitAsync can unwind/dispose the registration before its callback runs.
                throw;
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposals++;
            base.Dispose(disposing);
            if (disposing && failDispose) throw new IOException("ControlledCleanupFailure");
        }
    }
    static ExternalTaskRequest Request(IndependentLabRuntimeTests.Owned f)
        => new(f.Preparation.Task.TaskId, f.Workspace, f.Preparation.Task.Objective, f.Preparation.Task.StageTimeout);
    static async Task Clean(PersonalLabOfflineRuntime runtime, params Gate[] gates)
    {
        foreach (var gate in gates) gate.Release.TrySetResult();
        await runtime.RequestCancellationAsync();
        await runtime.WaitForCompletionAsync(Bound);
        runtime.Dispose();
    }
    [Fact]
    public async Task NormalJourneyDrainsBeforeCallerDisposesJournalAndRecipients()
    {
        using var f = new IndependentLabRuntimeTests.Owned("lifecycle-normal");
        using var pca = BoundNativeRecipient.ForTests(f.Operation, NativeModelRole.Pca,
            new PersonalLabFirstRunBoundaryTests.NativeHttp(f.Preparation, false, false));
        using var reviewer = BoundNativeRecipient.ForTests(f.Operation, NativeModelRole.Reviewer,
            new PersonalLabFirstRunBoundaryTests.NativeHttp(f.Preparation, true, false));
        using var runtime = PersonalLabOfflineRuntime.ForTests(f.Operation, f.Transport, LabOpenCodeBoundaryTests.Origin);
        var journey = runtime.RunAsync(pca, reviewer);
        Assert.Same(journey, runtime.RunAsync(pca, reviewer));
        var run = await journey;
        var completion = await runtime.WaitForCompletionAsync(Bound);
        Assert.Equal(0, completion.FaultedOperations); Assert.Equal(0, completion.CancelledOperations);
        Assert.True(runtime.IsQuiescent); Assert.False(runtime.CancellationRequested);
        Assert.Equal(ExternalExecutionStatus.Completed, run.Outcome.ExternalResult!.Status);
        f.Operation.RecordOutcome(run.Outcome);
        runtime.Dispose(); runtime.Dispose(); Assert.Equal(1, f.Transport.Disposals);
        f.Operation.Dispose();
        var journal = PersonalLabOfflineOperation.Inspect(f.Storage, f.Preparation.OperationId);
        Assert.Equal("ses_runtime", journal.VerifiedSession); Assert.False(journal.CanResume);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task ActiveChildRejectsDisposalAndCancellationCannotInventRemoteTerminal(bool cooperative)
    {
        using var f = new IndependentLabRuntimeTests.Owned("lifecycle-child-" + cooperative);
        var gate = new Gate(f.Transport, cooperative);
        var runtime = PersonalLabOfflineRuntime.ForTests(f.Operation, gate, LabOpenCodeBoundaryTests.Origin);
        try
        {
            var task = runtime.DelegateAsync(Request(f), f.Preparation.Task.WorkflowRunId, default);
            await gate.Entered.Task.WaitAsync(Bound);
            Assert.False(runtime.IsQuiescent);
            Assert.Equal("AwaitLabRuntimeBeforeDisposal", Assert.Throws<InvalidOperationException>(runtime.Dispose).Message);
            Assert.Equal(0, gate.Disposals);
            var cancel = runtime.RequestCancellationAsync();
            Assert.Same(cancel, runtime.RequestCancellationAsync());
            await cancel; await gate.CancellationObserved.Task.WaitAsync(Bound);
            Assert.True(runtime.CancellationRequested); Assert.True(runtime.CancellationCallbacksCompleted);
            Assert.Same(task, runtime.DelegateAsync(Request(f), f.Preparation.Task.WorkflowRunId, default));
            if (!cooperative)
            {
                await Assert.ThrowsAsync<TimeoutException>(() => runtime.WaitForCompletionAsync(TimeSpan.FromMilliseconds(50)));
                Assert.False(runtime.IsQuiescent); Assert.Equal(0, gate.Disposals);
                Assert.Throws<InvalidOperationException>(runtime.Dispose);
            }
            gate.Release.TrySetResult();
            await runtime.WaitForCompletionAsync(Bound);
            var result = await task;
            Assert.Equal(ExternalExecutionStatus.Unknown, result.Status); Assert.False(result.AbortRequested);
            Assert.DoesNotContain(f.Transport.Fake.Requests, r => r.StartsWith("POST"));
            runtime.Dispose(); runtime.Dispose(); Assert.Equal(1, gate.Disposals);
            await runtime.RequestCancellationAsync();
        }
        finally { await Clean(runtime, gate); }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NativeWorkRemainsOwnedAfterJourneyStopsWaiting(bool atReviewer)
    {
        using var f = new IndependentLabRuntimeTests.Owned("lifecycle-native-" + atReviewer);
        var held = new Gate(new PersonalLabFirstRunBoundaryTests.NativeHttp(f.Preparation, atReviewer, false), cooperative: false);
        using var pca = BoundNativeRecipient.ForTests(f.Operation, NativeModelRole.Pca, atReviewer
            ? new PersonalLabFirstRunBoundaryTests.NativeHttp(f.Preparation, false, false) : held);
        using var reviewer = BoundNativeRecipient.ForTests(f.Operation, NativeModelRole.Reviewer, atReviewer
            ? held : new PersonalLabFirstRunBoundaryTests.NativeHttp(f.Preparation, true, false));
        var runtime = PersonalLabOfflineRuntime.ForTests(f.Operation, f.Transport, LabOpenCodeBoundaryTests.Origin);
        try
        {
            var journey = runtime.RunAsync(pca, reviewer);
            await held.Entered.Task.WaitAsync(Bound);
            await runtime.RequestCancellationAsync();
            await held.CancellationObserved.Task.WaitAsync(Bound);
            var run = await journey.WaitAsync(Bound); // Bounded outer wait returned; actual native work is still held.
            Assert.NotEqual(HumanGateStatus.ReadyForHumanReview, run.Outcome.HumanGate);
            Assert.False(runtime.IsQuiescent);
            Assert.Throws<InvalidOperationException>(runtime.Dispose);
            using (var stopWaiting = new CancellationTokenSource())
            {
                stopWaiting.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.WaitForCompletionAsync(Bound, stopWaiting.Token));
            }
            Assert.False(runtime.IsQuiescent); Assert.Equal(0, held.Disposals);
            held.Release.TrySetResult();
            await runtime.WaitForCompletionAsync(Bound);
            Assert.True(runtime.IsQuiescent);
            f.Operation.RecordOutcome(run.Outcome);
            runtime.Dispose();
            f.Operation.Dispose();
            var journal = PersonalLabOfflineOperation.Inspect(f.Storage, f.Preparation.OperationId);
            Assert.Single(journal.Events, e => e.Kind == "ProviderObservation"); Assert.False(journal.CanResume);
            Assert.DoesNotContain(f.Transport.Fake.Requests, r => r.EndsWith("/abort") || r.EndsWith("/reply"));
            Assert.Equal(atReviewer ? 1 : 0, f.Transport.Fake.Requests.Count(r => r.EndsWith("/prompt_async")));
        }
        finally { await Clean(runtime, held); }
    }
    [Fact]
    public async Task CancellationAfterSubmissionPersistsUnknownWithoutRetryOrAbort()
    {
        using var f = new IndependentLabRuntimeTests.Owned("lifecycle-after-submission");
        var gate = new Gate(f.Transport, pathSuffix: "/message");
        using var pca = BoundNativeRecipient.ForTests(f.Operation, NativeModelRole.Pca,
            new PersonalLabFirstRunBoundaryTests.NativeHttp(f.Preparation, false, false));
        using var reviewer = BoundNativeRecipient.ForTests(f.Operation, NativeModelRole.Reviewer,
            new PersonalLabFirstRunBoundaryTests.NativeHttp(f.Preparation, true, false));
        var runtime = PersonalLabOfflineRuntime.ForTests(f.Operation, gate, LabOpenCodeBoundaryTests.Origin);
        try
        {
            var journey = runtime.RunAsync(pca, reviewer);
            await gate.Entered.Task.WaitAsync(Bound);
            await runtime.RequestCancellationAsync(); await runtime.WaitForCompletionAsync(Bound);
            var run = await journey;
            Assert.Equal(ExternalExecutionStatus.Unknown, run.Outcome.ExternalResult!.Status);
            Assert.False(run.Outcome.ExternalResult.AbortRequested);
            Assert.Equal("ses_runtime", run.Outcome.ExternalResult.Handle.ExternalSessionId);
            Assert.NotEqual(HumanGateStatus.ReadyForHumanReview, run.Outcome.HumanGate);
            f.Operation.RecordOutcome(run.Outcome); runtime.Dispose(); f.Operation.Dispose();
            var journal = PersonalLabOfflineOperation.Inspect(f.Storage, f.Preparation.OperationId);
            Assert.Equal("ses_runtime", journal.CandidateSession); Assert.Equal("ses_runtime", journal.VerifiedSession);
            Assert.Equal(ExternalExecutionStatus.Unknown, Assert.Single(journal.Events, e => e.Kind == "ProviderObservation").Status);
            Assert.False(journal.CanResume);
            Assert.Single(f.Transport.Fake.Requests, r => r == "POST /session");
            Assert.Single(f.Transport.Fake.Requests, r => r.EndsWith("/prompt_async"));
            Assert.DoesNotContain(f.Transport.Fake.Requests, r => r.EndsWith("/abort") || r.EndsWith("/reply"));
        }
        finally { await Clean(runtime, gate); }
    }
    [Fact]
    public async Task CallerTokenCancelsOwnedExecutionWithoutAnImplicitAbort()
    {
        using var f = new IndependentLabRuntimeTests.Owned("lifecycle-caller-token");
        var gate = new Gate(f.Transport);
        var runtime = PersonalLabOfflineRuntime.ForTests(f.Operation, gate, LabOpenCodeBoundaryTests.Origin);
        using var caller = new CancellationTokenSource();
        try
        {
            var child = runtime.DelegateAsync(Request(f), f.Preparation.Task.WorkflowRunId, caller.Token);
            await gate.Entered.Task.WaitAsync(Bound); caller.Cancel();
            await runtime.WaitForCompletionAsync(Bound);
            Assert.False(runtime.CancellationRequested); // Runtime stop and the caller's token are distinct signals.
            Assert.Equal(ExternalExecutionStatus.Unknown, (await child).Status);
            Assert.Empty(f.Transport.Fake.Requests);
        }
        finally { await Clean(runtime, gate); }
    }
    [Fact]
    public async Task StopBeforeStartCannotAllocateAnExecutionOrRetry()
    {
        using var f = new IndependentLabRuntimeTests.Owned("lifecycle-stop-before-start");
        using var runtime = PersonalLabOfflineRuntime.ForTests(f.Operation, f.Transport, LabOpenCodeBoundaryTests.Origin);
        await runtime.RequestCancellationAsync(); await runtime.RequestCancellationAsync();
        Assert.Equal("LabRuntimeStopping", (await Assert.ThrowsAsync<ExternalHarnessException>(() =>
            runtime.DelegateAsync(Request(f), f.Preparation.Task.WorkflowRunId, default))).Code);
        await runtime.WaitForCompletionAsync(Bound);
        Assert.True(runtime.IsQuiescent); Assert.Empty(f.Transport.Fake.Requests);
    }
    [Fact]
    public async Task CleanupFailureIsVisibleAndTransportDisposalIsAttemptedOnce()
    {
        using var f = new IndependentLabRuntimeTests.Owned("lifecycle-cleanup-failure");
        var gate = new Gate(f.Transport, failDispose: true);
        var runtime = PersonalLabOfflineRuntime.ForTests(f.Operation, gate, LabOpenCodeBoundaryTests.Origin);
        await runtime.RequestCancellationAsync(); await runtime.WaitForCompletionAsync(Bound);
        Assert.Equal("ControlledCleanupFailure", Assert.Throws<IOException>(runtime.Dispose).Message);
        runtime.Dispose(); Assert.Equal(1, gate.Disposals); Assert.Equal(1, f.Transport.Disposals);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => runtime.DelegateAsync(Request(f), f.Preparation.Task.WorkflowRunId, default));
    }
    [Fact]
    public async Task FailedChildIsObservedByDrainAndNeverBecomesSuccessfulCompletion()
    {
        using var f = new IndependentLabRuntimeTests.Owned("lifecycle-failed-child");
        using var runtime = PersonalLabOfflineRuntime.ForTests(f.Operation, f.Transport, LabOpenCodeBoundaryTests.Origin);
        f.Operation.Revoke();
        var child = runtime.DelegateAsync(Request(f), f.Preparation.Task.WorkflowRunId, default);
        await Assert.ThrowsAsync<ExternalHarnessException>(() => child);
        var drained = await runtime.WaitForCompletionAsync(Bound);
        Assert.Equal(1, drained.FaultedOperations); Assert.True(runtime.IsQuiescent);
        Assert.Empty(f.Transport.Fake.Requests);
    }
    [Fact]
    public async Task CancellationCallbackFailureIsObservableAndDoesNotAbandonOwnedWork()
    {
        using var f = new IndependentLabRuntimeTests.Owned("lifecycle-cancel-callback-failure");
        var gate = new Gate(f.Transport, cooperative: false, failCancellation: true);
        var runtime = PersonalLabOfflineRuntime.ForTests(f.Operation, gate, LabOpenCodeBoundaryTests.Origin);
        try
        {
            var child = runtime.DelegateAsync(Request(f), f.Preparation.Task.WorkflowRunId, default);
            await gate.Entered.Task.WaitAsync(Bound);
            await Assert.ThrowsAsync<AggregateException>(() => runtime.RequestCancellationAsync());
            Assert.True(runtime.CancellationRequested); Assert.False(runtime.CancellationCallbacksCompleted);
            Assert.False(runtime.IsQuiescent); Assert.Throws<InvalidOperationException>(runtime.Dispose);
            gate.Release.TrySetResult();
            var drained = await runtime.WaitForCompletionAsync(Bound);
            Assert.True(drained.FaultedOperations >= 1);
            Assert.Equal(ExternalExecutionStatus.Unknown, (await child).Status);
            // A callback exception can let the already-reserved validation GET return; no new effect may follow it.
            Assert.InRange(f.Transport.Fake.Requests.Count, 0, 1);
            Assert.All(f.Transport.Fake.Requests, r => Assert.Equal("GET /global/health", r));
        }
        finally
        {
            gate.Release.TrySetResult();
            try { await runtime.RequestCancellationAsync(); } catch (AggregateException) { }
            await runtime.WaitForCompletionAsync(Bound); runtime.Dispose();
        }
    }
    [Fact]
    public async Task CancellingOneRuntimeDoesNotStopAnUnrelatedOwnedSession()
    {
        using var first = new IndependentLabRuntimeTests.Owned("lifecycle-isolation-first");
        using var second = new IndependentLabRuntimeTests.Owned("lifecycle-isolation-second");
        var a = new Gate(first.Transport); var b = new Gate(second.Transport);
        var one = PersonalLabOfflineRuntime.ForTests(first.Operation, a, LabOpenCodeBoundaryTests.Origin);
        var two = PersonalLabOfflineRuntime.ForTests(second.Operation, b, LabOpenCodeBoundaryTests.Origin);
        try
        {
            var firstRun = one.DelegateAsync(Request(first), first.Preparation.Task.WorkflowRunId, default);
            var secondRun = two.DelegateAsync(Request(second), second.Preparation.Task.WorkflowRunId, default);
            await Task.WhenAll(a.Entered.Task, b.Entered.Task).WaitAsync(Bound);
            await one.RequestCancellationAsync(); await one.WaitForCompletionAsync(Bound);
            Assert.Equal(ExternalExecutionStatus.Unknown, (await firstRun).Status);
            Assert.False(b.CancellationObserved.Task.IsCompleted); Assert.False(secondRun.IsCompleted);
            Assert.False(two.CancellationRequested); Assert.Equal(0, b.Disposals);
            b.Release.TrySetResult();
            await two.WaitForCompletionAsync(Bound);
            Assert.Equal(ExternalExecutionStatus.Completed, (await secondRun).Status);
            Assert.Single(second.Transport.Fake.Requests, r => r == "POST /session");
            Assert.Single(second.Transport.Fake.Requests, r => r.EndsWith("/prompt_async"));
        }
        finally { try { await Clean(one, a); } finally { await Clean(two, b); } }
    }
}
