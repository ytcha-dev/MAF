using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MAF.Core;
using MAF.Core.Policy;

namespace MAF.Workflows.External.OpenCode;

// A committed original-session capability only; no arbitrary session API or reattachment.
// Host does not register this synthetic protected composition.
public static class ProtectedAbortDelivery
{
    public static async Task<AbortDeliveryEvidence> DeliverAsync(AbortDeliveryContinuation continuation,
        HttpClient http, OpenCodeOptions options, CancellationToken cancellation = default)
    {
        continuation.Consume(); // Burn even if configuration, identity or transport validation fails.
        var stage = "SuppressedBeforeWire"; var diagnostic = "AuthorizationChanged";
        var op = continuation.Operation;
        try
        {
            options.Validate();
            if (!ReceiptTransport.IsOwned(http) || continuation.Provider != "OpenCode"
                || op.Operation != PolicyOperation.AbortExistingExecution
                || !Regex.IsMatch(op.ProviderSessionId ?? "", "^ses_[a-zA-Z0-9_-]{1,120}$")
                || DurableExecution.NormalizeRoot(options.ApprovedProjectRoot) != op.Scope.ResourceId
                || new OpenCodeClient(http, options).Scope != op.Scope.ProviderScopeDigest)
                throw new InvalidOperationException("AbortBindingMismatch");
            cancellation.ThrowIfCancellationRequested();
            using var budget = new CancellationTokenSource(TimeSpan.FromTicks(op.Scope.BudgetTicks));
            // Task cancellation never propagates into delivery; only an explicit pre-wire
            // cancellation suppresses it. Transport has its own trusted, <= five second budget.
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.BaseUri,
                "session/" + Uri.EscapeDataString(op.ProviderSessionId!) + "/abort?directory=" + Uri.EscapeDataString(options.ApprovedProjectRoot)));
            if (options.Password is { Length: > 0 } password)
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes((options.Username ?? "opencode") + ":" + password)));
            await continuation.Preflight(budget.Token); // No authority lease or SQL transaction survives this await.
            await using var dispatchUse = await continuation.DispatchUse(budget.Token);
            cancellation.ThrowIfCancellationRequested();
            stage = "SentUncertain"; diagnostic = "ResponseLost";
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
            if (!ReceiptTransport.IsOriginalResponse(http, request, response) || !ReferenceEquals(response.RequestMessage, request)
                || !response.IsSuccessStatusCode)
                diagnostic = "ProviderUnavailable";
            else
            {
                await using var stream = await response.Content.ReadAsStreamAsync(budget.Token);
                var bytes = new byte[33]; var used = 0;
                while (used < bytes.Length)
                { var n = await stream.ReadAsync(bytes.AsMemory(used), budget.Token); if (n == 0) break; used += n; }
                diagnostic = "InvalidAcknowledgement";
                if (used < bytes.Length)
                {
                    try
                    {
                        using var json = JsonDocument.Parse(bytes.AsMemory(0, used));
                        if (json.RootElement.ValueKind == JsonValueKind.True) { stage = "Acknowledged"; diagnostic = "None"; }
                    }
                    catch (JsonException) { }
                }
            }
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or OperationCanceledException or HttpRequestException or IOException)
        { /* Closed diagnostic only. Before wire is suppressed; after wire is uncertain. */ }
        using var capture = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await continuation.Complete(stage, diagnostic, capture.Token);
    }
}
