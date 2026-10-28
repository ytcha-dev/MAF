using MAF.Host;

namespace MAF.Tests;

// Never enabled by default. Set only in an independently authorized environment after clearance.
public sealed class OwnedProcessOsFactAttribute : FactAttribute
{
    public OwnedProcessOsFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MAF_OWNED_PROCESS_OS_TESTS") != "1")
            Skip = "NotRun: OS child-process tests require separate execution clearance and explicit opt-in";
    }
}

public sealed class OwnedHarnessProcessOsTests
{
    static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    static OwnedHarnessProcess Launch(string mode, bool eof = false, TimeSpan? grace = null)
    {
        // No PATH lookup, copied executables, new projects or temporary directories.
        var executable = Environment.GetEnvironmentVariable("MAF_OWNED_PROCESS_TEST_DOTNET")
            ?? throw new InvalidOperationException("Explicit absolute approved dotnet executable required");
        var configuration = AppContext.BaseDirectory.Contains("Release", StringComparison.Ordinal) ? "Release" : "Debug";
        var fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../MAF.ApprovalAcceptance/bin", configuration, "net10.0/MAF.ApprovalAcceptance.dll"));
        Assert.True(File.Exists(fixture), "Build existing test-only fixture in the authorized environment first");
        var environment = new Dictionary<string, string>();
        // Windows runtime prerequisite only; do not pass API keys, PATH, DOTNET_* hooks or loader overrides.
        if (OperatingSystem.IsWindows()) environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return OwnedHarnessProcess.Start(executable, AppContext.BaseDirectory,
            [fixture, "owned-process-fixture", mode], environment,
            grace ?? Bound, Bound, eof);
    }

    [OwnedProcessOsFact]
    [Trait("Category", "OwnedProcessOS")]
    public async Task LaunchExitAndEarlyExitHaveOriginalExitCode()
    {
        await using var owner = Launch("exit");
        Assert.Equal(23, await owner.WaitForExitAsync(Bound));
        Assert.False(await owner.SurvivedStartupAsync(Bound));
        Assert.False(owner.Observe().ShutdownRequested);
        owner.Dispose(); owner.Dispose();
        await owner.ResourceCleanup.WaitAsync(Bound);
    }

    [OwnedProcessOsFact]
    [Trait("Category", "OwnedProcessOS")]
    public async Task ExplicitEofProtocolGracefullyStopsAndReleasesPipes()
    {
        await using var owner = Launch("eof", eof: true);
        Assert.Equal(OwnedProcessStop.GracefulExit, await owner.StopAsync());
        Assert.Equal(0, owner.Observe().ExitCode);
        owner.Dispose();
        await owner.ResourceCleanup.WaitAsync(Bound);
    }

    [OwnedProcessOsFact]
    [Trait("Category", "OwnedProcessOS")]
    public async Task OutputLargerThanPipeCapacityDoesNotBlockExit()
    {
        await using var owner = Launch("output");
        Assert.Equal(0, await owner.WaitForExitAsync(Bound));
        owner.Dispose();
        await owner.ResourceCleanup.WaitAsync(Bound);
    }

    [OwnedProcessOsFact]
    [Trait("Category", "OwnedProcessOS")]
    public async Task TimeoutCannotTerminateAnotherOwnedInstance()
    {
        await using var unrelated = Launch("eof", eof: true);
        var selected = Launch("linger", grace: TimeSpan.FromMilliseconds(1));
        try
        {
            var stop = await selected.StopAsync();
            Assert.Equal(OperatingSystem.IsWindows() ? OwnedProcessStop.Terminated : OwnedProcessStop.Unresolved, stop);
            Assert.Equal(OwnedProcessState.Running, unrelated.Observe().State);
            await selected.WaitForExitAsync(Bound); // Unix safe subset: fixture exits on its own within five seconds.
        }
        finally
        {
            await selected.WaitForExitAsync(Bound);
            selected.Dispose();
            await selected.ResourceCleanup.WaitAsync(Bound);
        }
    }

    [OwnedProcessOsFact]
    [Trait("Category", "OwnedProcessOS")]
    public void ExistingNonExecutableFailsWithoutFallback()
    {
        // Existing text source is metadata only; no executable fixture files are created.
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../README.md"));
        Assert.True(File.Exists(source));
        Assert.ThrowsAny<Exception>(() => OwnedHarnessProcess.Start(source, AppContext.BaseDirectory,
            [], new Dictionary<string, string>(), Bound, Bound));
    }

    [Fact] // Static path/metadata inspection only; does not launch the interpreter.
    public void KnownPlatformShellIsRejected()
    {
        var shell = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe") : "/bin/sh";
        Assert.True(File.Exists(shell), "Known Windows/Linux shell metadata required for this platform case");
        Assert.Throws<ArgumentException>(() => OwnedHarnessProcess.CreateStartInfo(shell, AppContext.BaseDirectory,
            [], new Dictionary<string, string>()));
    }
}
