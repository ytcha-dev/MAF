using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MAF.Core;

namespace MAF.Host;

internal sealed record LabOpenCodeProtocol(Uri Origin, string Agent, HarnessRecipientBinding Recipient);

// A Lab-only HTTP composition seam, not IRuntimeAccess, a v4 registration or a runtime.
// Only friend tests construct it. No operational factory/CLI, no Network permission.
internal sealed class LabOpenCodeBoundary : DelegatingHandler
{
    readonly PersonalLabOfflineOperation operation;
    readonly LabOpenCodeProtocol protocol;
    readonly SemaphoreSlim exchange = new(1, 1);
    readonly LabEffectTarget target;
    string? session;
    JsonNode? permissions;
    bool verified;
    LabOpenCodeBoundary(PersonalLabOfflineOperation operation, LabOpenCodeProtocol protocol, LabEffectTarget target)
    {
        ValidateProtocol(protocol);
        this.operation = operation; this.protocol = protocol; this.target = target;
        operation.BindLabProtocol(protocol); // Durable protocol/recipient identity before any effect.
    }
    internal static LabOpenCodeBoundary ForTests(PersonalLabOfflineOperation operation, Uri origin, string agent = "maf_readonly",
        LabEffectTarget target = LabEffectTarget.ControlledOfflineFixture)
        => new(operation, new(origin, agent, operation.Preparation.Binding.HarnessRecipient!), target);
    internal static void ValidateProtocol(LabOpenCodeProtocol p)
    {
        if (!p.Origin.IsAbsoluteUri || !p.Origin.IsLoopback || p.Origin.Scheme is not ("http" or "https")
            || p.Origin.UserInfo.Length != 0 || p.Origin.AbsolutePath != "/" || p.Origin.Query.Length != 0 || p.Origin.Fragment.Length != 0
            || !Regex.IsMatch(p.Agent, "\\A[A-Za-z0-9_-]{1,80}\\z") || p.Recipient is null)
            throw new ArgumentException("InvalidLabProtocol");
    }
    internal static void DemandModel(JsonObject prompt, HarnessRecipientBinding recipient)
    {
        if (prompt["model"] is not JsonObject model || model.Count != 2
            || model["providerID"]?.GetValue<string>() != recipient.Provider || model["modelID"]?.GetValue<string>() != recipient.Model)
            throw new ExternalHarnessException("LabModelSelectionMismatch");
    }
    static async Task<JsonNode> Read(HttpContent content, CancellationToken token)
    {
        await content.LoadIntoBufferAsync(1024 * 1024);
        return JsonNode.Parse(await content.ReadAsStringAsync(token), documentOptions: new() { MaxDepth = 32 })
            ?? throw new ExternalHarnessException("LabInvalidResponse");
    }
    internal void DemandIdentity(HandoffTask task, ExternalExecutionHandle original)
    {
        if (task != operation.Preparation.Task || original != operation.OriginalHandle)
            throw new ExternalHarnessException("LabOriginalIdentityMismatch");
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        await exchange.WaitAsync(token);
        try { return await SendCore(request, token); }
        finally { exchange.Release(); }
    }
    async Task<HttpResponseMessage> SendCore(HttpRequestMessage request, CancellationToken token)
    {
        var p = operation.Preparation; p.DemandExact(p.Binding);
        var original = operation.OriginalHandle;
        var uri = request.RequestUri ?? throw new ExternalHarnessException("LabDestinationMismatch");
        if (uri.GetLeftPart(UriPartial.Authority) != protocol.Origin.GetLeftPart(UriPartial.Authority)
            || request.Headers.Host is { } host && !string.Equals(host, uri.Authority, StringComparison.OrdinalIgnoreCase))
            throw new ExternalHarnessException("LabDestinationMismatch");
        var path = uri.AbsolutePath;
        var query = "?directory=" + Uri.EscapeDataString(p.Task.Workspace);
        var expectedQuery = path is "/global/health" or "/doc" ? "" : query;
        if (verified && path == "/session/" + session + "/message") expectedQuery += "&limit=100";
        var queryMatches = uri.Query == expectedQuery;
        if (verified && path == "/session/" + session + "/message" && uri.Query.StartsWith(expectedQuery + "&before=", StringComparison.Ordinal))
            queryMatches = Regex.IsMatch(uri.Query[(expectedQuery.Length + 8)..], "\\Amsg_[A-Za-z0-9_-]{1,120}\\z");
        if (!queryMatches)
            throw new ExternalHarnessException("LabWorkspaceMismatch");
        LabEffect effect;
        if (request.Method == HttpMethod.Post && path == "/session")
        {
            var body = (await Read(request.Content!, token)).AsObject();
            if (body["title"]?.GetValue<string>() != "MAF " + original.ExecutionId || body["agent"]?.GetValue<string>() != protocol.Agent
                || body["metadata"]?["executionId"]?.GetValue<string>() != original.ExecutionId
                || body["metadata"]?["workflowRunId"]?.GetValue<string>() != p.Task.WorkflowRunId)
                throw new ExternalHarnessException("LabOriginalIdentityMismatch");
            permissions = body["permission"]?.DeepClone(); DemandReadOnly(permissions);
            effect = LabEffect.CreateSession;
        }
        else if (request.Method == HttpMethod.Post && verified && path == "/session/" + session + "/prompt_async")
        {
            var body = (await Read(request.Content!, token)).AsObject();
            if (body["messageID"]?.GetValue<string>() != original.ExternalRequestId || body["agent"]?.GetValue<string>() != protocol.Agent
                || body["parts"] is not JsonArray parts || parts.Count != 1 || parts[0]?["type"]?.GetValue<string>() != "text"
                || parts[0]?["text"]?.GetValue<string>() != MAF.Core.Tools.ProjectTools.Redact(p.Task.Objective)
                || body.Any(x => x.Key is not ("messageID" or "agent" or "parts" or "model")))
                throw new ExternalHarnessException("LabPromptMismatch");
            if (!body.ContainsKey("model")) body["model"] = new JsonObject { ["providerID"] = protocol.Recipient.Provider, ["modelID"] = protocol.Recipient.Model };
            DemandModel(body, protocol.Recipient);
            request.Content?.Dispose(); request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            effect = LabEffect.SubmitPrompt;
        }
        else if (request.Method == HttpMethod.Get && path is "/global/health" or "/doc" or "/agent") effect = LabEffect.HarnessValidation;
        else if (request.Method == HttpMethod.Get && verified && (path == "/session/" + session || path == "/session/" + session + "/message"
            || path is "/permission" or "/question" or "/session/status")) effect = LabEffect.Observe;
        else throw new ExternalHarnessException("LabEffectUnavailable"); // No abort/shell/permission replies/replacement session.
        using var intent = operation.Reserve(effect, target, token);
        var method = request.Method;
        var response = await base.SendAsync(request, token);
        try
        {
            // Same captured request/URI/method and response association; no substitute GET or redirect receipt.
            if (!ReferenceEquals(response.RequestMessage, request) || request.RequestUri != uri || request.Method != method
                || response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest)
                throw new ExternalHarnessException("LabOriginalResponseMismatch", true);
            if (effect == LabEffect.CreateSession)
            {
                if (!response.IsSuccessStatusCode) throw new ExternalHarnessException("LabCreateUnconfirmed", true);
                var body = await Read(response.Content, token);
                var candidate = body["id"]?.GetValue<string>();
                if (candidate is null || !Regex.IsMatch(candidate, "\\Ases_[A-Za-z0-9_-]{1,100}\\z"))
                    throw new ExternalHarnessException("LabInvalidSession", true);
                intent.RecordReturned(candidate); operation.RecordSessionEvidence("LabSessionCandidate", candidate);
                operation.RecordSessionEvidence("LabResponseCorrelated", candidate);
                if (body["directory"]?.GetValue<string>() != p.Task.Workspace || !JsonNode.DeepEquals(permissions, body["permission"]))
                    throw new ExternalHarnessException("LabSessionPolicyMismatch", true);
                operation.RecordSessionEvidence("LabReceiptVerified", candidate); // Flush must succeed before permitting Prompt.
                session = candidate; verified = true;
            }
            else
            {
                if (path == "/session/" + session)
                {
                    var body = await Read(response.Content, token);
                    if (body["id"]?.GetValue<string>() != session || body["directory"]?.GetValue<string>() != p.Task.Workspace
                        || !JsonNode.DeepEquals(permissions, body["permission"])) throw new ExternalHarnessException("LabSessionMismatch", true);
                }
                if (path == "/session/" + session + "/message")
                {
                    var messages = (await Read(response.Content, token)).AsArray();
                    foreach (var item in messages)
                    {
                        var info = item?["info"];
                        if (info?["role"]?.GetValue<string>() != "assistant") continue;
                        if (info["sessionID"]?.GetValue<string>() != session || info["parentID"]?.GetValue<string>() != original.ExternalRequestId
                            || info["providerID"]?.GetValue<string>() != protocol.Recipient.Provider || info["modelID"]?.GetValue<string>() != protocol.Recipient.Model)
                            throw new ExternalHarnessException("LabObservedModelMismatch", true);
                    }
                }
                intent.RecordReturned();
            }
            return response; // Existing OpenCode observation, source validation and Workflow determine terminal result.
        }
        catch { response.Dispose(); operation.Revoke(); throw; }
    }
    static void DemandReadOnly(JsonNode? permission)
    {
        if (permission is not JsonArray rules || rules.Count is < 1 or > 32
            || rules[0]?["permission"]?.GetValue<string>() != "*" || rules[0]?["pattern"]?.GetValue<string>() != "*" || rules[0]?["action"]?.GetValue<string>() != "deny"
            || rules.Skip(1).Any(r => r?["action"]?.GetValue<string>() != "deny"
                && (r?["permission"]?.GetValue<string>() is not ("read" or "glob" or "grep") || r?["action"]?.GetValue<string>() is not ("allow" or "ask"))))
            throw new ExternalHarnessException("LabReadOnlyRequired");
    }
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) exchange.Dispose(); }
}
