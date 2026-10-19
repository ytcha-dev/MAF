using MAF.Core;
using MAF.Host;

namespace MAF.Tests;

public sealed class LocalSoloReadinessTests
{
    static (int Exit, string Output, string Error) Run(string[] args, Func<string, string?>? config = null)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = LocalSoloConsole.Run(args, output, error, config ?? (_ => null));
        return (exit, output.ToString(), error.ToString());
    }
    [Fact]
    public void MissingConfigurationIsNotAnExecutionOutcomeOrGrant()
    {
        var result = Run(["--local", "readiness"]);
        Assert.Equal(0, result.Exit); Assert.Empty(result.Error);
        Assert.Contains("CandidateOnly", result.Output); Assert.Contains("Harness: OpenCode", result.Output);
        Assert.Contains("Workspace: Unavailable", result.Output); Assert.Contains("Database: NotEstablished", result.Output);
        Assert.Contains("Overall execution readiness: Blocked / ProductionTrustUnavailable", result.Output);
        Assert.Contains("Trusted caller verification: NotEstablished", result.Output);
        Assert.Contains("Exact-operation human intention: NotEstablished", result.Output);
        Assert.Contains("Genuine policy issuer/current policy: NotEstablished", result.Output);
        Assert.Contains("Exact operation/scope authorization: NotEstablished", result.Output);
        Assert.Contains("Explicit capability activation and revocation: NotEstablished", result.Output);
        Assert.Contains("Protected configuration/durable-state custody: NotEstablished", result.Output);
        Assert.Contains("Restore/anti-rollback protection: NotEstablished", result.Output);
        Assert.Contains("Provider credential/bypass protection: NotEstablished", result.Output);
        Assert.Contains("Corporate execution hold is not lifted", result.Output);
        Assert.Contains("not a PolicyDecision", result.Output);
        Assert.DoesNotContain("OfflineSample", result.Output); Assert.DoesNotContain("ControlledOfflineFixture", result.Output);
        Assert.DoesNotContain("Observed status:", result.Output); Assert.DoesNotContain("Execution:", result.Output);
    }
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void RoleMetadataIsIndependentAndCannotEstablishEgressOrAuthority(bool pca, bool reviewer)
    {
        var reads = new List<string>();
        var result = Run(["--local", "readiness"], name =>
        {
            reads.Add(name);
            return name switch
            {
                "MAF_LOCAL_HARNESS" => "FutureHarness",
                "MAF_LOCAL_WORKSPACE" => null,
                "MAF_LOCAL_PCA_MODEL" => pca ? "private-model-A" : null,
                "MAF_LOCAL_REVIEWER_MODEL" => reviewer ? "private-model-B" : null,
                _ => throw new InvalidOperationException("Unexpected environment probe")
            };
        });
        Assert.Equal(0, result.Exit);
        Assert.Equal(new[] { "MAF_LOCAL_HARNESS", "MAF_LOCAL_WORKSPACE", "MAF_LOCAL_PCA_MODEL", "MAF_LOCAL_REVIEWER_MODEL" }, reads);
        var configured = "ConfiguredMetadataOnly / NotValidated";
        Assert.Contains($"PCA -> native model: metadata {(pca ? configured : "Unavailable")}; recipient/data-flow authorization NotEstablished", result.Output);
        Assert.Contains($"Reviewer -> native model: metadata {(reviewer ? configured : "Unavailable")}; recipient/data-flow authorization NotEstablished", result.Output);
        Assert.Contains("Harness -> its model/provider and downstream tools: recipient/configuration NotEstablished", result.Output);
        Assert.Contains("candidate Harness name is not activation", result.Output);
        Assert.DoesNotContain("private-model", result.Output); Assert.Contains("Blocked / ProductionTrustUnavailable", result.Output);
    }
    [Fact]
    public void ExplicitWorkspaceInspectsOnlyOwnedDirectoryMetadataAndCreatesNoFiles()
    {
        var directory = Directory.CreateTempSubdirectory("maf-local-s2b-");
        try
        {
            var result = Run(["--local", "readiness", "--workspace", directory.FullName], name => name switch
            {
                "MAF_LOCAL_HARNESS" => "OpenCode",
                "MAF_LOCAL_PCA_MODEL" or "MAF_LOCAL_REVIEWER_MODEL" => null,
                _ => throw new InvalidOperationException("Explicit workspace must not read fallback or other configuration")
            });
            Assert.Equal(0, result.Exit); Assert.Contains("Workspace approval: NotEstablished", result.Output);
            Assert.Contains("CandidateOnly", result.Output); Assert.Contains("Blocked / ProductionTrustUnavailable", result.Output);
            Assert.Empty(directory.EnumerateFileSystemInfos());
        }
        finally { directory.Delete(); } // owned empty directory only, non-recursive
    }
    public static TheoryData<string[]> Invalid => new()
    {
        new[] { "--local", "readiness", "--workspace" },
        new[] { "--local", "readiness", "--workspace", "one", "--workspace", "two" },
        new[] { "--local", "readiness", "--objective", "secret-value" },
        new[] { "--local", "readiness", "--diagnostics" },
        new[] { "--local", "readiness", "--trust-test" },
        new[] { "--local", "readiness", "--ready", "true" },
        new[] { "--local", "readiness", "--external-task", "secret-value" },
        new[] { "--local", "readiness", "--recover", "secret-value" },
        new[] { "--local", "readiness", "--ask", "secret-value" },
        new[] { "--local", "readiness", "--review" },
        new[] { "--local", "readiness", "--output", "json" },
        new[] { "--project", "secret-value", "--local", "readiness" }
    };
    [Theory, MemberData(nameof(Invalid))]
    public void StrictParserRejectsInvalidIntentBeforeReadingConfiguration(string[] args)
    {
        var result = Run(args, _ => throw new InvalidOperationException("Must not inspect"));
        Assert.Equal(2, result.Exit); Assert.Empty(result.Output);
        Assert.Equal("InvalidLocalInput: use --local help; no execution attempted." + Environment.NewLine, result.Error);
        Assert.DoesNotContain("secret-value", result.Error);
    }
    [Theory]
    [InlineData("relative-secret-path")] [InlineData("https://secret-value.invalid")]
    public void WorkspaceErrorsNeverEchoUntrustedInput(string path)
    {
        var result = Run(["--local", "readiness", "--workspace", path]);
        Assert.Equal(2, result.Exit); Assert.Empty(result.Output); Assert.DoesNotContain(path, result.Error);
    }
    [Fact]
    public void InspectionFailureCannotProduceReadyOrFallBackToAllow()
    {
        var result = Run(["--local", "readiness"], _ => throw new InvalidOperationException("password=secret-value"));
        Assert.Equal(1, result.Exit); Assert.Empty(result.Output);
        Assert.Equal("LocalInspectionUnavailable: no execution attempted." + Environment.NewLine, result.Error);
    }
    [Fact]
    public void ReadinessCannotToggleSubmissionOrRestoreTrustedExecution()
    {
        Assert.Equal(0, Run(["--local", "readiness"]).Exit);
        var result = Run(["--local", "submit"], _ => throw new InvalidOperationException("Must not read"));
        Assert.Equal(1, result.Exit); Assert.Equal("ProductionTrustUnavailable" + Environment.NewLine, result.Error);
        Assert.Equal("ProductionTrustUnavailable", Assert.Throws<ExternalHarnessException>(LocalSoloApplication.Submit).Code);
        Assert.Contains("OfflineSample / NotExecuted", Run(["--local", "sample"]).Output);
    }
    [Fact]
    public void CriticalLimitationsSurviveEarlyInformationalEntriesAndRemainBounded()
    {
        var external = new ExternalTaskResult("task", "run", ExternalExecutionStatus.Unknown,
            new("execution", "OtherHarness", "private-session"), "summary",
            Enumerable.Repeat("informational", 8).Append("uncertain provider delivery; no retry").ToArray(), []);
        var outcome = new WorkflowOutcome("task", "run", ComprehensionStatus.Proposed, external,
            new("task", "execution", EvidenceAssessmentStatus.ReviewUnavailable, "unavailable", [],
                [..Enumerable.Range(0, 8).Select(i => "review note " + i), "critical evidence unavailable"]),
            HumanGateStatus.NeedsHumanReview, [],
            [..Enumerable.Range(0, 16).Select(i => "gate information " + i), "critical scope restriction"], [], "blocked");
        var view = LocalSoloOutcomeMapper.FromControlledWorkflow(outcome);
        using var writer = new StringWriter(); LocalSoloPresentation.Result(writer, view); var text = writer.ToString();
        Assert.Contains("ControlledOfflineFixture / NotLiveProvider", text);
        Assert.Contains("Operational execution blocked / ProductionTrustUnavailable", text);
        Assert.Contains("Unknown execution outcome; not Failed/Cancelled or safely retryable", text);
        Assert.Contains("critical evidence unavailable", text); Assert.Contains("critical scope restriction", text);
        Assert.Contains("uncertain provider delivery; no retry", text);
        Assert.True(text.IndexOf("Operational execution blocked", StringComparison.Ordinal) < text.IndexOf("gate information", StringComparison.Ordinal));
        Assert.DoesNotContain("private-session", text); Assert.Empty(view.Evidence);
        Assert.DoesNotContain("Human result choice: Accepted", text);
        using var bounded = new StringWriter();
        LocalSoloPresentation.Result(bounded, view with { Limitations = Enumerable.Repeat(new string('x', 10000), 100).ToArray() });
        Assert.Contains("[truncated; additional limitations omitted]", bounded.ToString());
        Assert.True(bounded.ToString().Length < 40000);
    }
}
