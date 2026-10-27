using System.Text.RegularExpressions;
using System.Runtime.ExceptionServices;
using MAF.Core;
using MAF.Core.Policy;
using MAF.Workflows.External;
using MAF.Workflows.External.OpenCode;

namespace MAF.Host;

// Expected selection and observed FAKE launch evidence, not executable approval/attestation.
internal sealed record V1PreflightSelection(string Executable, string Sha256, string WorkingDirectory, Uri Origin);
internal sealed record V1PreflightEndpoint(OwnedHarnessProcess Owner, Uri Origin, string Executable,
    string Sha256, HttpMessageHandler Transport);

// Deliberately only ForTests: no OS launch/socket implementation, CLI registration or live factory.
// A real endpoint-custody mechanism must be reviewed before an operational composition exists.
internal sealed class OwnedOpenCodePreflight : IAsyncDisposable
{
    readonly CancellationTokenSource deadline;
    HttpClient? http;
    int released;
    string? failure;
    ExceptionDispatchInfo? transportFailure;
    internal OwnedHarnessProcess? Owner { get; private set; }
    internal Task ValidationCompletion { get; private set; } = Task.CompletedTask;
    internal string? FailureCode => Volatile.Read(ref failure);
    internal bool PreparedOffline => ValidationCompletion.IsCompletedSuccessfully && FailureCode is null
        && Owner is { InitializationFailed: false } && Owner.Observe().State == OwnedProcessState.Running
        && !Owner.Observe().ShutdownRequested
        && Volatile.Read(ref released) == 0 && !deadline.IsCancellationRequested;
    internal string Evidence => "ControlledOfflineFixture / NotLiveAdmission / NotEndpointCustodyProof";

    OwnedOpenCodePreflight(TimeSpan timeout, CancellationToken token)
    {
        deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
    }

    internal static async Task<OwnedOpenCodePreflight> ForTests(V1PreflightSelection selection,
        Func<CancellationToken, Task<bool>> portUnoccupied,
        Func<CancellationToken, Task<(OwnedHarnessProcess Owner, V1PreflightEndpoint Endpoint)>> start,
        TimeSpan timeout, CancellationToken token = default)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (selection.Origin.Scheme != "http" || selection.Origin.Host is not ("127.0.0.1" or "[::1]")
            || selection.Origin.IsDefaultPort || selection.Origin.UserInfo.Length != 0
            || selection.Origin.AbsolutePath != "/" || selection.Origin.Query.Length != 0 || selection.Origin.Fragment.Length != 0
            || !Regex.IsMatch(selection.Sha256, "\\A[0-9a-f]{64}\\z"))
            throw new ArgumentException("InvalidPreflightSelection");
        // Existing metadata validation only; never execute this start info here.
        _ = OwnedHarnessProcess.CreateStartInfo(selection.Executable, selection.WorkingDirectory,
            ["serve", "--hostname", selection.Origin.Host, "--port", selection.Origin.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            new Dictionary<string, string>());
        var result = new OwnedOpenCodePreflight(timeout, token);
        result.ValidationCompletion = result.Prepare(selection, portUnoccupied, start);
        try { await result.ValidationCompletion.WaitAsync(timeout, token).ConfigureAwait(false); }
        catch (TimeoutException) { result.Fail("PreflightTimedOut"); }
        catch (OperationCanceledException) { result.Fail("PreflightCancelled"); }
        return result; // Even a late/uncooperative start remains tracked by ValidationCompletion.
    }

    void Fail(string code) => Interlocked.CompareExchange(ref failure, code, null);

