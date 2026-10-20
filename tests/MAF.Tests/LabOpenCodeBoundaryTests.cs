using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MAF.Core;
using MAF.Host;

namespace MAF.Tests;

public sealed class LabOpenCodeBoundaryTests
{
    internal static readonly Uri Origin = new("http://127.0.0.1:4096/"); // Fake handler only; never a socket.
    internal static HttpRequestMessage Create(LocalExecutionPreparation p, ExternalExecutionHandle h) => Request(p, "/session", new
    { title = "MAF " + h.ExecutionId, agent = "maf_readonly", metadata = new { executionId = h.ExecutionId, workflowRunId = p.Task.WorkflowRunId },
        permission = new[] { new { permission = "*", pattern = "*", action = "deny" } } });
    internal static HttpRequestMessage Prompt(LocalExecutionPreparation p, ExternalExecutionHandle h) => Request(p, "/session/ses_fixture/prompt_async", new
    { messageID = h.ExternalRequestId, agent = "maf_readonly", parts = new[] { new { type = "text", text = p.Task.Objective } } });
    internal static HttpRequestMessage Request(LocalExecutionPreparation p, string path, object? body = null)
        => new(body is null ? HttpMethod.Get : HttpMethod.Post, new Uri(Origin, path + "?directory=" + Uri.EscapeDataString(p.Task.Workspace)))
        { Content = body is null ? null : Json(body) };
    internal static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    sealed class Fake(LocalExecutionPreparation p) : HttpMessageHandler
    {
        public int Creates, Prompts, Gets;
        public bool LoseCreate, LosePrompt, MismatchSession, Redirect, WrongExchange, BadDirectory, BadPermissions;
        public Action? AfterCreate;
        public JsonNode? SentPrompt;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken t)
        {
            var path = r.RequestUri!.AbsolutePath;
            if (r.Method == HttpMethod.Post && path == "/session")
            {
                Creates++;
                using var body = JsonDocument.Parse(await r.Content!.ReadAsStringAsync(t));
                if (LoseCreate) throw new HttpRequestException("ControlledLostCreate");
                AfterCreate?.Invoke();
                return new(Redirect ? HttpStatusCode.TemporaryRedirect : HttpStatusCode.OK)
                { RequestMessage = WrongExchange ? new(HttpMethod.Post, r.RequestUri) : r,
                    Content = Json(new { id = "ses_fixture", directory = BadDirectory ? p.Task.Workspace + "-other" : p.Task.Workspace,
                        permission = BadPermissions ? JsonSerializer.SerializeToElement(new[] { new { permission = "*", pattern = "*", action = "allow" } })
                            : body.RootElement.GetProperty("permission").Clone() }) };
            }
            if (r.Method == HttpMethod.Post)
            {
                Prompts++; SentPrompt = JsonNode.Parse(await r.Content!.ReadAsStringAsync(t));
                if (LosePrompt) throw new HttpRequestException("ControlledLostPrompt");
                return new(HttpStatusCode.NoContent) { RequestMessage = r };
            }
            Gets++;
            return new(HttpStatusCode.OK) { RequestMessage = r, Content = Json(new { id = MismatchSession ? "ses_replacement" : "ses_fixture",
                directory = p.Task.Workspace, permission = new[] { new { permission = "*", pattern = "*", action = "deny" } } }) };
        }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CandidateAndCorrelationCannotSubstituteForPolicyVerification(bool permissions)
    {
        using var f = new PersonalLabProvenanceTests.Owned();
        using (var op = f.Begin())
        {
            var fake = new Fake(f.Preparation) { BadDirectory = !permissions, BadPermissions = permissions };
            var boundary = LabOpenCodeBoundary.ForTests(op, Origin); boundary.InnerHandler = fake;
            using var http = new HttpClient(boundary);
            Assert.Equal("LabSessionPolicyMismatch", (await Assert.ThrowsAsync<ExternalHarnessException>(() => http.SendAsync(Create(f.Preparation, f.Handle)))).Code);
            await Assert.ThrowsAsync<ExternalHarnessException>(() => http.SendAsync(Prompt(f.Preparation, f.Handle)));
            Assert.Throws<ExternalHarnessException>(() => op.Reserve(LabEffect.Observe));
            Assert.Equal(1, fake.Creates); Assert.Equal(0, fake.Prompts);
        }
        var read = f.Read(); Assert.Equal("NotEstablished", read.Receipt);
        Assert.Contains(read.Events, e => e.Kind == "LabSessionCandidate");
        Assert.Contains(read.Events, e => e.Kind == "LabResponseCorrelated");
        Assert.DoesNotContain(read.Events, e => e.Kind == "LabReceiptVerified");
    }
    [Theory]
    [InlineData("missing")] [InlineData("provider")] [InlineData("model")] [InlineData("extra")]
    public void PureSelectionRejectsFallbackAndMismatch(string variant)
    {
        var b = PersonalLabAdmissionTests.Input().HarnessRecipient!;
        var body = new JsonObject { ["model"] = new JsonObject { ["providerID"] = b.Provider, ["modelID"] = b.Model } };
        LabOpenCodeBoundary.DemandModel(body, b);
        if (variant == "missing") body.Remove("model");
        if (variant == "provider") body["model"]!["providerID"] = "other";
        if (variant == "model") body["model"]!["modelID"] = "other";
        if (variant == "extra") body["model"]!["fallback"] = true;
        Assert.Throws<ExternalHarnessException>(() => LabOpenCodeBoundary.DemandModel(body, b)); // No preparation/storage/directory.
    }
    [Theory]
    [InlineData("valid")] [InlineData("lost-create")] [InlineData("lost-prompt")]
    public async Task OriginalExchangeAndUncertainDeliveryRemainDistinct(string variant)
    {
        using var f = new PersonalLabProvenanceTests.Owned();
        using (var op = f.Begin())
        {
            var fake = new Fake(f.Preparation) { LoseCreate = variant == "lost-create", LosePrompt = variant == "lost-prompt" };
            var boundary = LabOpenCodeBoundary.ForTests(op, Origin); boundary.InnerHandler = fake;
            using var http = new HttpClient(boundary);
            Assert.Equal("LabReceiptRequired", Assert.Throws<ExternalHarnessException>(() => op.Reserve(LabEffect.Observe)).Code);
            Assert.Equal("LabReceiptRequired", Assert.Throws<ExternalHarnessException>(() => op.Reserve(LabEffect.SubmitPrompt)).Code);
            if (variant == "lost-create")
                await Assert.ThrowsAsync<HttpRequestException>(() => http.SendAsync(Create(f.Preparation, f.Handle)));
            else
            {
                using var created = await http.SendAsync(Create(f.Preparation, f.Handle));
                if (variant == "lost-prompt") await Assert.ThrowsAsync<HttpRequestException>(() => http.SendAsync(Prompt(f.Preparation, f.Handle)));
                else { using var submitted = await http.SendAsync(Prompt(f.Preparation, f.Handle)); }
                Assert.Equal(f.Preparation.Binding.HarnessRecipient!.Provider, fake.SentPrompt!["model"]!["providerID"]!.GetValue<string>());
                Assert.Equal(f.Preparation.Binding.HarnessRecipient.Model, fake.SentPrompt["model"]!["modelID"]!.GetValue<string>());
                await Assert.ThrowsAsync<ExternalHarnessException>(() => http.SendAsync(Prompt(f.Preparation, f.Handle)));
                using var observed = await http.SendAsync(Request(f.Preparation, "/session/ses_fixture")); // Original only, no new POST.
                Assert.Equal(1, fake.Gets);
            }
            await Assert.ThrowsAsync<ExternalHarnessException>(() => http.SendAsync(Create(f.Preparation, f.Handle)));
            Assert.Equal(1, fake.Creates); Assert.Equal(variant == "lost-create" ? 0 : 1, fake.Prompts);
        }
        var read = f.Read(); read.DemandExact(f.Preparation, f.Handle); Assert.False(read.CanResume);
        Assert.Equal(variant == "lost-create" ? "NotEstablished" : "FixtureVerifiedOriginalResponse / NotProductionAuthenticity", read.Receipt);
        Assert.Equal("NotEstablished", read.HumanAcceptance);
        Assert.Equal(1, read.Events.Count(e => e.Kind == "Intent" && e.Effect == LabEffect.CreateSession));
        if (variant != "valid") Assert.Contains(read.Events, e => e.Kind == "Uncertain");
    }
    [Theory]
    [InlineData("task")] [InlineData("run")] [InlineData("execution")] [InlineData("request")]
    [InlineData("network")] [InlineData("redirect")] [InlineData("exchange")]
    [InlineData("session")] [InlineData("storage")]
    [InlineData("abort")] [InlineData("permission")] [InlineData("shell")]
    [InlineData("model")] [InlineData("workspace")]
    public async Task InvalidLifecycleCannotDispatchNewEffects(string variant)
    {
        using var f = new PersonalLabProvenanceTests.Owned(); using var op = f.Begin();
        var boundary = LabOpenCodeBoundary.ForTests(op, Origin, target: variant == "network" ? LabEffectTarget.Network : LabEffectTarget.ControlledOfflineFixture);
        var fake = new Fake(f.Preparation) { Redirect = variant == "redirect", WrongExchange = variant == "exchange" };
        boundary.InnerHandler = fake; using var http = new HttpClient(boundary);
        if (variant is "task" or "run" or "execution" or "request")
        {
            var task = f.Preparation.Task;
            if (variant == "task") task = task with { TaskId = "other" };
            if (variant == "run") task = task with { WorkflowRunId = "other" };
            var h = f.Handle;
            if (variant == "execution") h = h with { ExecutionId = "other" };
            if (variant == "request") h = h with { ExternalRequestId = "msg_other" };
            Assert.Throws<ExternalHarnessException>(() => boundary.DemandIdentity(task, h)); Assert.Equal(0, fake.Creates); return;
        }
        if (variant == "storage") fake.AfterCreate = op.Dispose; // Actual closed journal write fails, not a successful receipt.
        if (variant is "network" or "redirect" or "exchange" or "storage")
        {
            await Assert.ThrowsAnyAsync<Exception>(() => http.SendAsync(Create(f.Preparation, f.Handle)));
            await Assert.ThrowsAnyAsync<Exception>(() => http.SendAsync(Prompt(f.Preparation, f.Handle)));
            Assert.Equal(0, fake.Prompts); if (variant == "network") Assert.Equal(0, fake.Creates); return;
        }
        using var created = await http.SendAsync(Create(f.Preparation, f.Handle));
        if (variant == "session")
        {
            fake.MismatchSession = true;
            await Assert.ThrowsAsync<ExternalHarnessException>(() => http.SendAsync(Request(f.Preparation, "/session/ses_fixture")));
        }
        else
        {
            using var request = variant == "model" ? Prompt(f.Preparation, f.Handle) : Request(f.Preparation,
                variant == "permission" ? "/permission/reply" : "/session/ses_fixture/" + variant, new { });
            if (variant == "model")
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
                body["model"] = new JsonObject { ["providerID"] = "other", ["modelID"] = "other" }; request.Content = new StringContent(body.ToJsonString());
            }
            if (variant == "workspace") request.RequestUri = new(Origin, "/session/ses_fixture?directory=other");
            await Assert.ThrowsAsync<ExternalHarnessException>(() => http.SendAsync(request));
        }
        Assert.Equal(0, fake.Prompts);
    }
}
