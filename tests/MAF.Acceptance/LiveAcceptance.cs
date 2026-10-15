using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using MAF.Core;

// Transparent, test-owned request accounting. Never logs headers, bodies or transcripts.
sealed class CountingProxy : IAsyncDisposable
{
    readonly HttpListener listener = new();
    readonly HttpClient http = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
    readonly CancellationTokenSource stop = new();
    readonly List<Task> requests = [];
    readonly Task loop;
    int sessions, prompts;
    public bool Unavailable;
    public int Sessions => Volatile.Read(ref sessions);
    public int Prompts => Volatile.Read(ref prompts);
    public string Origin { get; }
    public static int Port() { var p = new TcpListener(IPAddress.Loopback, 0); p.Start(); var port = ((IPEndPoint)p.LocalEndpoint).Port; p.Stop(); return port; }
    public CountingProxy(string upstream)
    {
        Origin = $"http://127.0.0.1:{Port()}/";
        http.BaseAddress = new Uri(upstream);
        listener.Prefixes.Add(Origin); listener.Start(); loop = Listen();
    }
    async Task Listen()
    {
        try { while (!stop.IsCancellationRequested) { var c = await listener.GetContextAsync().WaitAsync(stop.Token); requests.Add(Forward(c)); } }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (HttpListenerException) when (stop.IsCancellationRequested) { }
    }
    async Task Forward(HttpListenerContext c)
    {
        try
        {
            if (Volatile.Read(ref Unavailable)) { c.Response.StatusCode = 503; c.Response.Close(); return; }
            var path = c.Request.Url!.AbsolutePath;
            if (c.Request.HttpMethod == "POST" && path == "/session") Interlocked.Increment(ref sessions);
            if (c.Request.HttpMethod == "POST" && path.EndsWith("/prompt_async", StringComparison.Ordinal)) Interlocked.Increment(ref prompts);
            using var r = new HttpRequestMessage(new HttpMethod(c.Request.HttpMethod), c.Request.Url.PathAndQuery);
            if (c.Request.HasEntityBody)
            {
                r.Content = new StreamContent(c.Request.InputStream);
                r.Content.Headers.TryAddWithoutValidation("Content-Type", c.Request.ContentType);
            }
            using var response = await http.SendAsync(r, HttpCompletionOption.ResponseHeadersRead, stop.Token);
            c.Response.StatusCode = (int)response.StatusCode;
            c.Response.ContentType = response.Content.Headers.ContentType?.ToString();
            await response.Content.CopyToAsync(c.Response.OutputStream, stop.Token);
            c.Response.Close();
        }
        catch (Exception e) when (e is HttpRequestException or HttpListenerException or IOException or OperationCanceledException)
        { c.Response.Abort(); }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); listener.Stop(); await loop;
        await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));
        http.Dispose(); listener.Close(); stop.Dispose();
    }
}

