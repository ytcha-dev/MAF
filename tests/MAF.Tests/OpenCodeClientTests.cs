using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using MAF.Core;
using MAF.Workflows.External;
using MAF.Workflows.External.OpenCode;

namespace MAF.Tests;

public sealed class OpenCodeClientTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("maf-http-").FullName;
    private ExternalTaskRequest Request() => new("http-task", root, "Read the fixture.", TimeSpan.FromSeconds(3));
    private OpenCodeOptions Options(string? password = null) => new(new Uri("http://127.0.0.1:4096"), root,
        AllowLoopbackHttp: true, Password: password);

    [Fact]
    public async Task VerifiedPolicyAndCorrelatedCompletionUseAsyncEndpoint()
    {
        using var handler = new OpenCodeApiFixture(root);
        using var http = new HttpClient(handler);
        var client = new OpenCodeClient(HistoricalRuntimeAccess.Instance, http, Options());
        var run = await new ExternalWorkflow(HistoricalRuntimeAccess.Instance, client, TimeSpan.FromMilliseconds(5)).RunAsync(Request());
        Assert.Equal(ExternalExecutionStatus.Completed, run.Result.Status);
        Assert.Equal(1, handler.Submissions);
        Assert.Contains(handler.Routes, route => route.Contains("prompt_async"));
        Assert.DoesNotContain(handler.Routes, route => route.EndsWith("/shell") || route.Contains("/reply"));
        Assert.Contains(handler.PermissionRules, rule => rule.GetProperty("permission").GetString() == "*"
            && rule.GetProperty("action").GetString() == "deny");
        Assert.True(run.Result.Requests > 0);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(302)]
    public async Task DiscoveryHttpErrorsFailBeforeLaunchingAndDoNotExposeBody(int status)
    {
        using var handler = new OpenCodeApiFixture(root) { HealthStatus = status };
        using var http = new HttpClient(handler);
        var result = (await new ExternalWorkflow(HistoricalRuntimeAccess.Instance, new OpenCodeClient(HistoricalRuntimeAccess.Instance, http, Options())).RunAsync(Request())).Result;
        Assert.Equal(ExternalExecutionStatus.Failed, result.Status);
        Assert.Equal("HTTP" + status, result.ErrorCode);
        Assert.DoesNotContain("private-response", result.ToJson());
        Assert.Equal(0, handler.Submissions);
    }

    [Fact]
    public async Task SessionPermissionMismatchPreservesIdentityAndNeverSubmits()
    {
        using var handler = new OpenCodeApiFixture(root) { StripSessionRules = true };
        using var http = new HttpClient(handler);
        var result = (await new ExternalWorkflow(HistoricalRuntimeAccess.Instance, new OpenCodeClient(HistoricalRuntimeAccess.Instance, http, Options())).RunAsync(Request())).Result;
        Assert.Equal(ExternalExecutionStatus.Failed, result.Status);
        Assert.Equal("SessionPolicyMismatch", result.ErrorCode);
        Assert.NotNull(result.Handle.ExternalSessionId);
        Assert.Equal(0, handler.Submissions);
    }

    [Fact]
    public async Task InvalidSessionShapePreservesIdentityWithoutSubmitting()
    {
        using var handler = new OpenCodeApiFixture(root) { InvalidSessionShape = true };
        using var http = new HttpClient(handler);
        var result = (await new ExternalWorkflow(HistoricalRuntimeAccess.Instance, new OpenCodeClient(HistoricalRuntimeAccess.Instance, http, Options())).RunAsync(Request())).Result;
        Assert.Equal(ExternalExecutionStatus.Unknown, result.Status);
        Assert.NotNull(result.Handle.ExternalSessionId);
        Assert.Equal("InvalidSessionResponse", result.ErrorCode);
        Assert.Equal(0, handler.Submissions);
    }

    [Fact]
    public async Task TransportTimeoutBeforeCreationIsTypedFailure()
    {
        using var handler = new OpenCodeApiFixture(root) { DelayHealth = true };
        using var http = new HttpClient(handler);
        var result = (await new ExternalWorkflow(HistoricalRuntimeAccess.Instance, new OpenCodeClient(HistoricalRuntimeAccess.Instance, http, Options() with { RequestTimeoutSeconds = 1 }))
            .RunAsync(Request())).Result;
        Assert.Equal(ExternalExecutionStatus.Failed, result.Status);
        Assert.Equal("TransportTimeout", result.ErrorCode);
        Assert.Equal(0, handler.Submissions);
    }

    [Fact]
    public async Task ReadApprovalProfileIsStricterAndNeverGrantsWritePermissions()
    {
        using var handler = new OpenCodeApiFixture(root) { PendingPermission = true };
        using var http = new HttpClient(handler);
        var result = (await new ExternalWorkflow(HistoricalRuntimeAccess.Instance, new OpenCodeClient(HistoricalRuntimeAccess.Instance, http, Options() with { RequireReadApproval = true }))
            .RunAsync(Request())).Result;
        Assert.Equal(ExternalExecutionStatus.WaitingForApproval, result.Status);
        Assert.Contains(handler.PermissionRules, rule => rule.GetProperty("permission").GetString() == "read"
            && rule.GetProperty("action").GetString() == "ask");
        Assert.DoesNotContain(handler.PermissionRules, rule => rule.GetProperty("permission").GetString() == "edit"
            && rule.GetProperty("action").GetString() == "allow");
    }

    [Fact]
    public async Task MissingSchemaCapabilityFailsClosed()
    {
        using var handler = new OpenCodeApiFixture(root) { OmitPermissionApi = true };
        using var http = new HttpClient(handler);
        var result = (await new ExternalWorkflow(HistoricalRuntimeAccess.Instance, new OpenCodeClient(HistoricalRuntimeAccess.Instance, http, Options())).RunAsync(Request())).Result;
        Assert.Equal("UnsupportedServerApi", result.ErrorCode);
        Assert.Equal(0, handler.Submissions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PermissionAndQuestionRequestsAreSurfacedWithoutReply(bool permission)
    {
        using var handler = new OpenCodeApiFixture(root) { PendingPermission = permission, PendingQuestion = !permission };
        using var http = new HttpClient(handler);
        var result = (await new ExternalWorkflow(HistoricalRuntimeAccess.Instance, new OpenCodeClient(HistoricalRuntimeAccess.Instance, http, Options())).RunAsync(Request())).Result;
        Assert.Equal(ExternalExecutionStatus.WaitingForApproval, result.Status);
        Assert.DoesNotContain(handler.Routes, route => route.Contains("reply"));
    }

    [Theory]
    [InlineData("future-state")]
    [InlineData("malformed-json")]
    [InlineData("malformed-shape")]
    public async Task UnknownAndInvalidResponsesAreNotCompletion(string state)
    {
        using var handler = new OpenCodeApiFixture(root) { State = state };
        using var http = new HttpClient(handler);
        var result = (await new ExternalWorkflow(HistoricalRuntimeAccess.Instance, new OpenCodeClient(HistoricalRuntimeAccess.Instance, http, Options()), TimeSpan.FromMilliseconds(5)).RunAsync(Request())).Result;
        Assert.Equal(ExternalExecutionStatus.Unknown, result.Status);
        Assert.Equal(1, handler.Submissions);
    }

    [Fact]
    public async Task AmbiguousPostAndTransientGetReconcileOriginalSession()
    {
        using var handler = new OpenCodeApiFixture(root) { LoseSubmitResponse = true, LoseFirstObservation = true };
        using var http = new HttpClient(handler);
        var run = await new ExternalWorkflow(HistoricalRuntimeAccess.Instance, new OpenCodeClient(HistoricalRuntimeAccess.Instance, http, Options()), TimeSpan.FromMilliseconds(5)).RunAsync(Request());
        Assert.Equal(ExternalExecutionStatus.Completed, run.Result.Status);
        Assert.Equal(1, handler.Submissions);
        Assert.Contains(run.Result.Trace, item => item.Kind == "ReconnectOriginalSession");
    }

    [Fact]
    public async Task TerminalTextIsRedactedAndBounded()
    {
        const string password = "private-server-value";
        using var handler = new OpenCodeApiFixture(root) { Output = "value " + password + new string('x', 400) };
        using var http = new HttpClient(handler);
        var options = Options(password) with { SummaryLimit = 100 };
        var result = (await new ExternalWorkflow(HistoricalRuntimeAccess.Instance, new OpenCodeClient(HistoricalRuntimeAccess.Instance, http, options)).RunAsync(Request())).Result;
        Assert.Equal(ExternalExecutionStatus.Partial, result.Status);
        Assert.DoesNotContain(password, result.ToJson());
        Assert.Equal(100, result.Summary.Length);
        Assert.NotNull(handler.Authorization);
    }

    [Fact]
    public async Task ResponseSizeAndPaginationLimitsAreExplicit()
    {
        using var oversized = new OpenCodeApiFixture(root) { Output = new string('x', 3000) };
        using var http = new HttpClient(oversized);
        var client = new OpenCodeClient(HistoricalRuntimeAccess.Instance, http, Options() with { ResponseLimitBytes = 2500 });
        var result = (await new ExternalWorkflow(HistoricalRuntimeAccess.Instance, client, TimeSpan.FromMilliseconds(5)).RunAsync(Request())).Result;
        Assert.Equal(ExternalExecutionStatus.Unknown, result.Status);
        Assert.Equal("ResponseSizeLimit", result.ErrorCode);
        using var pages = new OpenCodeApiFixture(root) { FullMessagePage = true };
        using var pageHttp = new HttpClient(pages);
        var paged = (await new ExternalWorkflow(HistoricalRuntimeAccess.Instance, new OpenCodeClient(HistoricalRuntimeAccess.Instance, pageHttp, Options())).RunAsync(Request())).Result;
        Assert.Equal(ExternalExecutionStatus.Partial, paged.Status);
        Assert.Contains(paged.Limitations, item => item.Contains("collection limit"));
        Assert.Contains(pages.Routes, route => route.Contains("before="));
    }

    [Theory]
    [InlineData("tool-calls")]
    [InlineData("foreign-parent")]
    [InlineData("incomplete")]
    public async Task IdleAloneAndUnrelatedOrToolMessagesAreNotCompleted(string mode)
    {
        using var handler = new OpenCodeApiFixture(root) { MessageMode = mode };
        using var http = new HttpClient(handler);
        var client = new OpenCodeClient(HistoricalRuntimeAccess.Instance, http, Options());
        var request = Request();
        await client.ValidateAsync(request, default);
        var handle = await client.CreateAsync(request, "workflow", new("execution", client.Provider), default);
        await client.SubmitAsync(request, handle, default);
        Assert.Equal(ExternalExecutionStatus.Running, (await client.ObserveAsync(handle, default)).Status);
    }

    [Fact]
    public void RemoteOrUnapprovedHttpOriginsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => (Options() with { AllowLoopbackHttp = false }).Validate());
        Assert.Throws<ArgumentException>(() => (Options() with { BaseUri = new Uri("https://example.invalid") }).Validate());
        Assert.Throws<ArgumentException>(() => (Options() with { BaseUri = new Uri("http://user:secret@localhost:4096") }).Validate());
    }

    public void Dispose() => Directory.Delete(root);
}

