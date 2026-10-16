using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using MAF.Core;
using MAF.Core.Policy;
using MAF.Persistence.Sqlite;
using MAF.Workflows.External;
using MAF.Workflows.External.OpenCode;

namespace MAF.ApprovalAcceptance;

// Controlled agent trust and in-memory HTTP only. Never installed in Host.
public sealed class SyntheticRuntime(string database) : ITrustedIdentityResolver, ITrustedPolicyAcquisition,
    IApprovalAuthorization, IExecutionBindingVerifier
{
    public string Database { get; } = database;
    public SyntheticActor Caller { get; } = new("runtime-agent");
    public SyntheticClock Clock { get; } = new();
    public string Root => DurableExecution.NormalizeRoot(Path.GetDirectoryName(Database)!);
    public OpenCodeOptions Options => new(new Uri("http://127.0.0.1:4096"), Root, AllowLoopbackHttp: true);
    public string ProviderScope
    {
        get { using var http = new HttpClient(); return new OpenCodeClient(http, Options).Scope; }
    }
    public ExternalTaskRequest Request => new("runtime-task", Root, "SYNTHETIC_RUNTIME_PROMPT", TimeSpan.FromSeconds(2));
    public ExternalAdmissionRequest Intent => new(ExternalAdmissionMode.Submit, Request.TaskId, Request.Timeout, Request);
    public CapabilityDescriptor Submit => new("runtime-connector", "1", PolicyCapability.ReadOnlyTask, EnforcementBoundary.TaskAdmission, [PolicyOperation.SubmitNewReadOnlyTask], true, true);
    public CapabilityDescriptor Inspect => Submit with { Capability = PolicyCapability.ExecutionInspection, SupportedOperations = [PolicyOperation.InspectExistingExecution] };
    public AuthorizationScope Scope => new(Root, ProviderScope, new('A', 64), 1, 1, ResourceAccess.ReadOnly, Request.Timeout.Ticks);
    public ExternalAdmissionInputs Inputs => new("runtime-authority", new(1), new(1), "OpenCode", Scope, Submit, Inspect,
        "runtime-operation", "runtime-run", new("runtime-execution", "OpenCode", ExternalRequestId: "msg_runtime"), []);
    PolicySnapshot? configured;
    public PolicySnapshot Policy
    {
        get => configured ?? new("runtime-authority", new(1), new(1), Clock.Now.AddHours(-1), Clock.Now.AddHours(1),
            TimeSpan.FromMinutes(5), [Submit, Inspect],
            [new(Caller.SubjectId, Submit.ConnectorId, Submit.Capability, PolicyOperation.SubmitNewReadOnlyTask, Scope, PolicyOutcome.Allow),
             new(Caller.SubjectId, Inspect.ConnectorId, Inspect.Capability, PolicyOperation.InspectExistingExecution, Scope, PolicyOutcome.Allow)]);
        set => configured = value;
    }
    public AuthenticatedIdentity? Resolve(ITrustedCallerContext context) => ReferenceEquals(context, Caller)
        ? new(Caller.SubjectId, Caller.AuthenticationContextId, IdentityKind.Agent, Clock.Now.AddHours(-1), Clock.Now.AddHours(1), false) : null;
    public Task<AcquiredPolicy?> AcquireAsync(CancellationToken token) => Task.FromResult<AcquiredPolicy?>(new("runtime-source", Policy, PolicySnapshotBinding.Digest(Policy)));
    public bool CanControl(AuthenticatedIdentity identity, PolicyRequest operation, PolicySnapshot currentPolicy) => identity.SubjectId == Caller.SubjectId;
    public bool CanDecide(AuthenticatedIdentity identity, PolicyRequest op, string digest, string scope, PolicySnapshot policy) => false;
    public bool IsDecisionSubjectStillAuthorized(string subject, PolicyRequest op, string digest, string scope, PolicySnapshot policy) => false;
    public bool Matches(PolicyRequest op, DurableExecution execution) => execution.Handle.Provider == "OpenCode" && op.ConnectorId == Submit.ConnectorId;
    public TrustedPolicyAuthority Authority() => new("runtime-source", "runtime-authority", this, Clock, this, this, this);
    public SqliteApprovalStore Store() => new(Database, Authority(), Clock);
    public ProtectedExternalRuntime Runtime(HttpClient http, SqliteApprovalStore? coordinator = null, IExecutionStore? executions = null)
    {
        var sql = coordinator ?? Store();
        return new(Inputs, new ImmutablePolicySource(Policy), this, executions ?? new SqliteExecutionStore(Database, ExecutionStoreProfile.ExplicitV2),
            sql, sql, sql, http, Options, Clock);
    }
}

