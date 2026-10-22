using System.Diagnostics;

namespace MAF.Host;

internal enum OwnedProcessState { Running, Exited, Unresolved }
internal enum OwnedProcessStop { AlreadyExited, GracefulExit, Terminated, TerminationTimedOut, Unresolved }
internal sealed record OwnedProcessObservation(OwnedProcessState State, int? ExitCode, bool ShutdownRequested);

// An ownership primitive, NEVER an authorization grant. No runtime/CLI registration.
internal sealed class OwnedHarnessProcess : IDisposable, IAsyncDisposable
{
    readonly IOwnedProcessChild child;
    readonly object sync = new();
    readonly TimeSpan grace, terminationWait;
    readonly bool closeInput;
    Task<OwnedProcessStop>? stop;
    bool disposed;

    private OwnedHarnessProcess(IOwnedProcessChild child, TimeSpan grace, TimeSpan terminationWait, bool closeInput)
    { this.child = child; this.grace = grace; this.terminationWait = terminationWait; this.closeInput = closeInput; }

    internal static OwnedHarnessProcess Start(string executable, string workingDirectory,
        IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment,
        TimeSpan grace, TimeSpan terminationWait, bool closeInputOnStop = false, CancellationToken token = default)
    {
        var start = CreateStartInfo(executable, workingDirectory, arguments, environment);
        ValidateDeadline(grace); ValidateDeadline(terminationWait);
        token.ThrowIfCancellationRequested(); // Synchronous launch commits ownership here; later cancellation uses StopAsync.
        return new(new SystemChild(start), grace, terminationWait, closeInputOnStop);
    }

