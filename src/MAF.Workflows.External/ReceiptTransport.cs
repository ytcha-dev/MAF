using System.Runtime.CompilerServices;

namespace MAF.Workflows.External;

// A known transport construction profile, NOT caller authentication. No automatic redirects
// may hide extra HTTP effects behind one provider guard. Host does not compose this runtime.
public static class ReceiptTransport
{
    sealed record Exchange(object Transport, HttpRequestMessage Request, Uri? Uri, HttpMethod Method);
    static readonly ConditionalWeakTable<HttpClient, object> Owned = new();
    static readonly ConditionalWeakTable<HttpResponseMessage, Exchange> Responses = new();
    public static HttpClient Create()
        => Register(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false }, false);
    internal static HttpClient ForTests(HttpMessageHandler handler) => Register(handler, true);
    sealed class AttestedExchange(HttpMessageHandler inner, object transport, bool synthetic) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri; var method = request.Method;
            var response = await base.SendAsync(request, token);
            if (synthetic) response.RequestMessage ??= request; // Test-owned fake, never production fallback.
            Responses.Add(response, new(transport, request, uri, method));
            return response;
        }
    }
    static HttpClient Register(HttpMessageHandler handler, bool synthetic)
    { var identity = new object(); var client = new HttpClient(new AttestedExchange(handler, identity, synthetic)); Owned.Add(client, identity); return client; }
    internal static bool IsOwned(HttpClient client) => Owned.TryGetValue(client, out _);
    internal static bool IsOriginalResponse(HttpClient client, HttpRequestMessage request, HttpResponseMessage response)
        => Owned.TryGetValue(client, out var identity) && Responses.TryGetValue(response, out var exchange)
            && ReferenceEquals(identity, exchange.Transport) && ReferenceEquals(request, exchange.Request)
            && exchange.Uri == request.RequestUri && exchange.Method == request.Method && request.Method == HttpMethod.Post;
}

internal interface IOriginalCreateAccess
{
    bool ReceiptEnabled { get; }
    void ArmOriginalCreate(HttpClient client, HttpRequestMessage request);
    void CaptureOriginalResponse(HttpClient client, HttpRequestMessage request, HttpResponseMessage response);
    Task FinalizeOriginalCreate(MAF.Core.ExternalExecutionHandle handle, bool verified, string diagnostic);
}
internal interface IOriginalCreateStore
{
    MAF.Core.DurableExecution? TakeOriginalCreate(MAF.Core.ExternalExecutionHandle handle);
}
