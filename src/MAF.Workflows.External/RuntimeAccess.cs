using MAF.Core;
using MAF.Core.Policy;

namespace MAF.Workflows.External;

// Internal composition protocol, not a same-principal security boundary.
internal interface IRuntimeAccess
{
    bool SuppressImplicitAbort { get; }
    void DemandEntry();
    IDisposable Enter(ProviderEffect effect, ExternalTaskRequest? request = null, string? run = null, ExternalExecutionHandle? handle = null);
    Task BeforeWireAsync(HttpMethod method, string path, string task, CancellationToken token);
}
internal sealed class UnavailableRuntimeAccess : IRuntimeAccess
{
    internal static readonly IRuntimeAccess Instance = new UnavailableRuntimeAccess();
    public bool SuppressImplicitAbort => true;
    public void DemandEntry() => throw new ExternalHarnessException("ProtectedRuntimeRequired");
    public IDisposable Enter(ProviderEffect effect, ExternalTaskRequest? request, string? run, ExternalExecutionHandle? handle)
        => throw new ExternalHarnessException("ProtectedRuntimeRequired");
    public Task BeforeWireAsync(HttpMethod method, string path, string task, CancellationToken token)
        => throw new ExternalHarnessException("ProtectedRuntimeRequired");
}
