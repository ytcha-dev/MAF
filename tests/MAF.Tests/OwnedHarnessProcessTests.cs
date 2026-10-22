using MAF.Host;

namespace MAF.Tests;

// Pure/in-memory: no directories, sockets, child processes, providers or model clients.
public sealed class OwnedHarnessProcessTests
{
    static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(30);
    static string ExistingFile => typeof(OwnedHarnessProcess).Assembly.Location;
    static string WorkingDirectory => AppContext.BaseDirectory;
    static OwnedHarnessProcess Own(FakeChild child, bool eof = false)
        => OwnedHarnessProcess.ForTests(child, Deadline, Deadline, eof);

    [Theory]
    [InlineData("")]
    [InlineData("tool")]
    [InlineData("relative/tool")]
    public void RequiresAbsoluteExecutable(string path)
        => Assert.Throws<ArgumentException>(() => OwnedHarnessProcess.CreateStartInfo(path, WorkingDirectory, [], new Dictionary<string, string>()));

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    public void RequiresExplicitWorkingDirectory(string directory)
        => Assert.Throws<ArgumentException>(() => OwnedHarnessProcess.CreateStartInfo(ExistingFile, directory, [], new Dictionary<string, string>()));

    [Fact]
    public void MissingExecutableAndDirectoryFailBeforeLaunch()
    {
        // A child of a file can never exist; no randomly named fixture is necessary.
        Assert.Throws<FileNotFoundException>(() => OwnedHarnessProcess.CreateStartInfo(Path.Combine(ExistingFile, "missing"), WorkingDirectory, [], new Dictionary<string, string>()));
        Assert.Throws<DirectoryNotFoundException>(() => OwnedHarnessProcess.CreateStartInfo(ExistingFile, Path.Combine(ExistingFile, "missing"), [], new Dictionary<string, string>()));
    }

    [Fact]
    public void ArgumentListAndEnvironmentAreExplicit()
    {
        var start = OwnedHarnessProcess.CreateStartInfo(ExistingFile, WorkingDirectory,
            ["argument with spaces", "$(never a shell)", "a;b"], new Dictionary<string, string> { ["OWNED_FIXTURE"] = "yes" });
        Assert.False(start.UseShellExecute);
        Assert.Empty(start.Arguments);
        Assert.Equal(new[] { "argument with spaces", "$(never a shell)", "a;b" }, start.ArgumentList);
        Assert.Single(start.Environment);
        Assert.Equal("yes", start.Environment["OWNED_FIXTURE"]);
        Assert.True(start.RedirectStandardOutput && start.RedirectStandardError && start.RedirectStandardInput);
    }

    [Fact]
    public void InvalidArgumentsAndEnvironmentFailBeforeLaunch()
    {
        Assert.Throws<ArgumentException>(() => OwnedHarnessProcess.CreateStartInfo(ExistingFile, WorkingDirectory, ["bad\0argument"], new Dictionary<string, string>()));
        Assert.Throws<ArgumentException>(() => OwnedHarnessProcess.CreateStartInfo(ExistingFile, WorkingDirectory, [], new Dictionary<string, string> { ["bad=name"] = "value" }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(61000)]
    public void DeadlinesMustBeFiniteAndBounded(int milliseconds)
        => Assert.Throws<ArgumentOutOfRangeException>(() => OwnedHarnessProcess.ForTests(new FakeChild(), TimeSpan.FromMilliseconds(milliseconds), Deadline));

    [Fact]
    public void CancelledStartupNeverExecutesSelectedFile()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => OwnedHarnessProcess.Start(ExistingFile, WorkingDirectory,
            [], new Dictionary<string, string>(), Deadline, Deadline, token: cts.Token));
    }

    [Fact]
    public async Task NormalExitPreservesExitCodeAndDisposesOnce()
    {
        var child = new FakeChild(); var owner = Own(child);
        Assert.Equal(OwnedProcessState.Running, owner.Observe().State);
        child.Complete(7);
        Assert.Equal(7, await owner.WaitForExitAsync(Deadline));
        Assert.Equal(OwnedProcessStop.AlreadyExited, await owner.StopAsync());
        owner.Dispose(); owner.Dispose(); await owner.DisposeAsync();
        Assert.Equal(1, child.Disposals); Assert.Equal(0, child.Kills);
        Assert.Equal(7, owner.Observe().ExitCode);
        await owner.ResourceCleanup;
    }

