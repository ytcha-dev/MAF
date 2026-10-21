using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using MAF.Agents.Review;
using MAF.Core;
using Microsoft.Extensions.AI;
using OpenAI;

namespace MAF.Host;

// Owns the concrete OpenAI SDK + HTTP construction. Metadata never grants a wire effect.
// No Host route registers this factory. Network effects remain denied by Lab admission.
internal sealed class BoundNativeRecipient : IDisposable
{
    readonly HttpClient http;
    readonly PersonalLabOfflineOperation operation;
    readonly NativeRecipientBinding binding;
    public IChatClient Client { get; }
    BoundNativeRecipient(PersonalLabOfflineOperation operation, NativeModelRole role, ApiKeyCredential credential,
        HttpMessageHandler transport, LabEffectTarget target)
    {
        this.operation = operation; binding = Select(operation.Preparation, role);
        http = new(new RecipientGuard(operation.Preparation, operation, binding, transport, target)) { Timeout = operation.Preparation.Task.StageTimeout };
        var options = new OpenAIClientOptions { Endpoint = binding.Endpoint,
            Transport = new HttpClientPipelineTransport(http), RetryPolicy = new ClientRetryPolicy(0) };
        Client = new OpenAIClient(credential, options).GetChatClient(binding.Model).AsIChatClient();
    }
    internal static NativeRecipientBinding Select(LocalExecutionPreparation p, NativeModelRole role)
    {
        p.DemandExact(p.Binding);
        var binding = role switch { NativeModelRole.Pca => p.Binding.Pca!, NativeModelRole.Reviewer => p.Binding.Reviewer!,
            _ => throw new ArgumentException("UnsupportedNativeRole") };
        if (binding.Provider != "openai" || binding.Endpoint.Scheme != "https")
            throw new ExternalHarnessException("OwnedNativeTransportUnsupported");
        return binding;
    }
    public static BoundNativeRecipient CreateDisabled(PersonalLabOfflineOperation operation, NativeModelRole role, ApiKeyCredential credential)
        => new(operation, role, credential, new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false }, LabEffectTarget.Network);
    internal static (ITaskBriefing Pca, NativeEvidenceReviewer Reviewer) BindAdapters(PersonalLabOfflineOperation operation,
        BoundNativeRecipient pca, BoundNativeRecipient reviewer, Action<Task>? ownedWork = null)
    {
        if (!ReferenceEquals(pca.operation, operation) || !ReferenceEquals(reviewer.operation, operation)
            || pca.binding != operation.Preparation.Binding.Pca || reviewer.binding != operation.Preparation.Binding.Reviewer)
            throw new ExternalHarnessException("OwnedNativeRoleMismatch");
        return operation.Preparation.BindNativeAdapters(pca.Client, reviewer.Client, ownedWork);
    }
    // Friend-test seam only, analogous to ReceiptTransport.ForTests; never operational DI/input.
    internal static BoundNativeRecipient ForTests(PersonalLabOfflineOperation operation, NativeModelRole role, HttpMessageHandler fake,
        LabEffectTarget target = LabEffectTarget.ControlledOfflineFixture)
        => new(operation, role, new ApiKeyCredential("controlled-offline-fixture"), fake, target);
    public void Dispose() { Client.Dispose(); http.Dispose(); }

    internal sealed class RecipientGuard : DelegatingHandler
    {
        readonly PersonalLabOfflineOperation? operation;
        readonly NativeRecipientBinding binding;
        readonly LabEffectTarget target;
        readonly Uri destination;
        internal RecipientGuard(LocalExecutionPreparation preparation, PersonalLabOfflineOperation? operation, NativeRecipientBinding binding, HttpMessageHandler transport, LabEffectTarget target)
            : base(transport)
        {
            this.operation = operation; this.binding = binding; this.target = target;
            if (Select(preparation, binding.Role) != binding || operation is not null && !ReferenceEquals(preparation, operation.Preparation))
                throw new ExternalHarnessException("NativeRecipientMismatch");
            destination = new(binding.Endpoint.AbsoluteUri.TrimEnd('/') + "/chat/completions");
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Post || request.RequestUri != destination || request.Content is null
                || request.Headers.Host is { } host && !string.Equals(host, destination.Authority, StringComparison.OrdinalIgnoreCase))
                throw new ExternalHarnessException("NativeDestinationRejected");
            await request.Content.LoadIntoBufferAsync(131072);
            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token), new JsonDocumentOptions { MaxDepth = 32 });
            if (body.RootElement.ValueKind != JsonValueKind.Object || body.RootElement.EnumerateObject().Count(p => p.Name == "model") != 1
                || !body.RootElement.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.String || model.GetString() != binding.Model)
                throw new ExternalHarnessException("NativeModelRejected");
            using var effect = (operation ?? throw new ExternalHarnessException("LabAdmissionRequired")).Reserve(
                binding.Role == NativeModelRole.Pca ? LabEffect.PcaInference : LabEffect.ReviewerInference, target, token);
            var response = await base.SendAsync(request, token);
            effect.RecordReturned(); // A returned HTTP response is not successful inference/consent.
            if ((int)response.StatusCode is >= 300 and < 400 || response.RequestMessage?.RequestUri is { } actual && actual != destination)
            { response.Dispose(); throw new ExternalHarnessException("NativeRedirectRejected"); }
            return response;
        }
    }
}
