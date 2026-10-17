using System.Text.Json.Nodes;
using MAF.Core.Policy;
using MAF.Persistence.Sqlite;
using MAF.Workflows.External;
using MAF.Workflows.External.OpenCode;

namespace MAF.ApprovalAcceptance;

// Test-owned trust, synthetic provider and explicit disposable v4 stores only.
public sealed class SyntheticReconciliation(string database)
{
    public SyntheticAbort Abort { get; } = new(database);
    public SyntheticCancellation F => Abort.F;
    public async Task Seed(string delivery = "Acknowledged")
    {
        await Abort.Seed();
        await using var owner = (await F.Store().TryAcquireDeliveryAsync("cancel-one"))!;
        var continuation = await F.Store().ReserveAbortAsync(Abort.Command, Abort.Inputs, F.Runtime.Caller, owner);
        if (delivery != "Intended")
        {
            using var fake = new RuntimeHttpFixture(F.Runtime.Root);
            if (delivery == "SentUncertain") fake.OnRequest = (_, _) => throw new HttpRequestException("SyntheticLostAck");
            using var http = ReceiptTransport.ForTests(fake);
            await ProtectedAbortDelivery.DeliverAsync(continuation, http, F.Runtime.Options);
        }
        InspectOnly(); await F.Store().ActivatePolicyAsync();
    }
    public void InspectOnly()
        => F.Policy = F.Policy with { Epoch = new(2), Version = new(2), Rules = [..F.Policy.Rules.Where(r => r.Operation == PolicyOperation.InspectExistingExecution)] };
    public CancellationReconciliationApplication Application(HttpClient http, Action<string>? checkpoint = null, SqliteApprovalStore? store = null)
    {
        var sql = store ?? F.Store(checkpoint);
        return new(F.Runtime.Inputs with { Epoch = F.Policy.Epoch, Version = F.Policy.Version, OperationId = "reconcile-operation" },
            new ImmutablePolicySource(F.Policy), F, F.Executions, sql, sql, http, F.Runtime.Options, F.Clock) { Checkpoint = checkpoint };
    }
    public ReconciliationHttp Fixture(string outcome) => new(new RuntimeHttpFixture(F.Runtime.Root) { StatePath = database + ".provider" }, outcome);
}

public sealed class ReconciliationHttp(RuntimeHttpFixture inner, string outcome) : DelegatingHandler(inner)
{
    public RuntimeHttpFixture Provider => inner;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (outcome == "Running") inner.Running = true;
        if (outcome == "Pending") inner.Pending = true;
        var response = await base.SendAsync(request, token);
        if (outcome == "Unknown" && request.RequestUri!.AbsolutePath == "/session/status")
            response.Content = new StringContent("{\"ses_runtime\":{\"type\":\"mystery\"}}");
        if (request.RequestUri!.AbsolutePath.EndsWith("/message"))
        {
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(token))!;
            var info = body[0]!["info"]!;
            if (outcome == "Partial") info["finish"] = "length";
            if (outcome is "Failed" or "Cancelled") info["error"] = new JsonObject { ["name"] = outcome == "Cancelled" ? "MessageAbortedError" : "ProviderFault" };
            if (outcome == "WrongParent") info["parentID"] = "msg_foreign";
            if (outcome == "WrongSession") info["sessionID"] = "ses_foreign";
            if (outcome == "MissingCompleted") info["time"]!.AsObject().Remove("completed");
            if (outcome == "MissingId") info.AsObject().Remove("id");
            if (outcome == "ChangedTerminal") info["id"] = "msg_second";
            if (outcome == "MalformedTime") info["time"]!["created"] = decimal.MaxValue;
            if (outcome == "InvalidCompleted") info["time"]!["completed"] = 1.5;
            if (outcome == "InvalidCode") info["error"] = new JsonObject { ["name"] = "unbounded provider text: not a code" };
            if (outcome == "Truncated") body[0]!["parts"]![0]!["text"] = new string('x', 40000);
            response.Content = new StringContent(outcome == "Malformed" ? "{" : outcome == "Empty" ? "[]" : body.ToJsonString());
        }
        return response;
    }
}