    [Fact]
    public async Task StartupSurvivalDetectsEarlyExitWithoutClaimingReadiness()
    {
        var child = new FakeChild(); var owner = Own(child);
        child.Complete(0);
        Assert.False(await owner.SurvivedStartupAsync(Deadline));
        Assert.False(owner.Observe().ShutdownRequested);
        owner.Dispose();
    }

    [Fact]
    public async Task StartupSurvivalAndExitWaitTimeoutKeepOriginalOwner()
    {
        var child = new FakeChild(); var owner = Own(child);
        Assert.True(await owner.SurvivedStartupAsync(Deadline));
        await Assert.ThrowsAsync<TimeoutException>(() => owner.WaitForExitAsync(Deadline));
        Assert.Equal(0, child.Kills);
        child.Complete(0); owner.Dispose();
    }

    [Fact]
    public async Task CancelledObservationRetainsOwnerAndDoesNotKill()
    {
        var child = new FakeChild(); var owner = Own(child);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.SurvivedStartupAsync(Deadline, cts.Token));
        Assert.Equal(0, child.Kills);
        Assert.Throws<InvalidOperationException>(() => owner.Dispose());
        child.Complete(0); owner.Dispose();
    }

    [Fact]
    public async Task GracefulEofStopsOnlyOwnedChild()
    {
        var unrelated = new FakeChild(); var child = new FakeChild { ExitOnEof = true };
        var owner = Own(child, eof: true);
        Assert.Equal(OwnedProcessStop.GracefulExit, await owner.StopAsync());
        Assert.True(owner.Observe().ShutdownRequested);
        Assert.Equal(1, child.Eofs); Assert.Equal(0, child.Kills);
        Assert.Equal(0, unrelated.Kills); Assert.False(unrelated.Exit.IsCompleted);
        owner.Dispose();
    }

    [Fact]
    public async Task GraceTimeoutTerminatesOriginalChildOnce()
    {
        var child = new FakeChild { ExitOnKill = true }; var owner = Own(child);
        var first = owner.StopAsync(); var second = owner.StopAsync();
        Assert.Same(first, second);
        Assert.Equal(OwnedProcessStop.Terminated, await first);
        Assert.Equal(1, child.Kills); Assert.Equal(0, child.Eofs);
        owner.Dispose();
    }

    [Fact]
    public async Task TerminationTimeoutRetainsOwnershipUntilObservedExit()
    {
        var child = new FakeChild(); var owner = Own(child);
        Assert.Equal(OwnedProcessStop.TerminationTimedOut, await owner.StopAsync());
        Assert.Throws<InvalidOperationException>(() => owner.Dispose());
        Assert.Equal(0, child.Disposals);
        child.Complete(0); owner.Dispose();
        Assert.Equal(OwnedProcessStop.TerminationTimedOut, await owner.StopAsync()); // No retry or rewritten evidence.
    }

    [Fact]
    public async Task ConcurrentStopAndDisposeDoNotAbandonOrDoubleTerminate()
    {
        var child = new FakeChild(); var owner = Own(child);
        var stop = owner.StopAsync();
        var contenders = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            Assert.Same(stop, owner.StopAsync());
            Assert.Throws<InvalidOperationException>(() => owner.Dispose());
        })).ToArray();
        await Task.WhenAll(contenders);
        child.Complete(0); await stop;
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(owner.Dispose)));
        Assert.Equal(1, child.Disposals);
    }

    [Fact]
    public async Task UnknownExitAndTerminationFailureAreNotSuccess()
    {
        var child = new FakeChild { ThrowOnKill = true }; var owner = Own(child);
        Assert.Equal(OwnedProcessStop.Unresolved, await owner.StopAsync());
        child.Completion.SetException(new IOException("synthetic observation failure"));
        Assert.Equal(OwnedProcessState.Unresolved, owner.Observe().State);
        Assert.Null(owner.Observe().ExitCode);
        Assert.Throws<InvalidOperationException>(() => owner.Dispose());
    }

    sealed class FakeChild : OwnedHarnessProcess.IOwnedProcessChild
    {
        internal readonly TaskCompletionSource<int> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<int> Exit => Completion.Task;
        public Task Cleanup => Task.CompletedTask;
        internal bool ExitOnEof, ExitOnKill, ThrowOnKill;
        internal int Kills, Eofs, Disposals;
        internal void Complete(int code) => Completion.TrySetResult(code);
        public void CloseInput() { Eofs++; if (ExitOnEof) Complete(0); }
        public void Terminate() { Kills++; if (ThrowOnKill) throw new IOException("synthetic termination failure"); if (ExitOnKill) Complete(137); }
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
}
