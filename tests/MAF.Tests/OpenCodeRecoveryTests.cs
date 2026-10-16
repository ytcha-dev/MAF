using MAF.Core;
using MAF.Workflows.External.OpenCode;

namespace MAF.Tests;

// Authored only. Uses the existing in-memory HTTP fixture; no socket or child process.
// Its temporary directory operations also require security clearance before execution.
public sealed class OpenCodeRecoveryTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("maf-recovery-").FullName;

    [Fact]
    public async Task NewClientReattachesSameSessionAndCannotSubmitAgain()
    {
        using var handler = new OpenCodeApiFixture(root);
        using var firstHttp = new HttpClient(handler, disposeHandler: false);
        var options = new OpenCodeOptions(new Uri("http://127.0.0.1:4096"), root, AllowLoopbackHttp: true);
        var first = new OpenCodeClient(HistoricalRuntimeAccess.Instance, firstHttp, options);
        var request = new ExternalTaskRequest("recovery", root, "Read synthetic README.", TimeSpan.FromSeconds(3));
        var prepared = first.PrepareExecution();
        await first.ValidateAsync(request, default);
        var handle = await first.CreateAsync(request, "original-workflow", prepared, default);
        Assert.Equal(prepared.ExternalRequestId, handle.ExternalRequestId);
        await first.SubmitAsync(request, handle, default);
        var routesBeforeRecovery = handler.Routes.Count;
        using var secondHttp = new HttpClient(handler, disposeHandler: false);
        var second = new OpenCodeClient(HistoricalRuntimeAccess.Instance, secondHttp, options);
        Assert.Equal(first.Scope, second.Scope);
        await second.ReattachAsync(request.TaskId, root, handle, default);
        Assert.Equal(ExternalExecutionStatus.Completed, (await second.ObserveAsync(handle, default)).Status);
        var error = await Assert.ThrowsAsync<ExternalHarnessException>(() => second.SubmitAsync(request, handle, default));
        Assert.Equal("DuplicateSubmissionBlocked", error.Code);
        Assert.Equal(1, handler.Submissions);
        Assert.DoesNotContain(handler.Routes.Skip(routesBeforeRecovery), route => route.Contains("prompt_async"));
        Assert.Equal(1, handler.Routes.Count(route => route.StartsWith("/session?", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ReattachmentRejectsUnapprovedWorkspaceBeforeBinding()
    {
        using var handler = new OpenCodeApiFixture(root);
        using var http = new HttpClient(handler);
        var client = new OpenCodeClient(HistoricalRuntimeAccess.Instance, http, new(new Uri("http://127.0.0.1:4096"), root, AllowLoopbackHttp: true));
        var handle = client.PrepareExecution() with { ExternalSessionId = "ses_fixture" };
        await Assert.ThrowsAsync<ExternalHarnessException>(() => client.ReattachAsync("task", Path.GetDirectoryName(root)!, handle, default));
        Assert.Empty(handler.Routes);
        Assert.Equal(0, handler.Submissions);
    }

    public void Dispose() => Directory.Delete(root);
}
