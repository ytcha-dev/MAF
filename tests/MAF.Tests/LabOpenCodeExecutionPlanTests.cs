using MAF.Core;
using MAF.Host;

namespace MAF.Tests;

// No owned directories, files, sockets, processes, clients, journals or remote calls.
// Preparation uses existing test-output directory metadata, as the existing pure tests do.
public sealed class LabOpenCodeExecutionPlanTests
{
    static LocalExecutionPreparation Prepare(LocalExecutionProfile profile = LocalExecutionProfile.PersonalLab)
        => LocalExecutionPreparation.Prepare(new("Explain the synthetic fixture",
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), profile,
            new(NativeModelRole.Pca, "pca-v1", new("https://pca.example.invalid/v1"), "pca-provider", "pca-model"),
            new(NativeModelRole.Reviewer, "review-v1", new("https://review.example.invalid/v1"), "review-provider", "review-model"),
            new("harness-v1", "harness-provider", "harness-model"), TimeSpan.FromSeconds(60)));
    static LabOpenCodeDownstreamConfiguration Config() => new(new("harness-v1", "harness-provider", "harness-model"),
        "https://harness.example.invalid/v1", new string('a', 64),
        LabDownstreamFlow.None, LabDownstreamFlow.None, LabDownstreamFlow.None);

    [Fact]
    public void BindingPreservesOriginalPreparationWithoutClaimingAdmission()
    {
        var p = Prepare(); var plan = LabOpenCodeExecutionPlan.Bind(p, Config());
        Assert.Same(p, plan.Preparation); Assert.Same(p.Task, plan.Preparation.Task);
        Assert.Equal(p.OperationId, plan.OperationId); Assert.Equal(p.Binding.HarnessRecipient, plan.Recipient);
        plan.DemandConfiguration(Config());
        Assert.All(typeof(LabOpenCodeExecutionPlan).GetProperties(), property => Assert.Null(property.SetMethod));
        // Binding is not an admission and does not consume the existing one-time admission claim.
        Assert.Equal(PersonalLabState.Admitted,
            PersonalLabAdmission.AdmitOffline(p, p.Binding, "original-execution", TimeProvider.System).State);
    }

    [Theory]
    [InlineData("provider")] [InlineData("model")] [InlineData("configuration")]
    public void RecipientMustMatchTheExistingPreparation(string field)
    {
        var c = Config(); var recipient = field switch
        {
            "provider" => c.Recipient with { Provider = "other" },
            "model" => c.Recipient with { Model = "other" },
            _ => c.Recipient with { ConfigurationId = "other" }
        };
        Assert.Equal("LabPlanRecipientMismatch", Assert.Throws<ArgumentException>(() =>
            LabOpenCodeExecutionPlan.Bind(Prepare(), c with { Recipient = recipient })).Message);
    }

    [Theory]
    [InlineData("provider")] [InlineData("model")] [InlineData("configuration")]
    [InlineData("endpoint")] [InlineData("digest")] [InlineData("path")] [InlineData("port")]
    public void CandidateDescriptionsCannotChangeAnyBoundValue(string field)
    {
        var c = Config(); var plan = LabOpenCodeExecutionPlan.Bind(Prepare(), c);
        var changed = field switch
        {
            "provider" => c with { Recipient = c.Recipient with { Provider = "other" } },
            "model" => c with { Recipient = c.Recipient with { Model = "other" } },
            "configuration" => c with { Recipient = c.Recipient with { ConfigurationId = "other" } },
            "endpoint" => c with { Endpoint = "https://other.example.invalid/v1" },
            "path" => c with { Endpoint = c.Endpoint + "/" },
            "port" => c with { Endpoint = "https://harness.example.invalid:8443/v1" },
            _ => c with { ConfigurationDigest = new string('b', 64) }
        };
        Assert.Equal("LabPlanConfigurationMismatch", Assert.Throws<ArgumentException>(() => plan.DemandConfiguration(changed)).Message);
        plan.DemandConfiguration(c); // Failed comparison never replaces the original binding.
        Assert.NotEqual(plan.BindingDigest, LabOpenCodeExecutionPlan.Bind(Prepare(), c with { ConfigurationDigest = new string('b', 64) }).BindingDigest);
    }

    [Fact]
    public void CanonicalRepresentationIsDeterministicAndDoesNotCreateAnExecutionIdentity()
    {
        var p = Prepare(); var first = LabOpenCodeExecutionPlan.Bind(p, Config());
        var equivalent = Config() with { Endpoint = "HTTPS://HARNESS.EXAMPLE.INVALID:443/v1", ConfigurationDigest = new string('A', 64) };
        var second = LabOpenCodeExecutionPlan.Bind(p, equivalent);
        first.DemandConfiguration(equivalent);
        Assert.Equal(first.ExpectedEndpoint.AbsoluteUri, second.ExpectedEndpoint.AbsoluteUri);
        Assert.Equal(first.BindingDigest, second.BindingDigest);
        // Fixed version-1 UTF-8 JSON contract, independently calculated; excludes operation IDs.
        Assert.Equal("a5b52ad3097317a0bbc8d7929e581ce27ace5ed8ea3613c6b5ec505feded9874", first.BindingDigest);
        Assert.Equal(64, first.BindingDigest.Length);
        Assert.Equal(first.BindingDigest, LabOpenCodeExecutionPlan.Bind(Prepare(), Config()).BindingDigest);
        Assert.Equal(p.OperationId, second.OperationId);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("model-a,model-b")]
    public void MissingOrAmbiguousModelCannotReplaceThePreparedSelector(string? model)
    {
        var c = Config() with { Recipient = Config().Recipient with { Model = model! } };
        Assert.Throws<ArgumentException>(() => LabOpenCodeExecutionPlan.Bind(Prepare(), c));
    }

    [Theory]
    [InlineData("overrides", 0)] [InlineData("overrides", 2)] [InlineData("overrides", 99)]
    [InlineData("fallback", 0)] [InlineData("fallback", 2)] [InlineData("fallback", 99)]
    [InlineData("auxiliary", 0)] [InlineData("auxiliary", 2)] [InlineData("auxiliary", 99)]
    public void UnknownOrPresentOutboundFlowsAreRejectedAtBindingAndComparison(string field, int state)
    {
        var c = Config(); var flow = (LabDownstreamFlow)state;
        var changed = field switch { "overrides" => c with { Overrides = flow },
            "fallback" => c with { Fallback = flow }, _ => c with { AuxiliaryOutbound = flow } };
        Assert.Throws<ArgumentException>(() => LabOpenCodeExecutionPlan.Bind(Prepare(), changed));
        Assert.Throws<ArgumentException>(() => LabOpenCodeExecutionPlan.Bind(Prepare(), c).DemandConfiguration(changed));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("relative/v1")] [InlineData("http://harness.example.invalid/v1")]
    [InlineData("https://user:secret@harness.example.invalid/v1")]
    [InlineData("https://harness.example.invalid/v1?token=secret")]
    [InlineData("https://harness.example.invalid/v1#secret")]
    [InlineData(" https://harness.example.invalid/v1")]
    [InlineData("https://harness.example.invalid/v1\n")]
    public void InvalidEndpointsAreRejectedWithoutEchoingInput(string? endpoint)
    {
        var c = Config() with { Endpoint = endpoint! };
        Assert.Equal("LabPlanInvalidEndpoint", Assert.Throws<ArgumentException>(() => LabOpenCodeExecutionPlan.Bind(Prepare(), c)).Message);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("sha256:unspecified")] [InlineData("secret")]
    public void MissingOrMalformedDigestFailsClosed(string? digest)
    {
        Assert.Equal("LabPlanInvalidConfigurationDigest", Assert.Throws<ArgumentException>(() =>
            LabOpenCodeExecutionPlan.Bind(Prepare(), Config() with { ConfigurationDigest = digest! })).Message);
    }

    [Fact]
    public void IncompleteConfigurationAndImplicitFlowDefaultsAreNotAccepted()
    {
        var p = Prepare(); var c = Config();
        Assert.Throws<ArgumentException>(() => LabOpenCodeExecutionPlan.Bind(p, null!));
        Assert.Throws<ArgumentException>(() => LabOpenCodeExecutionPlan.Bind(p, c with { Recipient = null! }));
        Assert.Throws<ArgumentException>(() => LabOpenCodeExecutionPlan.Bind(p, new(c.Recipient, c.Endpoint, c.ConfigurationDigest)));
        Assert.Throws<ArgumentNullException>(() => LabOpenCodeExecutionPlan.Bind(null!, c));
    }

    [Fact]
    public void ProductionProfileCannotBindAndMatchingLabMetadataStillCannotExecute()
    {
        var production = Prepare(LocalExecutionProfile.ProductionProtected);
        Assert.Equal("LabPlanPersonalLabRequired", Assert.Throws<ArgumentException>(() => LabOpenCodeExecutionPlan.Bind(production, Config())).Message);
        Assert.Equal("ProductionTrustUnavailable", Assert.Throws<ExternalHarnessException>(production.DemandExecution).Code);
        var p = Prepare(); var plan = LabOpenCodeExecutionPlan.Bind(p, Config()); plan.DemandConfiguration(Config());
        Assert.Equal("PersonalLabNotApproved", Assert.Throws<ExternalHarnessException>(p.DemandExecution).Code);
        Assert.Equal("HarnessRecipientVerificationUnavailable", Assert.Throws<ExternalHarnessException>(() => p.DemandHarnessRecipient(plan.Recipient)).Code);
        var admission = PersonalLabAdmission.AdmitOffline(p, p.Binding, "original-execution", TimeProvider.System);
        admission.BeginOffline();
        Assert.Equal("LabEffectUnavailable", Assert.Throws<ExternalHarnessException>(() => PersonalLabAdmission.Reserve(admission,
            new(p.OperationId, p.Task, admission.ExecutionId, p.Binding, p.EvidenceBudgets, LabEffect.CreateSession, LabEffectTarget.Network))).Code);
        Assert.Equal(0, admission.ReservedEffects);
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(1, LocalSoloConsole.Run(["--local", "submit"], output, error));
        Assert.Empty(output.ToString()); Assert.Contains("ProductionTrustUnavailable", error.ToString());
    }

    [Fact]
    public void DiagnosticsNeverPrintEndpointOrConfigurationValues()
    {
        var c = Config() with { Endpoint = "https://harness.example.invalid/private-sensitive-value" };
        var plan = LabOpenCodeExecutionPlan.Bind(Prepare(), c);
        Assert.Equal("LabOpenCodeDownstreamConfiguration / ValuesRedacted / NotRuntimeEvidence", c.ToString());
        Assert.Equal("PersonalLab / OfflineConfigurationBinding / NotRuntimeEvidence / ExecutionBlocked", plan.ToString());
        var error = Assert.Throws<ArgumentException>(() => plan.DemandConfiguration(Config()));
        Assert.Equal("LabPlanConfigurationMismatch", error.Message);
        Assert.DoesNotContain("private-sensitive-value", error.ToString());
    }
}