    async Task Prepare(V1PreflightSelection selection, Func<CancellationToken, Task<bool>> portUnoccupied,
        Func<CancellationToken, Task<(OwnedHarnessProcess Owner, V1PreflightEndpoint Endpoint)>> start)
    {
        try
        {
            var token = deadline.Token;
            token.ThrowIfCancellationRequested();
            if (!await portUnoccupied(token).ConfigureAwait(false))
                throw new ExternalHarnessException("PreflightPortOccupied");
            token.ThrowIfCancellationRequested();
            var launched = await start(token).ConfigureAwait(false);
            Owner = launched.Owner;
            // The fake launch transfers its transport as well as its original child to this owner.
            http = new(new DiscoveryOnlyHandler(launched.Endpoint.Transport, selection.Origin, Owner)) { BaseAddress = selection.Origin };
            var endpoint = launched.Endpoint;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!ReferenceEquals(endpoint.Owner, Owner) || endpoint.Origin != selection.Origin
                || !string.Equals(endpoint.Executable, selection.Executable, comparison) || endpoint.Sha256 != selection.Sha256)
                throw new ExternalHarnessException("PreflightOwnershipMismatch");
            token.ThrowIfCancellationRequested();
            if (Owner.InitializationFailed || Owner.Observe().State != OwnedProcessState.Running)
                throw new ExternalHarnessException("PreflightChildUnavailable");
            var client = new OpenCodeClient(new DiscoveryAccess(), http,
                new(selection.Origin, selection.WorkingDirectory, AllowLoopbackHttp: true, ExpectedVersion: "1.19.2"));
            await client.ValidateAsync(new("preflight-inspection", selection.WorkingDirectory,
                "Offline discovery only", TimeSpan.FromSeconds(30)), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (Owner.Observe().State != OwnedProcessState.Running)
                throw new ExternalHarnessException("PreflightChildUnavailable");
        }
        catch (OwnedProcessInitializationException error)
        {
            Owner = error.Owner;
            Fail("PreflightInitializationFailed");
        }
        catch (OperationCanceledException) { Fail("PreflightCancelled"); }
        catch (ExternalHarnessException error) { Fail(error.Code); }
        catch { Fail("PreflightUnavailable"); } // No raw paths/environment/output in diagnostics.
    }

    internal Task<OwnedProcessStop> StopAsync()
    {
        if (!ValidationCompletion.IsCompleted) throw new InvalidOperationException("AwaitPreflightBeforeStop");
        return Owner?.StopAsync() ?? Task.FromResult(OwnedProcessStop.AlreadyExited);
    }

    public async ValueTask DisposeAsync()
    {
        transportFailure?.Throw();
        if (!ValidationCompletion.IsCompleted) throw new InvalidOperationException("AwaitPreflightBeforeDisposal");
        try
        {
            if (Owner is not null) await Owner.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            // A stop timeout/Unresolved never abandons the original owner or its transport.
            if ((Owner is null || Owner.Observe().State == OwnedProcessState.Exited) && Interlocked.Exchange(ref released, 1) == 0)
            {
                try { http?.Dispose(); }
                catch (Exception error) { transportFailure = ExceptionDispatchInfo.Capture(error); throw; }
                finally { deadline.Dispose(); }
            }
        }
    }

    sealed class DiscoveryAccess : IRuntimeAccess
    {
        public bool SuppressImplicitAbort => true;
        public void DemandEntry() { }
        public IDisposable Enter(ProviderEffect effect, ExternalTaskRequest? request = null, string? run = null, ExternalExecutionHandle? handle = null)
            => effect == ProviderEffect.Validate ? new Scope() : throw new ExternalHarnessException("PreflightEffectDenied");
        public Task BeforeWireAsync(HttpMethod method, string path, string task, CancellationToken token) => Task.CompletedTask;
        sealed class Scope : IDisposable { public void Dispose() { } }
    }

    sealed class DiscoveryOnlyHandler(HttpMessageHandler inner, Uri origin, OwnedHarnessProcess owner) : DelegatingHandler(inner)
    {
        readonly HashSet<string> remaining = ["/global/health", "/doc", "/agent"];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (owner.InitializationFailed || owner.Observe().State != OwnedProcessState.Running)
                throw new ExternalHarnessException("PreflightChildUnavailable");
            if (request.Method != HttpMethod.Get || request.RequestUri is not { } uri
                || uri.GetLeftPart(UriPartial.Authority) != origin.GetLeftPart(UriPartial.Authority)
                || !remaining.Remove(uri.AbsolutePath))
                throw new ExternalHarnessException("PreflightEffectDenied");
            var response = await base.SendAsync(request, token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } actual && actual != uri)
            { response.Dispose(); throw new ExternalHarnessException("PreflightRedirectRejected"); }
            return response;
        }
    }
}
