using MAF.Core;
using MAF.Workflows.External;
using MAF.Workflows.External.OpenCode;

namespace MAF.Tests;

// In-memory HTTP + existing directory metadata; no new files, processes, sockets or models.
public sealed class OpenCodeVersionPinTests
{
    sealed class ReleaseSchema(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            if (request.RequestUri!.AbsolutePath == "/doc")
            {
                using var stream = typeof(OpenCodeVersionPinTests).Assembly.GetManifestResourceStream("MAF.Tests.Fixtures.opencode-1.19.2-discovery.json")!;
                using var reader = new StreamReader(stream);
                response.Content.Dispose(); response.Content = new StringContent(await reader.ReadToEndAsync(token));
            }
            return response;
        }
    }
    static OpenCodeOptions Options(string? version) => new(new Uri("http://127.0.0.1:4096"), AppContext.BaseDirectory,
        AllowLoopbackHttp: true, ExpectedVersion: version);
    static ExternalTaskRequest Request() => new("version-pin", AppContext.BaseDirectory, "ReadOnly fixture", TimeSpan.FromSeconds(5));

    [Fact]
    public async Task ExactReleaseOpenApiProjectionPassesCurrentDiscoveryWithoutAnyPost()
    {
        using var api = new OpenCodeApiFixture(AppContext.BaseDirectory) { Version = "1.19.2" };
        using var http = new HttpClient(new ReleaseSchema(api));
        var client = new OpenCodeClient(HistoricalRuntimeAccess.Instance, http, Options("1.19.2"));
        await client.ValidateAsync(Request(), default);
        Assert.Equal("1.19.2", client.ServerVersion); Assert.Equal(3, api.Routes.Count);
        Assert.Equal(0, api.Submissions); Assert.DoesNotContain(api.Routes, x => x.StartsWith("/session"));
    }

    [Theory]
    [InlineData("1.19.2")]
    [InlineData("1.18.35")]
    public async Task ExactReviewedVersionValidatesBeforeOneCreateAndPrompt(string version)
    {
        using var handler = new OpenCodeApiFixture(AppContext.BaseDirectory) { Version = version };
        using var http = new HttpClient(handler);
        var client = new OpenCodeClient(HistoricalRuntimeAccess.Instance, http, Options(version));
        var result = await new ExternalWorkflow(HistoricalRuntimeAccess.Instance, client).RunAsync(Request());
        Assert.Equal(ExternalExecutionStatus.Completed, result.Result.Status);
        Assert.Equal(version, client.ServerVersion); Assert.Equal(1, handler.Submissions);
        Assert.StartsWith("/global/health", handler.Routes.First());
    }

    [Theory]
    [InlineData("1.18.35")]
    [InlineData("1.19.0")]
    [InlineData("1.19.3")]
    [InlineData("2.0.0")]
    [InlineData("1.19.2-beta")]
    [InlineData("fixture")]
    [InlineData(null)]
    public async Task MismatchedOrUnknownServerStopsAtHealthWithoutAnyPost(string? actual)
    {
        using var handler = new OpenCodeApiFixture(AppContext.BaseDirectory) { Version = actual };
        using var http = new HttpClient(handler);
        var client = new OpenCodeClient(HistoricalRuntimeAccess.Instance, http, Options("1.19.2"));
        var result = await new ExternalWorkflow(HistoricalRuntimeAccess.Instance, client).RunAsync(Request());
        Assert.Equal("OpenCodeVersionMismatch", result.Result.ErrorCode);
        Assert.Equal(ExternalExecutionStatus.Failed, result.Result.Status);
        Assert.Equal(new[] { "/global/health" }, handler.Routes); Assert.Equal(0, handler.Submissions);
        var handle = client.PrepareExecution();
        Assert.Equal("OpenCodeVersionNotValidated", (await Assert.ThrowsAsync<ExternalHarnessException>(() =>
            client.CreateAsync(Request(), "run", handle, default))).Code);
        Assert.Equal("OpenCodeVersionNotValidated", (await Assert.ThrowsAsync<ExternalHarnessException>(() =>
            client.SubmitAsync(Request(), handle, default))).Code);
        Assert.Single(handler.Routes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.19.0")]
    [InlineData("1.19.3")]
    [InlineData("2.0.0")]
    [InlineData(" 1.19.2")]
    public void UnsupportedPinRejectedBeforeTransport(string version)
        => Assert.Throws<ArgumentException>(() => Options(version).Validate());

    [Fact]
    public async Task FailedSchemaValidationCannotEnableDirectCreate()
    {
        using var handler = new OpenCodeApiFixture(AppContext.BaseDirectory) { Version = "1.19.2", OmitPermissionApi = true };
        using var http = new HttpClient(handler);
        var client = new OpenCodeClient(HistoricalRuntimeAccess.Instance, http, Options("1.19.2"));
        await Assert.ThrowsAsync<ExternalHarnessException>(() => client.ValidateAsync(Request(), default));
        Assert.Equal("OpenCodeVersionNotValidated", (await Assert.ThrowsAsync<ExternalHarnessException>(() =>
            client.CreateAsync(Request(), "run", client.PrepareExecution(), default))).Code);
        Assert.Equal(0, handler.Submissions); Assert.DoesNotContain(handler.Routes, x => x.StartsWith("/session"));
    }

    [Fact]
    public void PinnedScopesDifferFromLegacyAndOtherPinnedVersionWithoutChangingLegacyDefault()
    {
        using var http = new HttpClient(new OpenCodeApiFixture(AppContext.BaseDirectory));
        var legacy = new OpenCodeClient(HistoricalRuntimeAccess.Instance, http, Options(null));
        var same = new OpenCodeClient(HistoricalRuntimeAccess.Instance, http, Options(null));
        var pinned = new OpenCodeClient(HistoricalRuntimeAccess.Instance, http, Options("1.19.2"));
        var oldPin = new OpenCodeClient(HistoricalRuntimeAccess.Instance, http, Options("1.18.35"));
        Assert.Equal(legacy.Scope, same.Scope); Assert.NotEqual(legacy.Scope, pinned.Scope); Assert.NotEqual(pinned.Scope, oldPin.Scope);
        Assert.Null(Options(null).ExpectedVersion);
    }
}
