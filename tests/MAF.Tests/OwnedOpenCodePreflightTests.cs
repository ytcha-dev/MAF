using System.Net;
using MAF.Host;

namespace MAF.Tests;

// No process/socket/file creation: launch/port/identity evidence is explicitly fake.
public sealed class OwnedOpenCodePreflightTests
{
    static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    static V1PreflightSelection Selection => new(typeof(OwnedHarnessProcess).Assembly.Location,
        new string('a', 64), AppContext.BaseDirectory, new Uri("http://127.0.0.1:43127"));

    sealed class Child : OwnedHarnessProcess.IOwnedProcessChild
    {
        internal readonly TaskCompletionSource<int> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool ExitOnEof = true, RejectKill;
        internal int Disposals, Kills;
        public Task<int> Exit => Completion.Task;
        public Task Cleanup => Task.CompletedTask;
        public void CloseInput() { if (ExitOnEof) Completion.TrySetResult(0); }
        public void Terminate() { Kills++; if (RejectKill) throw new PlatformNotSupportedException(); Completion.TrySetResult(7); }
        public void Dispose() { Disposals++; }
    }

    sealed class Transport(OpenCodeApiFixture api) : DelegatingHandler(api)
    {
        internal readonly List<string> Requests = [];
        internal Action? AfterHealth;
        internal bool UnsafeAgent, FailDispose;
        internal int Disposals;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            if (UnsafeAgent && request.RequestUri.AbsolutePath == "/agent")
                return new(HttpStatusCode.OK) { Content = new StringContent("""
                    [{"name":"maf_readonly","permission":[{"permission":"*","pattern":"*","action":"allow"}]}]
                    """) };
            var response = await base.SendAsync(request, token);
            if (request.RequestUri.AbsolutePath == "/global/health") AfterHealth?.Invoke();
            return response;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposals++;
            base.Dispose(disposing);
            if (disposing && FailDispose) throw new IOException("synthetic release failure");
        }
    }

    sealed class Fixture
    {
        internal readonly Child Child = new();
        internal readonly OwnedHarnessProcess Owner;
        internal readonly Transport Transport;
        internal int Starts, PortChecks;
        internal bool Available = true;
        internal Fixture(string version = "1.19.2", bool omitPermission = false, bool delayHealth = false)
        {
            Owner = OwnedHarnessProcess.ForTests(Child, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), true);
            Transport = new(new OpenCodeApiFixture(AppContext.BaseDirectory)
                { Version = version, OmitPermissionApi = omitPermission, DelayHealth = delayHealth });
        }
        internal V1PreflightEndpoint Endpoint => new(Owner, Selection.Origin, Selection.Executable, Selection.Sha256, Transport);
        internal Task<OwnedOpenCodePreflight> Run(V1PreflightEndpoint? endpoint = null, TimeSpan? timeout = null)
            => OwnedOpenCodePreflight.ForTests(Selection, token => { PortChecks++; return Task.FromResult(Available); },
                token => { Starts++; return Task.FromResult((Owner, endpoint ?? Endpoint)); }, timeout ?? Bound);
    }

    [Fact]
    public async Task MatchingFakeOwnerDiscoversExactlyThreeGetsAndReleasesOnce()
    {
        var f = new Fixture(); var result = await f.Run();
        Assert.True(result.PreparedOffline); Assert.Null(result.FailureCode); Assert.Same(f.Owner, result.Owner);
        Assert.Contains("NotEndpointCustodyProof", result.Evidence); Assert.Contains("NotLiveAdmission", result.Evidence);
        Assert.Equal(new[] { "GET /global/health", "GET /doc", "GET /agent" }, f.Transport.Requests);
        Assert.Equal(1, f.Starts); Assert.Equal(1, f.PortChecks);
        await result.DisposeAsync(); await result.DisposeAsync();
        Assert.Equal(1, f.Child.Disposals); Assert.Equal(1, f.Transport.Disposals); Assert.False(result.PreparedOffline);
    }

    [Fact]
    public async Task OccupiedPortNeverStartsOrProbesEvenIfMatchingServerCouldExist()
    {
        var f = new Fixture { Available = false }; await using var result = await f.Run();
        Assert.Equal("PreflightPortOccupied", result.FailureCode); Assert.Null(result.Owner);
        Assert.Equal(0, f.Starts); Assert.Empty(f.Transport.Requests);
        f.Child.Completion.SetResult(0); f.Owner.Dispose(); f.Transport.Dispose(); // Untransferred test resources.
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("origin")]
    [InlineData("digest")]
    [InlineData("executable")]
    public async Task MatchingVersionCannotSubstituteForeignEndpointOrBinary(string mismatch)
    {
        var f = new Fixture(); var other = new Fixture();
        var endpoint = mismatch switch
        {
            "owner" => f.Endpoint with { Owner = other.Owner },
            "origin" => f.Endpoint with { Origin = new Uri("http://127.0.0.1:43128") },
            "digest" => f.Endpoint with { Sha256 = new string('b', 64) },
            _ => f.Endpoint with { Executable = Selection.Executable + ".other" }
        };
        var result = await f.Run(endpoint);
        Assert.Equal("PreflightOwnershipMismatch", result.FailureCode); Assert.False(result.PreparedOffline);
        Assert.Empty(f.Transport.Requests); Assert.Same(f.Owner, result.Owner);
        await result.DisposeAsync(); Assert.Equal(0, other.Child.Kills); Assert.Equal(0, other.Child.Disposals);
        other.Child.Completion.SetResult(0); other.Owner.Dispose(); other.Transport.Dispose();
    }

    [Fact]
    public async Task StartFailureIsBoundedAndDoesNotInventAnOwner()
    {
        await using var result = await OwnedOpenCodePreflight.ForTests(Selection, _ => Task.FromResult(true),
            _ => throw new IOException("sensitive binary path"), Bound);
        Assert.Equal("PreflightUnavailable", result.FailureCode); Assert.Null(result.Owner);
    }

    [Fact]
    public async Task PostStartSetupFailureRetainsExactOwnerUntilExplicitShutdown()
    {
        var child = new Child();
        var result = await OwnedOpenCodePreflight.ForTests(Selection, _ => Task.FromResult(true), _ =>
        {
            OwnedHarnessProcess.InitializeStartedForTests(child, () => throw new IOException(), Bound, Bound, true);
            throw new InvalidOperationException("unreachable");
        }, Bound);
        Assert.Equal("PreflightInitializationFailed", result.FailureCode); Assert.NotNull(result.Owner);
        Assert.True(result.Owner.InitializationFailed); Assert.Equal(0, child.Disposals);
        await result.DisposeAsync(); Assert.Equal(1, child.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlyExitBeforeOrDuringDiscoveryCannotBePrepared(bool duringDiscovery)
    {
        var f = new Fixture();
        if (duringDiscovery) f.Transport.AfterHealth = () => f.Child.Completion.TrySetResult(23);
        else f.Child.Completion.SetResult(23);
        await using var result = await f.Run();
        Assert.False(result.PreparedOffline); Assert.Equal("PreflightChildUnavailable", result.FailureCode);
        Assert.Equal(23, result.Owner!.Observe().ExitCode);
        Assert.True(f.Transport.Requests.Count <= 1);
    }

    [Theory]
    [InlineData("1.18.35", false, false, "OpenCodeVersionMismatch")]
    [InlineData("1.19.2", true, false, "UnsupportedServerApi")]
    [InlineData("1.19.2", false, true, "AgentAllowsDangerousTools")]
    public async Task HealthVersionSchemaAndEffectiveAgentPolicyAreAllRequired(string version, bool missingApi, bool unsafeAgent, string code)
    {
        var f = new Fixture(version, missingApi); f.Transport.UnsafeAgent = unsafeAgent;
        await using var result = await f.Run();
        Assert.False(result.PreparedOffline); Assert.Equal(code, result.FailureCode);
        Assert.All(f.Transport.Requests, x => Assert.StartsWith("GET ", x));
        Assert.True(f.Transport.Requests.Count <= 3);
    }

    [Fact]
    public async Task UncooperativeLateStartRemainsTrackedAndCannotBeDisposedAway()
    {
        var f = new Fixture();
        var start = new TaskCompletionSource<(OwnedHarnessProcess, V1PreflightEndpoint)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = await OwnedOpenCodePreflight.ForTests(Selection, _ => Task.FromResult(true), _ => start.Task, TimeSpan.FromMilliseconds(30));
        Assert.False(result.PreparedOffline); Assert.False(result.ValidationCompletion.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => result.DisposeAsync().AsTask());
        start.SetResult((f.Owner, f.Endpoint)); await result.ValidationCompletion.WaitAsync(Bound);
        Assert.Same(f.Owner, result.Owner); Assert.False(result.PreparedOffline); Assert.Empty(f.Transport.Requests);
        await result.DisposeAsync(); Assert.Equal(1, f.Child.Disposals);
    }

    [Fact]
    public async Task HealthTimeoutRetainsOriginalOwnerAndNeverSendsPost()
    {
        var f = new Fixture(delayHealth: true);
        var result = await f.Run(timeout: TimeSpan.FromMilliseconds(50));
        Assert.False(result.PreparedOffline); Assert.Same(f.Owner, result.Owner);
        Assert.Contains(result.FailureCode, new[] { "PreflightCancelled", "PreflightTimedOut" });
        await result.ValidationCompletion.WaitAsync(Bound);
        Assert.Equal(new[] { "GET /global/health" }, f.Transport.Requests);
        Assert.Equal(0, f.Child.Disposals); await result.DisposeAsync();
        Assert.Equal(1, f.Child.Disposals);
    }

    [Fact]
    public async Task UnresolvedStopCannotDisposeOriginalOwnerOrTransport()
    {
        var f = new Fixture(); f.Child.ExitOnEof = false; f.Child.RejectKill = true;
        var result = await f.Run(); var stop = result.StopAsync();
        Assert.Equal(OwnedProcessStop.Unresolved, await stop); Assert.Same(stop, result.StopAsync());
        Assert.False(result.PreparedOffline);
        await Assert.ThrowsAsync<InvalidOperationException>(() => result.DisposeAsync().AsTask());
        Assert.Equal(0, f.Child.Disposals); Assert.Equal(0, f.Transport.Disposals); Assert.Equal(1, f.Child.Kills);
        f.Child.Completion.SetResult(0); await result.DisposeAsync(); await result.DisposeAsync();
        Assert.Equal(1, f.Child.Disposals); Assert.Equal(1, f.Transport.Disposals);
    }

    [Fact]
    public async Task TransportCleanupFaultRemainsVisibleAcrossRepeatedDisposal()
    {
        var f = new Fixture(); f.Transport.FailDispose = true; var result = await f.Run();
        await Assert.ThrowsAsync<IOException>(() => result.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<IOException>(() => result.DisposeAsync().AsTask());
        Assert.Equal(1, f.Child.Disposals); Assert.Equal(1, f.Transport.Disposals);
    }

    [Fact]
    public async Task CancelledInputCannotReachPortOrLaunchCallbacks()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await using var result = await OwnedOpenCodePreflight.ForTests(Selection,
            _ => throw new InvalidOperationException("port must not be probed"),
            _ => throw new InvalidOperationException("must not start"), Bound, cancelled.Token);
        Assert.False(result.PreparedOffline); Assert.Null(result.Owner); Assert.Equal("PreflightCancelled", result.FailureCode);
    }
}
