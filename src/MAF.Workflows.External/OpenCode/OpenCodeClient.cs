using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MAF.Core;
using MAF.Core.Tools;
using MAF.Core.Policy;

namespace MAF.Workflows.External.OpenCode;

public sealed class OpenCodeClient : IResumableExternalHarnessClient
{
    private sealed record PermissionRule(string Permission, string Pattern, string Action);
    private sealed record Binding(string TaskId, string Root, ExternalExecutionHandle Handle);
    private readonly IRuntimeAccess access;
    private readonly HttpClient http;
    private readonly OpenCodeOptions options;
    private readonly ConcurrentDictionary<string, Binding> bindings = new();
    private readonly ConcurrentDictionary<string, int> counts = new();
    private readonly ConcurrentDictionary<string, byte> submitted = new();
    private readonly string approvedRoot;
    private readonly PermissionRule[] sessionRules;
    public string Provider => "OpenCode";
    public string Scope => DurableExecution.Hash(System.Text.Json.JsonSerializer.Serialize(new
    {
        Origin = options.BaseUri.GetLeftPart(UriPartial.Authority), Root = DurableExecution.NormalizeRoot(approvedRoot),
        options.Agent, options.RequireReadApproval, Rules = sessionRules
    }));
    public ExternalExecutionHandle PrepareExecution() => new(Guid.NewGuid().ToString("N"), Provider,
        ExternalRequestId: NewMessageId());
    private static string NewMessageId() => "msg_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("x12")
        + Guid.NewGuid().ToString("N")[..12];

    public async Task ReattachAsync(string taskId, string projectRoot, ExternalExecutionHandle handle, CancellationToken cancellationToken)
    {
        using var effect = access.Enter(ProviderEffect.Reattach, handle: handle);
        if (handle.Provider != Provider || handle.ExternalSessionId is null
            || !Regex.IsMatch(handle.ExternalSessionId, "^ses_[a-zA-Z0-9_-]{1,120}$")
            || handle.ExternalRequestId is null || !Regex.IsMatch(handle.ExternalRequestId, "^msg_[a-zA-Z0-9_-]{1,120}$"))
            throw new ExternalHarnessException("InvalidRecoveryIdentity", true);
        await ValidateAsync(new(taskId, projectRoot, "Recovery inspection", TimeSpan.FromSeconds(30)), cancellationToken);
        var binding = new Binding(taskId, approvedRoot, handle);
        using var session = await SendAsync(HttpMethod.Get, SessionPath(handle) + Query(approvedRoot), null, taskId, cancellationToken);
        VerifySession(session.RootElement, binding);
        bindings[handle.ExecutionId] = binding;
        submitted[handle.ExecutionId] = 0; // Reattached executions can never submit again through this client.
    }
    public string? ServerVersion { get; private set; }
    public int GetRequestCount(string taskId) => counts.GetValueOrDefault(taskId);

    private static readonly PermissionRule[] ReadOnlyRules =
    [
        new("*", "*", "deny"),
        new("read", "*", "allow"),
        new("glob", "*", "allow"),
        new("grep", "*", "allow"),
        new("external_directory", "*", "deny"),
        new("read", "*.env*", "deny"),
        new("read", "*.pem", "deny"),
        new("read", "*.key", "deny"),
        new("read", "*credential*", "deny"),
        new("read", "*secret*", "deny"),
        new("read", "*/.git/*", "deny"),
        new("read", "*\\.git\\*", "deny")
    ];

    public OpenCodeClient(HttpClient httpClient, OpenCodeOptions configuration) : this(UnavailableRuntimeAccess.Instance, httpClient, configuration) { }

    internal OpenCodeClient(IRuntimeAccess access, HttpClient httpClient, OpenCodeOptions configuration)
    {
        this.access = access;
        configuration.Validate();
        http = httpClient;
        options = configuration;
        approvedRoot = new ProjectTools(options.ApprovedProjectRoot).Root;
        sessionRules = ReadOnlyRules.Select(rule => options.RequireReadApproval && rule.Permission == "read" && rule.Action == "allow"
            ? rule with { Action = "ask" } : rule).ToArray();
        if (http.BaseAddress is null) http.BaseAddress = options.BaseUri;
        else if (http.BaseAddress != options.BaseUri) throw new ArgumentException("ProviderOriginMismatch");
    }

