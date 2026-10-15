using System.Net;
using System.Net.Sockets;
using System.Text.Json;

// Test-owned loopback server. No models, filesystem tools, credentials or approval replies.
sealed class ControlledProvider : IAsyncDisposable
{
    readonly HttpListener listener = new();
    readonly string root;
    readonly bool holdResponse;
    readonly CancellationTokenSource stop = new();
    readonly List<Task> requests = [];
    readonly Task loop;
    JsonElement rules;
    string? message;
    int sessions, prompts, aborts, observations;
    string mode = "busy";
    public string Mode { get => Volatile.Read(ref mode); set => Volatile.Write(ref mode, value); }
    public int Sessions => Volatile.Read(ref sessions);
    public int Prompts => Volatile.Read(ref prompts);
    public int Aborts => Volatile.Read(ref aborts);
    public int Observations => Volatile.Read(ref observations);
    public TaskCompletionSource Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string Origin { get; }
    public ControlledProvider(string root, bool holdResponse)
    {
        this.root = root; this.holdResponse = holdResponse;
        // Port is acquired by the listener before any child starts. A bind race fails the run.
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        Origin = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(Origin); listener.Start();
        loop = Listen();
    }
    async Task Listen()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(stop.Token);
                requests.Add(Respond(context));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (HttpListenerException) when (stop.IsCancellationRequested) { }
    }
    object Session() => new { id = "ses_controlled", directory = root, permission = rules };
    async Task Respond(HttpListenerContext c)
    {
        try
        {
            var path = c.Request.Url!.AbsolutePath;
            object body;
            if (Mode == "unavailable") { c.Response.StatusCode = 503; body = new { error = "synthetic outage" }; }
            else if (path == "/global/health") body = new { healthy = true, version = "controlled-acceptance-v1" };
            else if (path == "/doc") body = Schema();
            else
            {
                Acceptance.Require(c.Request.QueryString["directory"] == root, "Provider directory mismatch");
                if (path == "/agent") body = new[] { new { name = "maf_readonly", permission = new[] { new { permission = "*", pattern = "*", action = "deny" } } } };
                else if (path == "/session" && c.Request.HttpMethod == "POST")
                {
                    using var json = await JsonDocument.ParseAsync(c.Request.InputStream);
                    rules = json.RootElement.GetProperty("permission").Clone();
                    Acceptance.Require(rules.EnumerateArray().First().GetProperty("action").GetString() == "deny", "Missing deny-default");
                    Interlocked.Increment(ref sessions); body = Session();
                }
                else if (path == "/session/ses_controlled/prompt_async" && c.Request.HttpMethod == "POST")
                {
                    using var json = await JsonDocument.ParseAsync(c.Request.InputStream);
                    message = json.RootElement.GetProperty("messageID").GetString();
                    Interlocked.Increment(ref prompts);
                    Accepted.TrySetResult();
                    if (holdResponse) await Task.Delay(Timeout.Infinite, stop.Token); // accepted, acknowledgment never delivered
                    c.Response.StatusCode = 204; c.Response.Close(); return;
                }
                else if (path == "/session/ses_controlled") body = Session();
                else if (path == "/permission") body = Mode == "permission" ? new object[] { new { sessionID = "ses_controlled" } } : [];
                else if (path == "/question") body = Array.Empty<object>();
                else if (path == "/session/status") body = new Dictionary<string, object> { ["ses_controlled"] = new { type = Mode == "busy" ? "busy" : "idle" } };
                else if (path == "/session/ses_controlled/message")
                {
                    Interlocked.Increment(ref observations);
                    body = Mode == "busy" ? Array.Empty<object>() : new object[] { new {
                        info = new { id = "msg_reply", role = "assistant", sessionID = "ses_controlled", parentID = message,
                            time = new { created = 1000, completed = 2000 }, finish = "stop",
                            error = Mode == "cancelled" ? new { name = "MessageAbortedError" } : null },
                        parts = new[] { new { type = "text", text = "Synthetic recovered result." } } } };
                }
                else if (path == "/session/ses_controlled/abort") { Interlocked.Increment(ref aborts); body = true; }
                else throw new InvalidOperationException("Unexpected provider route: " + path);
            }
            c.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(c.Response.OutputStream, body, cancellationToken: stop.Token);
            c.Response.Close();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { c.Response.Abort(); }
        catch (HttpListenerException) { c.Response.Abort(); }
    }
    static object Schema()
    {
        var paths = new Dictionary<string, object>();
        foreach (var (path, method) in new[] { ("/session/{sessionID}", "get"), ("/session/{sessionID}/prompt_async", "post"),
            ("/session/status", "get"), ("/session/{sessionID}/message", "get"), ("/session/{sessionID}/abort", "post"),
            ("/permission", "get"), ("/question", "get"), ("/agent", "get") })
            paths[path] = new Dictionary<string,object> { [method] = new { } };
        paths["/session"] = new { post = new { requestBody = new { content = new Dictionary<string,object> {
            ["application/json"] = new { schema = new { properties = new { permission = new Dictionary<string,string> {
                ["$ref"] = "#/components/schemas/PermissionRuleset" } } } } } } } };
        return new { paths, components = new { schemas = new { PermissionRuleset = new { type = "array" } } } };
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); listener.Stop();
        await loop;
        await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));
        listener.Close(); stop.Dispose();
    }
}
