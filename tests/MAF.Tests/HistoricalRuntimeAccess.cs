using MAF.Core;
using MAF.Core.Policy;
using MAF.Workflows.External;

namespace MAF.Tests;

// Explicit historical-only fixture. Not referenced by Host or any production composition.
// These regression tests prove old state/HTTP behavior, not protected authorization.
internal sealed class HistoricalRuntimeAccess : IRuntimeAccess
{
    internal static readonly HistoricalRuntimeAccess Instance = new();
    public bool SuppressImplicitAbort => false;
    public void DemandEntry() { }
    public IDisposable Enter(ProviderEffect effect, ExternalTaskRequest? request = null, string? run = null, ExternalExecutionHandle? handle = null) => new Scope();
    public Task BeforeWireAsync(HttpMethod method, string path, string task, CancellationToken token) => Task.CompletedTask;
    sealed class Scope : IDisposable { public void Dispose() { } }
}