    public async Task ValidateAsync(ExternalTaskRequest request, CancellationToken cancellationToken)
    {
        using var effect = access.Enter(ProviderEffect.Validate, request);
        string root;
        try
        {
            root = new ProjectTools(request.ProjectRoot).Root;
        }
        catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException or IOException)
        {
            throw new ExternalHarnessException("UnapprovedWorkspace");
        }
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (request.Policy != ExternalExecutionPolicy.ReadOnly || !root.Equals(approvedRoot, comparison))
        {
            throw new ExternalHarnessException("UnapprovedWorkspace");
        }
        try
        {
            using var health = await SendAsync(HttpMethod.Get, "global/health", null, request.TaskId, cancellationToken);
            if (health.RootElement.GetProperty("healthy").ValueKind != JsonValueKind.True)
            {
                throw new ExternalHarnessException("UnhealthyServer");
            }
            ServerVersion = health.RootElement.GetProperty("version").GetString();
            using var schema = await SendAsync(HttpMethod.Get, "doc", null, request.TaskId, cancellationToken);
            VerifySchema(schema.RootElement);
            using var agents = await SendAsync(HttpMethod.Get, "agent" + Query(root), null, request.TaskId, cancellationToken);
            VerifyAgent(agents.RootElement);
        }
        catch (ExternalHarnessException error)
        {
            // No task has been created or submitted during validation.
            throw new ExternalHarnessException(error.Code);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ExternalHarnessException("InvalidDiscoveryResponse");
        }
    }

    private static void VerifySchema(JsonElement schema)
    {
        var paths = schema.GetProperty("paths");
        foreach (var (path, method) in new[]
        {
            ("/session", "post"), ("/session/{sessionID}", "get"),
            ("/session/{sessionID}/prompt_async", "post"), ("/session/status", "get"),
            ("/session/{sessionID}/message", "get"), ("/session/{sessionID}/abort", "post"),
            ("/permission", "get"), ("/question", "get"), ("/agent", "get")
        })
        {
            if (!paths.TryGetProperty(path, out var operations) || !operations.TryGetProperty(method, out _))
            {
                throw new ExternalHarnessException("UnsupportedServerApi");
            }
        }
        var body = paths.GetProperty("/session").GetProperty("post").GetProperty("requestBody")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("properties");
        if (!body.TryGetProperty("permission", out var permission)
            || permission.GetProperty("$ref").GetString() != "#/components/schemas/PermissionRuleset")
        {
            throw new ExternalHarnessException("SessionPermissionUnsupported");
        }
        var rules = schema.GetProperty("components").GetProperty("schemas").GetProperty("PermissionRuleset");
        if (rules.GetProperty("type").GetString() != "array")
        {
            throw new ExternalHarnessException("SessionPermissionUnsupported");
        }
    }

    private void VerifyAgent(JsonElement agents)
    {
        if (agents.ValueKind != JsonValueKind.Array)
        {
            throw new ExternalHarnessException("InvalidAgentPolicy");
        }
        var found = agents.EnumerateArray().FirstOrDefault(agent => String(agent, "name") == options.Agent);
        if (found.ValueKind == JsonValueKind.Undefined || !found.TryGetProperty("permission", out var permission)
            || permission.ValueKind != JsonValueKind.Array)
        {
            throw new ExternalHarnessException("ReadOnlyAgentMissing");
        }
        var rules = permission.EnumerateArray().Select(ParseRule).ToArray();
        var lastDeny = Array.FindLastIndex(rules, rule => rule == new PermissionRule("*", "*", "deny"));
        if (lastDeny < 0 || rules.Skip(lastDeny + 1).Any(rule => rule.Action != "deny"
            && rule.Permission is not ("read" or "glob" or "grep" or "external_directory")))
        {
            throw new ExternalHarnessException("AgentAllowsDangerousTools");
        }
        // OpenCode adds a narrowly scoped tool-output-cache read permission to agents.
        // Session rules below separately deny all external-directory operations.
        if (rules.Skip(lastDeny + 1).Any(rule => rule.Permission == "external_directory"
            && rule.Action != "deny" && !rule.Pattern.Replace('\\', '/').Contains("/.local/share/opencode/tool-output/", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ExternalHarnessException("AgentAllowsExternalDirectories");
        }
    }

    public async Task<ExternalExecutionHandle> CreateAsync(ExternalTaskRequest request, string workflowRunId,
        ExternalExecutionHandle handle, CancellationToken cancellationToken)
    {
        try
        {
            using var effect = access.Enter(ProviderEffect.CreateSession, request, workflowRunId, handle);
            return await CreateCoreAsync(request, workflowRunId, handle, cancellationToken);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            bindings.TryGetValue(handle.ExecutionId, out var binding);
            throw new ExternalHarnessException("InvalidSessionResponse", true, binding?.Handle);
        }
    }

    private async Task<ExternalExecutionHandle> CreateCoreAsync(ExternalTaskRequest request, string workflowRunId,
        ExternalExecutionHandle handle, CancellationToken cancellationToken)
    {
        var messageId = handle.ExternalRequestId ?? NewMessageId();
        var body = new
        {
            title = "MAF " + handle.ExecutionId,
            agent = options.Agent,
            metadata = new { workflowRunId, executionId = handle.ExecutionId },
            permission = sessionRules
        };
        using var session = await SendAsync(HttpMethod.Post, "session" + Query(approvedRoot), body, request.TaskId, cancellationToken);
        var sessionId = String(session.RootElement, "id");
        if (sessionId is null || !Regex.IsMatch(sessionId, "^ses_[a-zA-Z0-9_-]{1,120}$"))
        {
            throw new ExternalHarnessException("InvalidSessionIdentity", true);
        }
        var created = handle with { ExternalSessionId = sessionId, ExternalRequestId = messageId };
        bindings[handle.ExecutionId] = new(request.TaskId, approvedRoot, created);
        // Identity is preserved even if subsequent safety verification rejects execution.
        try
        {
            VerifySession(session.RootElement, bindings[handle.ExecutionId]);
        }
        catch (ExternalHarnessException error)
        {
            if (access is IOriginalCreateAccess { ReceiptEnabled: true } receipt)
                await receipt.FinalizeOriginalCreate(created, false,
                    error.Code == "SessionPolicyMismatch" ? "SessionPolicyMismatch" : "InvalidSessionResponse");
            throw new ExternalHarnessException(error.Code, error.Ambiguous, created);
        }
        if (access is IOriginalCreateAccess { ReceiptEnabled: true } original)
            await original.FinalizeOriginalCreate(created, true, "None");
        return created;
    }

    private static PermissionRule ParseRule(JsonElement rule) => new(
        String(rule, "permission") ?? "", String(rule, "pattern") ?? "", String(rule, "action") ?? "");

    private void VerifySession(JsonElement session, Binding binding)
    {
        try
        {
            VerifySessionCore(session, binding);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            throw new ExternalHarnessException("InvalidSessionResponse", true, binding.Handle);
        }
    }

    private void VerifySessionCore(JsonElement session, Binding binding)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (String(session, "id") != binding.Handle.ExternalSessionId || String(session, "directory") is not { } root
            || !Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).Equals(binding.Root, comparison)
            || !session.TryGetProperty("permission", out var permission) || permission.ValueKind != JsonValueKind.Array
            || !permission.EnumerateArray().Select(ParseRule).SequenceEqual(sessionRules))
        {
            throw new ExternalHarnessException("SessionPolicyMismatch");
        }
    }

    public async Task SubmitAsync(ExternalTaskRequest request, ExternalExecutionHandle handle, CancellationToken cancellationToken)
    {
        using var effect = access.Enter(ProviderEffect.SubmitPrompt, request, handle: handle);
        var binding = Bound(handle);
        if (binding.TaskId != request.TaskId || !submitted.TryAdd(handle.ExecutionId, 0))
        {
            throw new ExternalHarnessException("DuplicateSubmissionBlocked");
        }
        using var session = await SendAsync(HttpMethod.Get, SessionPath(handle) + Query(binding.Root), null, binding.TaskId, cancellationToken);
        VerifySession(session.RootElement, binding);
        using var acknowledgment = await SendAsync(HttpMethod.Post, SessionPath(handle) + "/prompt_async" + Query(binding.Root),
            new { messageID = handle.ExternalRequestId, agent = options.Agent, parts = new[] { new { type = "text", text = Redact(request.TaskText) } } },
            binding.TaskId, cancellationToken, noContent: true);
    }

    public async Task<ExternalObservation> ObserveAsync(ExternalExecutionHandle handle, CancellationToken cancellationToken)
    {
        try
        {
            using var effect = access.Enter(ProviderEffect.Observe, handle: handle);
            return await ObserveCoreAsync(handle, cancellationToken);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            throw new ExternalHarnessException("InvalidProviderShape", true);
        }
    }

    private async Task<ExternalObservation> ObserveCoreAsync(ExternalExecutionHandle handle, CancellationToken cancellationToken)
    {
        var binding = Bound(handle);
        using var session = await SendAsync(HttpMethod.Get, SessionPath(handle) + Query(binding.Root), null, binding.TaskId, cancellationToken);
        VerifySession(session.RootElement, binding);
        foreach (var route in new[] { "permission", "question" })
        {
            using var pending = await SendAsync(HttpMethod.Get, route + Query(binding.Root), null, binding.TaskId, cancellationToken);
            if (pending.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new ExternalHarnessException("InvalidPendingInput", true);
            }
            if (pending.RootElement.EnumerateArray().Any(item => String(item, "sessionID") == handle.ExternalSessionId))
            {
                return new(ExternalExecutionStatus.WaitingForApproval, "External permission or user input is required.");
            }
        }
        using var statuses = await SendAsync(HttpMethod.Get, "session/status" + Query(binding.Root), null, binding.TaskId, cancellationToken);
        if (statuses.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ExternalHarnessException("InvalidStatusResponse", true);
        }
        var state = statuses.RootElement.TryGetProperty(handle.ExternalSessionId!, out var value) ? String(value, "type") : "idle";
        if (state is not ("idle" or "busy" or "retry"))
        {
            return new(ExternalExecutionStatus.Unknown, ErrorCode: "UnknownProviderState");
        }
        var messages = await ReadMessagesAsync(binding, cancellationToken);
        var relevant = messages.Items.Where(item => String(item.GetProperty("info"), "role") == "assistant"
            && String(item.GetProperty("info"), "parentID") == handle.ExternalRequestId
            && String(item.GetProperty("info"), "sessionID") == handle.ExternalSessionId).ToArray();
        var last = relevant.OrderBy(item => item.GetProperty("info").GetProperty("time").GetProperty("created").GetInt64()).LastOrDefault();
        if (last.ValueKind == JsonValueKind.Undefined || state != "idle")
        {
            return new(ExternalExecutionStatus.Running);
        }
        var info = last.GetProperty("info");
        if (info.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            return new(String(error, "name") == "MessageAbortedError" ? ExternalExecutionStatus.Cancelled : ExternalExecutionStatus.Failed,
                "Provider confirmed a terminal message error.", "ProviderMessageError");
        }
        if (!info.GetProperty("time").TryGetProperty("completed", out var completed) || completed.ValueKind != JsonValueKind.Number)
        {
            return new(ExternalExecutionStatus.Running);
        }
        var finish = String(info, "finish");
        if (finish == "tool-calls" || finish is null)
        {
            return new(ExternalExecutionStatus.Running);
        }
        var text = Redact(string.Join('\n', last.GetProperty("parts").EnumerateArray()
            .Where(part => String(part, "type") == "text" && (!part.TryGetProperty("ignored", out var ignored) || ignored.ValueKind != JsonValueKind.True))
            .Select(part => String(part, "text"))));
        var truncated = messages.Truncated || text.Length > options.SummaryLimit;
        if (text.Length > options.SummaryLimit)
        {
            text = text[..options.SummaryLimit];
        }
        return new(finish == "stop" && !string.IsNullOrWhiteSpace(text) && !truncated
            ? ExternalExecutionStatus.Completed : ExternalExecutionStatus.Partial,
            text, OutputTruncated: truncated);
    }

    private async Task<(List<JsonElement> Items, bool Truncated)> ReadMessagesAsync(Binding binding, CancellationToken cancellationToken)
    {
        var items = new List<JsonElement>();
        string? before = null;
        var seen = new HashSet<string>();
        for (var page = 0; page < 3; page++)
        {
            var path = SessionPath(binding.Handle) + "/message" + Query(binding.Root) + "&limit=100";
            if (before is not null)
            {
                path += "&before=" + Uri.EscapeDataString(before);
            }
            using var response = await SendAsync(HttpMethod.Get, path, null, binding.TaskId, cancellationToken);
            if (response.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new ExternalHarnessException("InvalidMessageList", true);
            }
            var entries = response.RootElement.EnumerateArray().ToArray();
            items.AddRange(entries.Select(item => item.Clone()));
            if (entries.Length < 100)
            {
                return (items, false);
            }
            before = entries.OrderBy(item => item.GetProperty("info").GetProperty("time").GetProperty("created").GetInt64())
                .Select(item => String(item.GetProperty("info"), "id")).FirstOrDefault();
            if (before is null || !seen.Add(before))
            {
                return (items, true);
            }
        }
        return (items, true);
    }

    public async Task<bool> AbortAsync(ExternalExecutionHandle handle, CancellationToken cancellationToken)
    {
        using var effect = access.Enter(ProviderEffect.Abort, handle: handle);
        var binding = Bound(handle);
        using var result = await SendAsync(HttpMethod.Post, SessionPath(handle) + "/abort" + Query(binding.Root), null,
            binding.TaskId, cancellationToken);
        return result.RootElement.ValueKind == JsonValueKind.True;
    }

    private Binding Bound(ExternalExecutionHandle handle)
    {
        if (!bindings.TryGetValue(handle.ExecutionId, out var binding) || binding.Handle != handle)
        {
            throw new ExternalHarnessException("UnknownExecutionHandle");
        }
        return binding;
    }

    private static string SessionPath(ExternalExecutionHandle handle) => "session/" + Uri.EscapeDataString(handle.ExternalSessionId!);
    private static string Query(string root) => "?directory=" + Uri.EscapeDataString(root);
    private static string? String(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private string Redact(string text)
    {
        var result = ProjectTools.Redact(text);
        if (options.Password is { Length: > 0 } password)
        {
            result = result.Replace(password, "[REDACTED server credential]", StringComparison.Ordinal);
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes((options.Username ?? "opencode") + ":" + password));
            result = result.Replace(encoded, "[REDACTED authorization]", StringComparison.Ordinal);
        }
        return result;
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body, string taskId,
        CancellationToken cancellationToken, bool noContent = false)
    {
        await access.BeforeWireAsync(method, path, taskId, cancellationToken);
        counts.AddOrUpdate(taskId, 1, (_, value) => value + 1);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
        using var request = new HttpRequestMessage(method, new Uri(options.BaseUri, path));
        if (options.Password is { Length: > 0 } password)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes((options.Username ?? "opencode") + ":" + password)));
        }
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, ExternalTaskResult.JsonOptions), Encoding.UTF8, "application/json");
        }
        try
        {
            if (access is IOriginalCreateAccess { ReceiptEnabled: true } receipt)
                receipt.ArmOriginalCreate(http, request);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var definiteRejection = response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
                    or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity;
                throw new ExternalHarnessException("HTTP" + (int)response.StatusCode, !definiteRejection);
            }
            if (access is IOriginalCreateAccess { ReceiptEnabled: true } original)
                original.CaptureOriginalResponse(http, request, response);
            if (noContent)
            {
                if (response.StatusCode != HttpStatusCode.NoContent)
                {
                    throw new ExternalHarnessException("InvalidSubmitAcknowledgment", true);
                }
                return JsonDocument.Parse("null");
            }
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int length;
            while ((length = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (buffer.Length + length > options.ResponseLimitBytes)
                {
                    throw new ExternalHarnessException("ResponseSizeLimit", true);
                }
                buffer.Write(chunk, 0, length);
            }
            return JsonDocument.Parse(buffer.ToArray());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ExternalHarnessException("TransportTimeout", true);
        }
        catch (HttpRequestException)
        {
            throw new ExternalHarnessException("TransportDisconnected", true);
        }
        catch (JsonException)
        {
            throw new ExternalHarnessException("InvalidJson", true);
        }
    }
}