public sealed class RuntimeHttpFixture(string root) : HttpMessageHandler
{
    public ConcurrentQueue<string> Requests { get; } = new();
    public Func<HttpRequestMessage, CancellationToken, Task>? OnRequest { get; set; }
    public bool LoseCreateResponse, LoseSubmitResponse, Running, Pending;
    public string? LedgerPath;
    public string? StatePath;
    JsonElement permission;
    string? message;
    static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    object Session
    {
        get
        {
            if (permission.ValueKind == JsonValueKind.Undefined && StatePath is not null && File.Exists(StatePath))
            {
                using var state = JsonDocument.Parse(File.ReadAllText(StatePath));
                permission = state.RootElement.GetProperty("permission").Clone();
                message = state.RootElement.GetProperty("message").GetString();
            }
            return new { id = "ses_runtime", directory = root, permission };
        }
    }
    void Save() { if (StatePath is not null) File.WriteAllText(StatePath, JsonSerializer.Serialize(new { permission, message })); }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var route = request.RequestUri!.AbsolutePath;
        var accounting = request.Method + " " + route;
        Requests.Enqueue(accounting);
        if (LedgerPath is not null) File.AppendAllText(LedgerPath, accounting + Environment.NewLine);
        if (OnRequest is not null) await OnRequest(request, token);
        if (route == "/global/health") return Json(new { healthy = true, version = "synthetic" });
        if (route == "/doc")
        {
            var paths = new Dictionary<string, object>();
            foreach (var path in new[] { "/session/{sessionID}", "/session/status", "/session/{sessionID}/message", "/permission", "/question", "/agent" }) paths[path] = new { get = new { } };
            foreach (var path in new[] { "/session/{sessionID}/prompt_async", "/session/{sessionID}/abort" }) paths[path] = new { post = new { } };
            paths["/session"] = new { post = new { requestBody = new { content = new Dictionary<string, object> { ["application/json"] = new { schema = new { properties = new { permission = new Dictionary<string, string> { ["$ref"] = "#/components/schemas/PermissionRuleset" } } } } } } } };
            return Json(new { paths, components = new { schemas = new { PermissionRuleset = new { type = "array" } } } });
        }
        if (route == "/agent") return Json(new[] { new { name = "maf_readonly", permission = new[] { new { permission = "*", pattern = "*", action = "deny" } } } });
        if (route == "/session" && request.Method == HttpMethod.Post)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); permission = body.RootElement.GetProperty("permission").Clone();
            Save();
            if (LoseCreateResponse) throw new HttpRequestException("SyntheticLostCreate");
            return Json(Session);
        }
        if (route.EndsWith("/prompt_async"))
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); message = body.RootElement.GetProperty("messageID").GetString();
            Save();
            if (LoseSubmitResponse) throw new HttpRequestException("SyntheticLostSubmit");
            return new(HttpStatusCode.NoContent);
        }
        if (route == "/session/ses_runtime") return Json(Session);
        if (route is "/permission" or "/question") return Json(Pending ? new object[] { new { sessionID = "ses_runtime" } } : []);
        if (route == "/session/status") return Json(new Dictionary<string, object> { ["ses_runtime"] = new { type = Running ? "busy" : "idle" } });
        if (route.EndsWith("/message")) return Json(new[] { new { info = new { id = "msg_reply", sessionID = "ses_runtime", parentID = message, role = "assistant", time = new { created = 1, completed = 2 }, finish = "stop" }, parts = new[] { new { type = "text", text = "synthetic result" } } } });
        throw new InvalidOperationException("UnexpectedFakeRoute");
    }
}