internal sealed class OpenCodeApiFixture(string root) : HttpMessageHandler
{
    public ConcurrentQueue<string> Routes { get; } = new();
    public JsonElement[] PermissionRules { get; private set; } = [];
    public int Submissions { get; private set; }
    public int HealthStatus { get; init; } = 200;
    public bool StripSessionRules { get; init; }
    public bool InvalidSessionShape { get; init; }
    public bool DelayHealth { get; init; }
    public bool OmitPermissionApi { get; init; }
    public bool PendingPermission { get; init; }
    public bool PendingQuestion { get; init; }
    public bool LoseSubmitResponse { get; init; }
    public bool LoseFirstObservation { get; init; }
    public bool FullMessagePage { get; init; }
    public string Output { get; init; } = "Fixture contains README.md and Program.cs.";
    public string State { get; init; } = "idle";
    public string MessageMode { get; init; } = "stop";
    public string? Authorization { get; private set; }
    private string? messageId;
    private bool readLost;

    private static HttpResponseMessage Json(object value, int status = 200) => new((HttpStatusCode)status)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };

    private object Session() => new
    {
        id = "ses_fixture",
        directory = InvalidSessionShape ? "invalid\0directory" : root,
        permission = StripSessionRules ? Array.Empty<JsonElement>() : PermissionRules
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        Routes.Enqueue(request.RequestUri.PathAndQuery);
        Authorization = request.Headers.Authorization?.ToString();
        if (path == "/global/health")
        {
            if (DelayHealth)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return HealthStatus == 200 ? Json(new { healthy = true, version = "fixture" })
                : Json(new { error = "private-response" }, HealthStatus);
        }
        if (path == "/doc")
        {
            var paths = new Dictionary<string, object>();
            foreach (var (route, method) in new[]
            {
                ("/session/{sessionID}", "get"), ("/session/{sessionID}/prompt_async", "post"),
                ("/session/status", "get"), ("/session/{sessionID}/message", "get"),
                ("/session/{sessionID}/abort", "post"), ("/question", "get"), ("/agent", "get")
            })
            {
                paths[route] = new Dictionary<string, object> { [method] = new { } };
            }
            if (!OmitPermissionApi)
            {
                paths["/permission"] = new { get = new { } };
            }
            paths["/session"] = new
            {
                post = new
                {
                    requestBody = new
                    {
                        content = new Dictionary<string, object>
                        {
                            ["application/json"] = new
                            {
                                schema = new
                                {
                                    properties = new
                                    {
                                        permission = new Dictionary<string, string>
                                        { ["$ref"] = "#/components/schemas/PermissionRuleset" }
                                    }
                                }
                            }
                        }
                    }
                }
            };
            return Json(new { paths, components = new { schemas = new { PermissionRuleset = new { type = "array" } } } });
        }
        if (path == "/agent")
        {
            return Json(new[] { new { name = "maf_readonly", permission = new[] { new { permission = "*", pattern = "*", action = "deny" } } } });
        }
        if (path == "/session" && request.Method == HttpMethod.Post)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            PermissionRules = body.RootElement.GetProperty("permission").EnumerateArray().Select(rule => rule.Clone()).ToArray();
            return Json(Session());
        }
        if (path.EndsWith("/prompt_async"))
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            messageId = body.RootElement.GetProperty("messageID").GetString();
            Submissions++;
            if (LoseSubmitResponse)
            {
                throw new HttpRequestException("private transport detail");
            }
            return new(HttpStatusCode.NoContent);
        }
        if (path == "/session/ses_fixture")
        {
            if (Submissions > 0 && LoseFirstObservation && !readLost)
            {
                readLost = true;
                throw new HttpRequestException("private transport detail");
            }
            return Json(Session());
        }
        if (path is "/permission" or "/question")
        {
            return Json((path == "/permission" && PendingPermission || path == "/question" && PendingQuestion)
                ? new[] { new { sessionID = "ses_fixture" } } : Array.Empty<object>());
        }
        if (path == "/session/status")
        {
            if (State == "malformed-json")
            {
                return new(HttpStatusCode.OK) { Content = new StringContent("not-json") };
            }
            if (State == "malformed-shape")
            {
                return Json(new[] { "invalid" });
            }
            return Json(new Dictionary<string, object> { ["ses_fixture"] = new { type = State } });
        }
        if (path.EndsWith("/message"))
        {
            var time = new Dictionary<string, long> { ["created"] = 1000 };
            if (MessageMode != "incomplete")
            {
                time["completed"] = 2000;
            }
            var message = new
            {
                info = new
                {
                    id = "msg_reply",
                    sessionID = "ses_fixture",
                    role = "assistant",
                    parentID = MessageMode == "foreign-parent" ? "msg_other" : messageId,
                    time,
                    finish = MessageMode == "tool-calls" ? "tool-calls" : "stop"
                },
                parts = new[] { new { type = "text", text = Output } }
            };
            return Json(Enumerable.Repeat(message, FullMessagePage ? 100 : 1).ToArray());
        }
        if (path.EndsWith("/abort"))
        {
            return Json(true);
        }
        return Json(new { error = "Unexpected fixture route" }, 404);
    }
}