static class LiveAcceptance
{
    public static async Task Run(string host, string runDirectory, string launcher, string model)
    {
        host = Path.GetFullPath(host); var root = Path.GetFullPath(runDirectory);
        Acceptance.Require(!Directory.Exists(root) && File.Exists(host), "New run directory and built Host required");
        var executable = OpenCodeLauncher.ResolveNative(launcher);
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "README.md"), "# Synthetic greeting demo\nProgram.cs prints a greeting. This fixture has no confidential data.\n");
        await File.WriteAllTextAsync(Path.Combine(source, "Program.cs"), "Console.WriteLine(\"Synthetic greeting\");\n");
        var configObject = new Dictionary<string,object> { ["$schema"] = "https://opencode.ai/config.json" };
        var configFields = JsonSerializer.SerializeToElement(new { model, enabled_providers = new[] { "opencode" }, autoupdate = false,
            permission = new Dictionary<string,string> { ["*"] = "deny", ["read"] = "allow", ["glob"] = "allow", ["grep"] = "allow", ["external_directory"] = "deny" },
            agent = new { maf_readonly = new { description = "Synthetic read-only lifecycle validation", mode = "primary", model,
                permission = new Dictionary<string,string> { ["*"] = "deny", ["read"] = "allow", ["glob"] = "allow", ["grep"] = "allow", ["external_directory"] = "deny" } } },
            lsp = false, formatter = false, share = "disabled" });
        foreach (var field in configFields.EnumerateObject()) configObject[field.Name] = field.Value;
        var config = JsonSerializer.Serialize(configObject);
        await File.WriteAllTextAsync(Path.Combine(source, "opencode.json"), config);
        var env = new Dictionary<string,string>();
        foreach (var key in new[] { "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "XDG_STATE_HOME" })
        { var directory = key == "XDG_DATA_HOME" ? Path.Combine(root, ".local", "share") : Path.Combine(root, key); Directory.CreateDirectory(directory); env[key] = directory; }
        env["OPENCODE_CONFIG_CONTENT"] = config;
        env["OPENCODE_DISABLE_AUTOUPDATE"] = "1";
        env["OPENCODE_DISABLE_SHARE"] = "1";
        async Task Git(params string[] a)
        {
            // Absolute git resolved through PATH by the caller's installed toolchain.
            using var git = new Child(["-C", source, ..a], executable: "git.exe");
            var result = await git.Finish(); Acceptance.Require(result.Exit == 0, "Synthetic Git operation failed");
        }
        await Git("init", "--quiet"); await Git("add", "--", "README.md", "Program.cs", "opencode.json");
        await Git("-c", "user.name=Synthetic Validation", "-c", "user.email=validation@example.invalid", "-c", "core.hooksPath=NUL", "commit", "--quiet", "-m", "synthetic baseline");
        async Task<object> Manifest()
        {
            var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories).Where(p => !Path.GetRelativePath(source, p).StartsWith(".git" + Path.DirectorySeparatorChar))
                .OrderBy(p => p, StringComparer.Ordinal).ToDictionary(p => Path.GetRelativePath(source, p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
            var index = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(source, ".git", "index"))));
            using var git = new Child(["-C", source, "-c", "core.fsmonitor=false", "--no-optional-locks", "status", "--porcelain=v1", "--untracked-files=all"], executable: "git.exe");
            var status = await git.Finish(); Acceptance.Require(status.Exit == 0, "Status failed");
            return new { files, index, status = status.Output };
        }
        var baseline = JsonSerializer.Serialize(await Manifest());
        var port = CountingProxy.Port(); var upstream = $"http://127.0.0.1:{port}/";
        using var server = new Child(["serve", "--pure", "--hostname", "127.0.0.1", "--port", port.ToString(), "--log-level", "ERROR"], env, executable: executable, workingDirectory: source);
        using var http = new HttpClient { BaseAddress = new Uri(upstream), Timeout = TimeSpan.FromSeconds(5) };
        await Acceptance.Until(async () => { try { using var h = await http.GetAsync("global/health"); return h.IsSuccessStatusCode; } catch (HttpRequestException) { return false; } });
        using var health = JsonDocument.Parse(await http.GetStringAsync("global/health"));
        await using var proxy = new CountingProxy(upstream);
        var hostEnv = new Dictionary<string,string> { ["OPENCODE_BASE_URL"] = proxy.Origin, ["OPENCODE_PROJECT_ROOT"] = source,
            ["OPENCODE_AGENT"] = "maf_readonly", ["OPENCODE_ALLOW_LOOPBACK_HTTP"] = "1" };
        var evidence = new List<object>();
        await File.WriteAllTextAsync(Path.Combine(root, "baseline.json"), baseline);
        foreach (var variant in new[] { "active", "completed-while-stopped", "unavailable" })
        {
            var task = "live-" + variant; var db = Path.Combine(root, task + ".db");
            var initialSessions = proxy.Sessions; var initialPrompts = proxy.Prompts;
            string[] HostArgs(bool recover, int timeout = 120) => [host, "--project", source, "--execution-db", db,
                recover ? "--recover" : "--task-id", task, ..(recover ? Array.Empty<string>() : new[] { "--external-task", "Read README.md and Program.cs using read tools. Explain the exact greeting and cite both filenames. Do not run commands or change files. Give a final answer." }),
                "--external-timeout", timeout.ToString(), "--output", "json"];
            using var original = new Child(HostArgs(false), hostEnv);
            DurableExecution? before = null;
            await Acceptance.Until(async () => { before = await Evidence.Snapshot(db); return before?.State == DurableExecutionState.Running || original.Process.HasExited; });
            if (before?.Handle.ExternalSessionId is null)
            {
                var failure = await original.Finish();
                await File.WriteAllTextAsync(Path.Combine(root, "launch-failure.json"), failure.Output);
                throw new InvalidOperationException("Live identity not persisted: " + failure.Output);
            }
            var query = "?directory=" + Uri.EscapeDataString(source);
            async Task<JsonDocument> Status() => JsonDocument.Parse(await http.GetStringAsync("session/status" + query));
            var active = false;
            await Acceptance.Until(async () =>
            {
                using var status = await Status();
                active = status.RootElement.TryGetProperty(before.Handle.ExternalSessionId, out var state)
                    && state.GetProperty("type").GetString() == "busy" && !original.Process.HasExited;
                return active || original.Process.HasExited;
            });
            if (!active)
            {
                var failure = await original.Finish();
                await File.WriteAllTextAsync(Path.Combine(root, "no-active-window.json"), failure.Output);
                using var session = JsonDocument.Parse(await http.GetStringAsync("session/" + before.Handle.ExternalSessionId + query));
                var policy = new { fields = session.RootElement.EnumerateObject().Select(p => p.Name).ToArray(),
                    directoryMatches = session.RootElement.TryGetProperty("directory", out var d) && d.GetString() == source,
                    relativeDirectory = Path.GetRelativePath(root, session.RootElement.GetProperty("directory").GetString()!),
                    permission = session.RootElement.TryGetProperty("permission", out var p) ? p : default };
                Console.WriteLine(JsonSerializer.Serialize(policy));
                throw new InvalidOperationException("Live active crash window not reproduced: " + failure.Output);
            }
            original.Kill(); await original.Finish();
            if (variant == "completed-while-stopped")
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                while (true)
                {
                    using var s = await Status();
                    if (!s.RootElement.TryGetProperty(before.Handle.ExternalSessionId, out var v) || v.GetProperty("type").GetString() == "idle") break;
                    await Task.Delay(250, deadline.Token);
                }
            }
            proxy.Unavailable = variant == "unavailable";
            using var recovered = new Child(HostArgs(true, variant == "active" ? 1 : 120), hostEnv);
            var result = await recovered.Finish(150);
            var parsed = JsonDocument.Parse(result.Output).RootElement.GetProperty("result");
            var firstStatus = parsed.GetProperty("status").GetString();
            var firstErrorCode = parsed.GetProperty("errorCode").GetString();
            var firstSnapshot = await Evidence.Snapshot(db);
            using var afterRecoveryStatus = await Status();
            var stillBusyAfterRecovery = afterRecoveryStatus.RootElement.TryGetProperty(before.Handle.ExternalSessionId, out var remote)
                && remote.GetProperty("type").GetString() == "busy";
            Acceptance.Require(variant != "unavailable" || firstStatus == "Unknown" && result.Exit == 4, "Unavailable provider was not Unknown");
            Acceptance.Require(parsed.GetProperty("handle").Deserialize<ExternalExecutionHandle>(ExternalTaskResult.JsonOptions) == before.Handle, "Live recovered handle changed");
            proxy.Unavailable = false;
            object finalProcess = recovered.Identity;
            if (firstStatus == "Unknown")
            {
                using var later = new Child(HostArgs(true), hostEnv);
                result = await later.Finish(150); finalProcess = later.Identity;
                parsed = JsonDocument.Parse(result.Output).RootElement.GetProperty("result");
            }
            var finalStatus = parsed.GetProperty("status").GetString();
            Acceptance.Require(parsed.GetProperty("taskId").GetString() == task && parsed.GetProperty("workflowRunId").GetString() == before.WorkflowRunId
                && parsed.GetProperty("handle").Deserialize<ExternalExecutionHandle>(ExternalTaskResult.JsonOptions) == before.Handle,
                "Final live result correlation mismatch");
            var after = await Evidence.Snapshot(db);
            Acceptance.Require(after!.Handle == before.Handle && after.WorkflowRunId == before.WorkflowRunId && after.RequestFingerprint == before.RequestFingerprint,
                "Live durable identity changed");
            Acceptance.Require(proxy.Sessions - initialSessions == 1 && proxy.Prompts - initialPrompts == 1, "Duplicate live submission");
            var endManifest = JsonSerializer.Serialize(await Manifest());
            var unchanged = baseline == endManifest;
            await File.WriteAllTextAsync(Path.Combine(root, task + "-manifest-after.json"), endManifest);
            // Extract only non-secret provider/model IDs, never retain prompt or tool transcript.
            using var messages = JsonDocument.Parse(await http.GetStringAsync("session/" + before.Handle.ExternalSessionId + "/message" + query + "&limit=100"));
            var models = messages.RootElement.EnumerateArray().Select(x => x.GetProperty("info"))
                .Where(x => x.TryGetProperty("providerID", out _) && x.TryGetProperty("modelID", out _))
                .Select(x => new { provider = x.GetProperty("providerID").GetString(), model = x.GetProperty("modelID").GetString() }).Distinct().ToArray();
            evidence.Add(new { variant, server = server.Identity, version = health.RootElement.GetProperty("version").GetString(), model, observedModels = models,
                original = original.Identity, recovered = recovered.Identity, finalProcess, checkpoint = before, recoveredSnapshot = after,
                activeAtCrash = active, firstStatus, firstErrorCode, firstSnapshot, stillBusyAfterRecovery, finalStatus, exit = result.Exit,
                sessionPosts = proxy.Sessions - initialSessions, promptPosts = proxy.Prompts - initialPrompts,
                sourceIndexStatusUnchanged = unchanged, outcome = !unchanged ? "Failed" : finalStatus == "Completed" && result.Exit == 0 ? "Passed" : "Not Demonstrated" });
            await File.WriteAllTextAsync(Path.Combine(root, "live-evidence.json"), JsonSerializer.Serialize(evidence, ExternalTaskResult.JsonOptions));
            Acceptance.Require(unchanged, "Live source/index/status changed; preserve evidence");
            Console.WriteLine($"Live {variant}: first={firstStatus}, final={finalStatus}, session/prompt POST=1/1");
        }
    }
}
