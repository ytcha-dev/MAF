using System.Net;
using System.Text;
using System.Text.Json;
using MAF.Core;
using MAF.Host;
using Microsoft.Extensions.AI;

namespace MAF.Tests;

public sealed class BoundNativeRecipientTests
{
    sealed class Fake : HttpMessageHandler
    {
        public int Calls;
        public string? Model;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public Uri? RewrittenDestination;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); Model = body.RootElement.GetProperty("model").GetString();
            var response = new HttpResponseMessage(Status) { Content = new StringContent(JsonSerializer.Serialize(new
            { id = "chatcmpl_fixture", @object = "chat.completion", created = 1, model = Model,
                choices = new[] { new { index = 0, message = new { role = "assistant", content = "offline fixture" }, finish_reason = "stop" } }
            }), Encoding.UTF8, "application/json") };
            if (RewrittenDestination is not null) response.RequestMessage = new(HttpMethod.Post, RewrittenDestination);
            return response;
        }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OwnedSdkPinsEachRoleIndependently(bool review)
    {
        using var f = new PersonalLabProvenanceTests.Owned(); using var op = f.Begin();
        var fake = new Fake();
        var role = review ? NativeModelRole.Reviewer : NativeModelRole.Pca;
        using var owned = BoundNativeRecipient.ForTests(op, role, fake);
        using var other = BoundNativeRecipient.ForTests(op, review ? NativeModelRole.Pca : NativeModelRole.Reviewer, new Fake());
        var adapters = review ? BoundNativeRecipient.BindAdapters(op, other, owned)
            : BoundNativeRecipient.BindAdapters(op, owned, other);
        Assert.NotNull(adapters.Pca); Assert.NotNull(adapters.Reviewer);
        var result = await owned.Client.GetResponseAsync([new(ChatRole.User, "Personal fixture")]);
        Assert.Equal("offline fixture", result.Text); Assert.Equal(1, fake.Calls);
        Assert.Equal(review ? "review-model" : "pca-model", fake.Model);
        Assert.Equal(review ? LabEffect.ReviewerInference : LabEffect.PcaInference, Assert.Single(f.Admission.Observations).Effect);
        Assert.Equal(LocalExecutionProfile.PersonalLab, f.Preparation.Binding.Profile);
        var count = f.Admission.ReservedEffects;
        var deniedTransport = new Fake();
        using var disabled = BoundNativeRecipient.ForTests(op, role, deniedTransport, LabEffectTarget.Network);
        await Assert.ThrowsAnyAsync<Exception>(() => disabled.Client.GetResponseAsync([new(ChatRole.User, "fixture")]));
        Assert.Equal(count, f.Admission.ReservedEffects); // Even a matching owned client cannot get Network effects.
        Assert.Equal(0, deniedTransport.Calls);
        op.Revoke();
        await Assert.ThrowsAnyAsync<Exception>(() => owned.Client.GetResponseAsync([new(ChatRole.User, "fixture")]));
        Assert.Equal(1, fake.Calls);
    }
    [Theory]
    [InlineData("host")] [InlineData("path")] [InlineData("model")] [InlineData("method")] [InlineData("network")] [InlineData("binding")]
    [InlineData("host-header")] [InlineData("duplicate-model")]
    public async Task UnexpectedRecipientNeverReceivesARequest(string variant)
    {
        var input = PersonalLabAdmissionTests.Input();
        var p = LocalExecutionPreparation.Prepare(input with { Pca = input.Pca! with { Provider = "openai" } });
        var fake = new Fake(); var binding = p.Binding.Pca!;
        if (variant == "binding")
        {
            Assert.Throws<ExternalHarnessException>(() => new BoundNativeRecipient.RecipientGuard(p, null, binding with { Model = "other" }, fake, LabEffectTarget.ControlledOfflineFixture));
            Assert.Equal(0, fake.Calls); return;
        }
        using var client = new HttpClient(new BoundNativeRecipient.RecipientGuard(p, null, binding, fake,
            variant == "network" ? LabEffectTarget.Network : LabEffectTarget.ControlledOfflineFixture));
        var uri = variant == "host" ? "https://other.example.invalid/v1/chat/completions" : binding.Endpoint.AbsoluteUri.TrimEnd('/') + (variant == "path" ? "/responses" : "/chat/completions");
        using var request = new HttpRequestMessage(variant == "method" ? HttpMethod.Get : HttpMethod.Post, uri)
        { Content = new StringContent(variant == "duplicate-model" ? "{\"model\":\"other\",\"model\":" + JsonSerializer.Serialize(binding.Model) + "}"
            : JsonSerializer.Serialize(new { model = variant == "model" ? "other" : binding.Model, messages = Array.Empty<object>() })) };
        if (variant == "host-header") request.Headers.Host = "other.example.invalid";
        await Assert.ThrowsAsync<ExternalHarnessException>(() => client.SendAsync(request));
        Assert.Equal(0, fake.Calls);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RedirectsAndHiddenDestinationChangesAreRejected(bool rewrite)
    {
        using var f = new PersonalLabProvenanceTests.Owned(); using var op = f.Begin();
        var fake = new Fake { Status = rewrite ? HttpStatusCode.OK : HttpStatusCode.TemporaryRedirect,
            RewrittenDestination = rewrite ? new("https://other.example.invalid/v1/chat/completions") : null };
        using var owned = BoundNativeRecipient.ForTests(op, NativeModelRole.Pca, fake);
        await Assert.ThrowsAnyAsync<Exception>(() => owned.Client.GetResponseAsync([new(ChatRole.User, "fixture")]));
        Assert.Equal(1, fake.Calls); // No redirect, failover or SDK retry.
    }
    [Fact]
    public async Task AfterAnAdmittedCallModelOverrideCannotChangeRecipient()
    {
        using var f = new PersonalLabProvenanceTests.Owned(); using var op = f.Begin(); var fake = new Fake();
        using var owned = BoundNativeRecipient.ForTests(op, NativeModelRole.Pca, fake);
        await owned.Client.GetResponseAsync([new(ChatRole.User, "fixture")]);
        await Assert.ThrowsAnyAsync<Exception>(() => owned.Client.GetResponseAsync([new(ChatRole.User, "fixture")], new ChatOptions { ModelId = "other" }));
        Assert.Equal(1, fake.Calls);
    }
    [Fact]
    public async Task EqualEndpointAndModelDoNotPermitRoleSwap()
    {
        using var f = new PersonalLabProvenanceTests.Owned(sharedNativeRecipient: true); using var op = f.Begin();
        var pcaWire = new Fake(); var reviewWire = new Fake();
        using var pca = BoundNativeRecipient.ForTests(op, NativeModelRole.Pca, pcaWire);
        using var reviewer = BoundNativeRecipient.ForTests(op, NativeModelRole.Reviewer, reviewWire);
        Assert.Equal(f.Preparation.Binding.Pca!.Endpoint, f.Preparation.Binding.Reviewer!.Endpoint);
        Assert.Equal(f.Preparation.Binding.Pca.Model, f.Preparation.Binding.Reviewer.Model);
        Assert.NotEqual(f.Preparation.Binding.Pca.ConfigurationId, f.Preparation.Binding.Reviewer.ConfigurationId);
        Assert.Throws<ExternalHarnessException>(() => BoundNativeRecipient.BindAdapters(op, reviewer, pca));
        Assert.Equal(0, pcaWire.Calls + reviewWire.Calls);
        var adapters = BoundNativeRecipient.BindAdapters(op, pca, reviewer); Assert.NotNull(adapters.Pca);
        await pca.Client.GetResponseAsync([new(ChatRole.User, "fixture")]);
        await reviewer.Client.GetResponseAsync([new(ChatRole.User, "fixture")]);
        Assert.Equal(new[] { LabEffect.PcaInference, LabEffect.ReviewerInference }, f.Admission.Observations.Select(x => x.Effect));
        Assert.Equal(1, pcaWire.Calls); Assert.Equal(1, reviewWire.Calls);
    }
    [Fact]
    public void UnsupportedProviderHasNoGenericFallbackWithoutFilesystemAllocation()
    {
        var p = LocalExecutionPreparation.Prepare(PersonalLabAdmissionTests.Input());
        Assert.Throws<ExternalHarnessException>(() => BoundNativeRecipient.Select(p, NativeModelRole.Pca));
    }
}