    internal static System.Diagnostics.ProcessStartInfo CreateStartInfo(string executable, string workingDirectory,
        IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(arguments); ArgumentNullException.ThrowIfNull(environment);
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable))
            throw new ArgumentException("Explicit absolute executable required", nameof(executable));
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Path.IsPathFullyQualified(workingDirectory))
            throw new ArgumentException("Explicit absolute working directory required", nameof(workingDirectory));
        if (!File.Exists(executable)) throw new FileNotFoundException("Selected executable unavailable");
        if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException("Working directory unavailable");
        // Scripts and command interpreters are outside this primitive's contract. No PATH fallback.
        var name = Path.GetFileNameWithoutExtension(executable);
        if (new[] { "cmd", "powershell", "pwsh", "sh", "bash", "dash", "zsh", "csh", "fish" }
            .Contains(name, StringComparer.OrdinalIgnoreCase)
            || new[] { ".cmd", ".bat", ".ps1", ".sh" }.Contains(Path.GetExtension(executable), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Shell launch is unsupported", nameof(executable));
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment.Clear(); // Caller explicitly selects environment; no inherited tokens, PATH or loader options.
        foreach (var pair in environment)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Contains('=') || pair.Key.Contains('\0')
                || pair.Value is null || pair.Value.Contains('\0')) throw new ArgumentException("Invalid environment entry");
            start.Environment.Add(pair.Key, pair.Value);
        }
        foreach (var arg in arguments)
        {
            if (arg is null || arg.Contains('\0')) throw new ArgumentException("Invalid argument");
            start.ArgumentList.Add(arg);
        }
        return start;
    }

    internal static OwnedHarnessProcess ForTests(IOwnedProcessChild child, TimeSpan grace,
        TimeSpan terminationWait, bool closeInputOnStop = false)
    {
        ValidateDeadline(grace); ValidateDeadline(terminationWait);
        return new(child, grace, terminationWait, closeInputOnStop);
    }
    static void ValidateDeadline(TimeSpan value)
    { if (value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(value)); }

    internal OwnedProcessObservation Observe()
    {
        lock (sync)
        {
            var exit = child.Exit;
            if (exit.IsCompletedSuccessfully) return new(OwnedProcessState.Exited, exit.Result, stop is not null);
            if (exit.IsFaulted) _ = exit.Exception; // Observe diagnostics without exposing paths/environment/output.
            return new(exit.IsCompleted ? OwnedProcessState.Unresolved : OwnedProcessState.Running, null, stop is not null);
        }
    }

    // A survival interval is NOT readiness/HTTP preflight. Caller retains ownership if this wait is cancelled.
    internal async Task<bool> SurvivedStartupAsync(TimeSpan interval, CancellationToken token = default)
    {
        ValidateDeadline(interval);
        try { await child.Exit.WaitAsync(interval, token).ConfigureAwait(false); return false; }
        catch (TimeoutException) { return Observe().State == OwnedProcessState.Running; }
    }

    internal Task<int> WaitForExitAsync(TimeSpan timeout, CancellationToken token = default)
    { ValidateDeadline(timeout); return child.Exit.WaitAsync(timeout, token); }

    internal Task ResourceCleanup => child.Cleanup;

    internal Task<OwnedProcessStop> StopAsync()
    {
        lock (sync)
        {
            if (stop is not null) return stop;
            if (disposed) return Task.FromResult(OwnedProcessStop.AlreadyExited);
            // Publish before any backend callback; all concurrent stops share exactly one operation.
            var completion = new TaskCompletionSource<OwnedProcessStop>(TaskCreationOptions.RunContinuationsAsynchronously);
            stop = completion.Task;
            _ = CompleteStop(completion);
            return stop;
        }
    }
    async Task CompleteStop(TaskCompletionSource<OwnedProcessStop> completion)
    {
        try { completion.SetResult(await StopCore().ConfigureAwait(false)); }
        catch { completion.SetResult(OwnedProcessStop.Unresolved); }
    }
    async Task<OwnedProcessStop> StopCore()
    {
        if (child.Exit.IsCompleted) return child.Exit.IsCompletedSuccessfully ? OwnedProcessStop.AlreadyExited : OwnedProcessStop.Unresolved;
        // EOF is opt-in and only useful for a child with an explicitly reviewed EOF protocol.
        if (closeInput) child.CloseInput();
        try { await child.Exit.WaitAsync(grace).ConfigureAwait(false); return OwnedProcessStop.GracefulExit; }
        catch (TimeoutException) { }
        if (child.Exit.IsCompletedSuccessfully) return OwnedProcessStop.GracefulExit;
        child.Terminate(); // Original retained handle only; never descendants, names or PID lookup.
        try { await child.Exit.WaitAsync(terminationWait).ConfigureAwait(false); return OwnedProcessStop.Terminated; }
        catch (TimeoutException) { return OwnedProcessStop.TerminationTimedOut; }
        catch { return OwnedProcessStop.Unresolved; }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            if (stop is { IsCompleted: false } || !child.Exit.IsCompletedSuccessfully)
                throw new InvalidOperationException("OwnedProcessNotResolved: retain owner and await exit before disposal");
            disposed = true;
            child.Dispose(); // No implicit kill; repeated disposal never touches a reused PID.
        }
    }
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        Dispose();
        await ResourceCleanup.WaitAsync(terminationWait).ConfigureAwait(false);
    }

    // Tiny injectable OS seam for deterministic offline tests; no attach operation or public Process exposure.
    internal interface IOwnedProcessChild : IDisposable
    {
        Task<int> Exit { get; }
        Task Cleanup { get; }
        void CloseInput();
        void Terminate();
    }

    sealed class SystemChild : IOwnedProcessChild
    {
        readonly Process process;
        readonly CancellationTokenSource outputStop = new();
        readonly Task stdout, stderr;
        readonly TaskCompletionSource cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<int> Exit { get; }
        public Task Cleanup => cleanup.Task;
        internal SystemChild(ProcessStartInfo start)
        {
            process = new() { StartInfo = start };
            try
            {
                if (!process.Start()) throw new InvalidOperationException("OwnedProcessStartFailed");
            }
            catch { process.Dispose(); outputStop.Dispose(); throw; }
            // Start both drains immediately. Fixed buffers, no retained output, no line/read-to-end buffering.
            stdout = Discard(process.StandardOutput.BaseStream, outputStop.Token);
            stderr = Discard(process.StandardError.BaseStream, outputStop.Token);
            Exit = ObserveExit();
        }
        async Task<int> ObserveExit()
        {
            // HasExited polling avoids WaitForExitAsync's redirected-stream EOF coupling to descendants.
            while (!process.HasExited) await Task.Delay(20).ConfigureAwait(false);
            return process.ExitCode;
        }
        static async Task Discard(Stream stream, CancellationToken token)
        {
            var buffer = new byte[4096];
            try { while (await stream.ReadAsync(buffer, token).ConfigureAwait(false) != 0) { } }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (ObjectDisposedException) { }
            catch (IOException) { } // Stream diagnostics cannot establish child/session success.
        }
        public void CloseInput() { if (!process.HasExited) process.StandardInput.Close(); }
        public void Terminate()
        {
            if (process.HasExited) return;
            // Built-in Unix Kill is PID based; this increment cannot prove identity atomically.
            // Leave a noncooperative child owned/unresolved instead of risking PID reuse.
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Safe forced termination unavailable");
            try { process.Kill(entireProcessTree: false); }
            catch (InvalidOperationException) when (process.HasExited) { } // Exit raced termination.
        }
        public void Dispose()
        {
            Exception? failure = null;
            void Release(Action action) { try { action(); } catch (Exception error) { failure ??= error; } }
            Release(outputStop.Cancel);
            Release(process.StandardInput.Dispose);
            Release(process.StandardOutput.Dispose);
            Release(process.StandardError.Dispose);
            Release(process.Dispose); // Close our pipe ends even if descendants retain theirs.
            _ = ReleaseOutput(failure);
            if (failure is not null) throw new IOException("OwnedProcessResourceCleanupFailed");
        }
        async Task ReleaseOutput(Exception? failure)
        {
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
            catch (Exception error) { failure ??= error; }
            finally
            {
                outputStop.Dispose();
                if (failure is null) cleanup.TrySetResult();
                else cleanup.TrySetException(new IOException("OwnedProcessResourceCleanupFailed"));
            }
        }
    }
}
