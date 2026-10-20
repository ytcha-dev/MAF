using MAF.Core;
using MAF.Host;
using Microsoft.Extensions.AI;

namespace MAF.Tests;

public sealed class LocalExecutionPreparationTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "maf-s2c-" + Guid.NewGuid().ToString("N"));
    public LocalExecutionPreparationTests() => Directory.CreateDirectory(root);
    LocalExecutionInput Input() => new("Explain the personal fixture", root, LocalExecutionProfile.PersonalLab,
        new(NativeModelRole.Pca, "pca-v1", new("https://pca.example.invalid/v1"), "pca-provider", "pca-model"),
        new(NativeModelRole.Reviewer, "review-v1", new("https://review.example.invalid/v1"), "review-provider", "review-model"),
        new("harness-v1", "harness-provider", "harness-model"), TimeSpan.FromSeconds(60));

    [Fact]
    public void ExactImmutableBindingReusesTaskAndCreatesNoFiles()
    {
        var input = Input(); var prepared = LocalExecutionPreparation.Prepare(input);
        prepared.DemandExact(input);
        Assert.Equal(input, prepared.Binding); Assert.Equal(input.Workspace, prepared.Task.Workspace);
        Assert.Equal(input.Objective, prepared.Task.Objective); Assert.Equal(input.StageTimeout, prepared.Task.StageTimeout);
        Assert.True(HandoffValidation.ValidTask(prepared.Task)); Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        Assert.NotEqual(prepared.OperationId, LocalExecutionPreparation.Prepare(input).OperationId);
        Assert.All(typeof(LocalExecutionPreparation).GetProperties(), p => Assert.Null(p.SetMethod));
    }
    [Theory]
    [InlineData("objective")] [InlineData("workspace")] [InlineData("profile")] [InlineData("harness")]
    [InlineData("access")] [InlineData("timeout")] [InlineData("pca")] [InlineData("reviewer")]
    [InlineData("recipient")] [InlineData("configuration")]
    public void ChangedBindingRequiresNewOperation(string field)
    {
        var input = Input(); var prepared = LocalExecutionPreparation.Prepare(input);
        var changed = field switch
        {
            "objective" => input with { Objective = "Changed" },
            "workspace" => input with { Workspace = root + "-other" },
            "profile" => input with { Profile = LocalExecutionProfile.ProductionProtected },
            "harness" => input with { Harness = "Other" },
            "access" => input with { Access = (ExternalExecutionPolicy)99 },
            "timeout" => input with { StageTimeout = TimeSpan.FromSeconds(30) },
            "pca" => input with { Pca = input.Pca! with { Model = "other" } },
            "reviewer" => input with { Reviewer = input.Reviewer! with { Endpoint = new("https://other.invalid") } },
            "recipient" => input with { HarnessRecipient = input.HarnessRecipient! with { Provider = "other" } },
            _ => input with { HarnessRecipient = input.HarnessRecipient! with { ConfigurationId = "v2" } }
        };
        Assert.Equal("PreparedIntentMismatch", Assert.Throws<ArgumentException>(() => prepared.DemandExact(changed)).Message);
    }
    [Theory]
    [InlineData("pca", "PcaBindingMissing")] [InlineData("reviewer", "ReviewerBindingMissing")]
    [InlineData("harness", "HarnessRecipientBindingMissing")] [InlineData("role", "ModelRoleMismatch")]
    [InlineData("shared", "IndependentRoleConfigurationRequired")]
    [InlineData("profile", "UnsupportedExecutionProfile")] [InlineData("access", "ReadOnlyRequired")]
    [InlineData("executable", "UnsupportedHarness")] [InlineData("factory", "UnsupportedHarness")]
    [InlineData("objective", "InvalidObjective")] [InlineData("timeout", "InvalidStageBudget")]
    [InlineData("endpoint", "InvalidNativeRecipient")] [InlineData("recipient", "InvalidRecipientConfiguration")]
    public void InvalidConfigurationIsDistinctFromAuthorization(string field, string error)
    {
        var input = Input();
        input = field switch
        {
            "pca" => input with { Pca = null }, "reviewer" => input with { Reviewer = null },
            "harness" => input with { HarnessRecipient = null },
            "role" => input with { Pca = input.Reviewer },
            "shared" => input with { Reviewer = input.Reviewer! with { ConfigurationId = input.Pca!.ConfigurationId } },
            "profile" => input with { Profile = (LocalExecutionProfile)99 },
            "access" => input with { Access = (ExternalExecutionPolicy)99 },
            "executable" => input with { Harness = @"C:\custom.exe" },
            "factory" => input with { Harness = "Custom.Factory" },
            "objective" => input with { Objective = new string('a', 4001) },
            "timeout" => input with { StageTimeout = TimeSpan.FromMinutes(3) },
            "endpoint" => input with { Pca = input.Pca! with { Endpoint = new("https://user:password@example.invalid") } },
            _ => input with { HarnessRecipient = input.HarnessRecipient! with { Model = "bad\nmodel" } }
        };
        Assert.Equal(error, Assert.Throws<ArgumentException>(() => LocalExecutionPreparation.Prepare(input)).Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }
    [Theory]
    [InlineData(0, "PersonalLabNotApproved")]
    [InlineData(1, "ProductionTrustUnavailable")]
    public void NeitherProfileCanExecute(int profile, string code)
    {
        var prepared = LocalExecutionPreparation.Prepare(Input() with { Profile = (LocalExecutionProfile)profile });
        Assert.Equal(code, Assert.Throws<ExternalHarnessException>(prepared.DemandExecution).Code);
        Assert.Equal("ProductionTrustUnavailable", Assert.Throws<ExternalHarnessException>(LocalSoloApplication.Submit).Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void HarnessRecipientCannotBeInferredOrSelfAttested(bool matching)
    {
        var input = Input(); var prepared = LocalExecutionPreparation.Prepare(input);
        if (matching) Assert.Equal("HarnessRecipientVerificationUnavailable",
            Assert.Throws<ExternalHarnessException>(() => prepared.DemandHarnessRecipient(input.HarnessRecipient)).Code);
        else Assert.Throws<ArgumentException>(() => prepared.DemandHarnessRecipient(null));
        Assert.Throws<ArgumentException>(() => prepared.DemandHarnessRecipient(input.HarnessRecipient! with { Model = "other" }));
    }
    [Fact]
    public void ConcreteNativeAdaptersBindIndependentRecipientsWithoutModelCalls()
    {
        var input = Input(); var prepared = LocalExecutionPreparation.Prepare(input);
        using var pca = new MetadataClient(input.Pca!); using var reviewer = new MetadataClient(input.Reviewer!);
        var adapters = prepared.BindNativeAdapters(pca, reviewer);
        Assert.IsType<MAF.Agents.Comprehension.TaskBriefingAdapter>(adapters.Pca);
        Assert.IsType<MAF.Agents.Review.NativeEvidenceReviewer>(adapters.Reviewer);
        Assert.Throws<ArgumentException>(() => prepared.BindNativeAdapters(reviewer, pca));
        Assert.Throws<ArgumentException>(() => prepared.BindNativeAdapters(pca, pca));
        Assert.Equal(0, pca.Calls + reviewer.Calls); Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }
    [Theory]
    [InlineData("endpoint")] [InlineData("provider")] [InlineData("model")] [InlineData("missing")]
    public void NativeClientMetadataMismatchCannotBind(string field)
    {
        var input = Input(); var prepared = LocalExecutionPreparation.Prepare(input);
        var wrong = field switch
        {
            "endpoint" => input.Pca! with { Endpoint = new("https://elsewhere.invalid") },
            "provider" => input.Pca! with { Provider = "other" },
            _ => input.Pca! with { Model = "other" }
        };
        using var pca = new MetadataClient(wrong) { Missing = field == "missing" };
        using var reviewer = new MetadataClient(input.Reviewer!);
        Assert.Equal("NativeRecipientMismatch", Assert.Throws<ArgumentException>(() => prepared.BindNativeAdapters(pca, reviewer)).Message);
        Assert.Equal(0, pca.Calls + reviewer.Calls);
    }
    [Theory]
    [InlineData("personal-run")] [InlineData("--profile")] [InlineData("--factory")]
    public void PreparationDoesNotRegisterAnOperationalCli(string option)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var args = option == "personal-run" ? new[] { "--local", option }
            : new[] { "--local", "submit", option, "synthetic" };
        Assert.Equal(2, LocalSoloConsole.Run(args, output, error, _ => throw new InvalidOperationException("UnexpectedConfigurationRead")));
        Assert.Empty(output.ToString()); Assert.Contains("InvalidLocalInput", error.ToString());
    }
    sealed class MetadataClient(NativeRecipientBinding binding) : IChatClient
    {
        public int Calls { get; private set; }
        public bool Missing { get; init; }
        public object? GetService(Type type, object? key = null) => !Missing && type == typeof(ChatClientMetadata)
            ? new ChatClientMetadata(binding.Provider, binding.Endpoint, binding.Model) : null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("NoModelRequestPermitted"); }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("NoModelRequestPermitted"); }
        public void Dispose() { }
    }
    public void Dispose() => Directory.Delete(root); // Owned empty fixture only; deliberately non-recursive.
}
