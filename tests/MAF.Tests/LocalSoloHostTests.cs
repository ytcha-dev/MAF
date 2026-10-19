using MAF.Core;
using MAF.Host;

namespace MAF.Tests;

// Authored for a separately cleared environment. No provider/server/model/test trust composition.
public sealed class LocalSoloHostTests : IDisposable
{
    private readonly Lazy<string> ownedWorkspace = new(() => Directory.CreateTempSubdirectory("maf-local-s1-").FullName);
    private string workspace => ownedWorkspace.Value;
    private static string? EmptyConfiguration(string _) => null;
    public void Dispose()
    {
        if (ownedWorkspace.IsValueCreated)
            Directory.Delete(ownedWorkspace.Value); // owned, empty fixture only; non-recursive
    }

    private static (int Exit, string Output, string Error) Run(string[] args, Func<string, string?>? config = null)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = LocalSoloConsole.Run(args, output, error, config ?? EmptyConfiguration);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public void ConfigReadsOnlyMetadataAndNeverPrintsModelValues()
    {
        var reads = new List<string>();
        var result = Run(["--local", "config", "--workspace", workspace], name =>
        {
            reads.Add(name);
            return name switch
            {
                "MAF_LOCAL_HARNESS" => "FutureHarness",
                "MAF_LOCAL_PCA_MODEL" => "private-model-A",
                "MAF_LOCAL_REVIEWER_MODEL" => "private-model-B",
                _ => throw new InvalidOperationException("Unexpected configuration read")
            };
        });
        Assert.Equal(0, result.Exit);
        Assert.Equal(new[] { "MAF_LOCAL_HARNESS", "MAF_LOCAL_PCA_MODEL", "MAF_LOCAL_REVIEWER_MODEL" }, reads);
        Assert.Contains("FutureHarness", result.Output);
        Assert.Contains("ConfiguredMetadataOnly / NotValidated", result.Output);
        Assert.DoesNotContain("private-model", result.Output);
        Assert.Contains("Workspace approval: NotEstablished", result.Output);
        Assert.Contains("ProductionTrustUnavailable", result.Output);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace));
    }

    [Fact]
    public void MissingConfigurationIsExplicitlyUnavailableAndDoesNotCreateDatabase()
    {
        var result = Run(["--local", "config"]);
        Assert.Equal(0, result.Exit);
        Assert.Contains("Harness: OpenCode", result.Output);
        Assert.Contains("Workspace: Unavailable", result.Output);
        Assert.Contains("PCA model-role binding: Unavailable", result.Output);
        Assert.Contains("Reviewer model-role binding: Unavailable", result.Output);
        Assert.Contains("Database: NotEstablished", result.Output);
        Assert.False(ownedWorkspace.IsValueCreated);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://example.invalid")]
    [InlineData("../../client")]
    [InlineData("Harness\u001b[2J")]
    public void InvalidHarnessMetadataIsRejectedWithoutEcho(string harness)
    {
        var result = Run(["--local", "config"], name => name == "MAF_LOCAL_HARNESS" ? harness : null);
        Assert.Equal(2, result.Exit);
        Assert.Empty(result.Output);
        Assert.Equal("InvalidLocalInput: use --local help; no execution attempted." + Environment.NewLine, result.Error);
        Assert.False(ownedWorkspace.IsValueCreated);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData(@"\\server\share\project")]
    [InlineData(@"\\?\C:\project")]
    [InlineData("https://example.invalid/project")]
    [InlineData("//server/share")]
    [InlineData("C:relative")]
    [InlineData("C:\\project\u001b[2J")]
    public void UnsafeWorkspaceShapeIsRejected(string path)
    {
        Assert.Throws<ArgumentException>(() => LocalSoloApplication.ValidateWorkspace(path));
        Assert.False(ownedWorkspace.IsValueCreated);
    }

    [Fact]
    public void ExplicitExistingWorkspaceIsOnlyAnAvailableCandidate()
    {
        var result = Run(["--local", "preview", "--workspace", workspace, "--objective", "Explain bounded public entrypoints"]);
        Assert.Equal(0, result.Exit);
        Assert.Contains("Candidate task preview only", result.Output);
        Assert.Contains("ReadOnly", result.Output);
        Assert.Contains("120 seconds", result.Output);
        Assert.Contains("PCA -> native model; Harness -> its model; Reviewer -> native model", result.Output);
        Assert.Contains("Workspace approval: NotEstablished", result.Output);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace));
    }

    [Fact]
    public void WorkspaceMustExistAndCannotTraverseOrSelectAFile()
    {
        Assert.Throws<ArgumentException>(() => LocalSoloApplication.ValidateWorkspace(Path.Combine(workspace, "missing")));
        Assert.Throws<ArgumentException>(() => LocalSoloApplication.ValidateWorkspace(Path.Combine(workspace, "..", "other")));
        Assert.Throws<ArgumentException>(() => LocalSoloApplication.ValidateWorkspace(new string('a', 1025)));
        // Existing test assembly is checked only as file metadata; never opened or modified.
        Assert.Throws<ArgumentException>(() => LocalSoloApplication.ValidateWorkspace(typeof(LocalSoloHostTests).Assembly.Location));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("task\nspoofed status")]
    [InlineData("task\tother")]
    [InlineData("task\u202Eother")]
    public void UnsafeObjectiveIsRejected(string objective)
    {
        Assert.Throws<ArgumentException>(() => LocalSoloApplication.Preview("unused", objective, EmptyConfiguration));
        Assert.False(ownedWorkspace.IsValueCreated);
    }

    [Fact]
    public void ObjectiveLengthIsBounded()
    {
        Assert.Equal(4000, LocalSoloApplication.Preview(workspace, new string('a', 4000), EmptyConfiguration).Objective.Length);
        Assert.Throws<ArgumentException>(() => LocalSoloApplication.Preview(workspace, new string('a', 4001), EmptyConfiguration));
    }

    [Fact]
    public void ProfileWorkspaceFallbackCannotReplaceExplicitPreviewSelection()
    {
        string? Configuration(string name) => name == "MAF_LOCAL_WORKSPACE" ? Path.Combine(workspace, "missing") : null;
        var preview = LocalSoloApplication.Preview(workspace, "Explain entrypoints", Configuration);
        Assert.Equal(LocalSoloApplication.ValidateWorkspace(workspace), preview.Profile.Workspace);
        Assert.Throws<ArgumentException>(() => LocalSoloApplication.Profile(null, Configuration));
        var result = Run(["--local", "preview", "--objective", "task"], _ => workspace);
        Assert.Equal(2, result.Exit); // explicit --workspace is mandatory even with configuration
    }

    [Fact]
    public void PreviewRedactsRecognizableSecretsWithoutChangingCandidateObjective()
    {
        var preview = LocalSoloApplication.Preview(workspace, "password=do-not-display", EmptyConfiguration);
        using var writer = new StringWriter();
        LocalSoloPresentation.Preview(writer, preview);
        Assert.Equal("password=do-not-display", preview.Objective);
        Assert.DoesNotContain("do-not-display", writer.ToString());
        Assert.Contains("REDACTED", writer.ToString());
    }

    public static TheoryData<string[]> InvalidCli => new()
    {
        new[] { "--local" },
        new[] { "--local", "unknown" },
        new[] { "--local", "preview", "--objective", "task" },
        new[] { "--local", "preview", "--workspace", "path" },
        new[] { "--local", "config", "--workspace" },
        new[] { "--local", "config", "--workspace", "one", "--workspace", "two" },
        new[] { "--local", "sample", "--diagnostics", "--diagnostics" },
        new[] { "--local", "submit", "--trust-test" },
        new[] { "--local", "submit", "--external-task", "task" },
        new[] { "--local", "sample", "--ask", "task" },
        new[] { "--local", "config", "--review" },
        new[] { "--project", "path", "--local", "sample" },
        new[] { "--local", "sample", "--recover-pending" }
    };

    [Theory]
    [MemberData(nameof(InvalidCli))]
    public void InvalidOrMixedModesNeverReadConfiguration(string[] args)
    {
        var result = Run(args, _ => throw new InvalidOperationException("Must not read"));
        Assert.Equal(2, result.Exit);
        Assert.Empty(result.Output);
        Assert.False(ownedWorkspace.IsValueCreated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SubmissionIsUnconditionallyBlockedBeforeConfigurationOrFilesystemUse(bool withIntent)
    {
        var args = withIntent ? new[] { "--local", "submit", "--workspace", @"\\invalid\remote", "--objective", "task" }
            : new[] { "--local", "submit" };
        var result = Run(args, _ => throw new InvalidOperationException("Must not read configuration"));
        Assert.Equal(1, result.Exit);
        Assert.Empty(result.Output);
        Assert.Equal("ProductionTrustUnavailable" + Environment.NewLine, result.Error);
        Assert.False(ownedWorkspace.IsValueCreated);
    }

    [Fact]
    public void SampleIsPresentationOnlyWithoutTrustedEvidenceOrIdentities()
    {
        var result = Run(["--local", "sample"], _ => throw new InvalidOperationException("Must not read"));
        Assert.Equal(0, result.Exit);
        Assert.Contains("OfflineSample / NotExecuted / ApplicationOwnedSample / NotVerified", result.Output);
        Assert.Contains("Task: Unavailable", result.Output);
        Assert.Contains("Execution: Unavailable", result.Output);
        Assert.Contains("Observed status: NotExecuted", result.Output);
        Assert.Contains("Freshness: NotEstablished", result.Output);
        Assert.Contains("Human result choice: Undecided", result.Output);
        Assert.DoesNotContain("Adapter diagnostics", result.Output);
        Assert.DoesNotContain("HTTP requests", result.Output);
        Assert.DoesNotContain("External session", result.Output);
        Assert.False(ownedWorkspace.IsValueCreated);
    }

    [Fact]
    public void SampleNeverPresentsAnActualCompletedObservation()
    {
        var text = Present(LocalSoloPresentation.Sample() with
        {
            Status = ExternalExecutionStatus.Completed, ObservedAt = DateTimeOffset.UtcNow
        });
        Assert.Contains("Observed status: NotExecuted", text);
        Assert.Contains("Observed at: Unavailable", text);
        Assert.DoesNotContain("Observed status: Completed", text);
        Assert.False(ownedWorkspace.IsValueCreated);
    }

    private static string Present(LocalResultView view, bool diagnostics = false)
    {
        using var writer = new StringWriter();
        LocalSoloPresentation.Result(writer, view, diagnostics);
        return writer.ToString();
    }

    [Theory]
    [InlineData(ExternalExecutionStatus.Unknown)]
    [InlineData(ExternalExecutionStatus.Partial)]
    [InlineData(ExternalExecutionStatus.WaitingForApproval)]
    public void ObservationDoesNotInferEvidenceApprovalOrHumanAcceptance(ExternalExecutionStatus status)
    {
        var text = Present(new() { Harness = "OtherHarness", Status = status });
        Assert.Contains($"Observed status: {status}", text);
        Assert.Contains("Task: Unavailable", text);
        Assert.Contains("Execution: Unavailable", text);
        Assert.Contains("Unavailable; provenance: NotEstablished", text);
        Assert.Contains("Advisory assessment: Unavailable", text);
        Assert.Contains("MAF pre-dispatch approval: NotEstablished", text);
        Assert.Contains("Harness tool permission: NotEstablished", text);
        Assert.Contains("Human result choice: NotEstablished", text);
        Assert.False(ownedWorkspace.IsValueCreated);
    }

    [Fact]
    public void ApprovalPermissionAndHumanChoiceAreSeparateDisplayEvents()
    {
        var text = Present(new()
        {
            TaskId = "task-original", ExecutionId = "execution-original", Status = ExternalExecutionStatus.WaitingForApproval,
            MafApproval = "Unavailable", HarnessPermission = "Waiting (untrusted observation)", HumanChoice = LocalHumanChoice.Rejected
        });
        Assert.Contains("Task: task-original", text);
        Assert.Contains("Execution: execution-original", text);
        Assert.Contains("MAF pre-dispatch approval: Unavailable", text);
        Assert.Contains("Harness tool permission: Waiting (untrusted observation)", text);
        Assert.Contains("Human result choice: Rejected", text);
        Assert.Contains("HumanGate readiness is not Accepted", text);
        Assert.Contains("Observed status: WaitingForApproval", text);
        Assert.False(ownedWorkspace.IsValueCreated);
    }

    [Fact]
    public void DiagnosticsAreOptInAndMissingSourceFieldsStayUnavailable()
    {
        var view = new LocalResultView { Evidence = [new(null, null)], AdapterDiagnostics = ["opaque adapter identifier"] };
        Assert.DoesNotContain("opaque adapter identifier", Present(view));
        Assert.Contains("opaque adapter identifier", Present(view, true));
        Assert.Contains("Unavailable; provenance: Unavailable", Present(view));
        Assert.False(ownedWorkspace.IsValueCreated);
    }

    [Theory]
    [InlineData("password=do-not-display", "do-not-display")]
    [InlineData("client_secret=do-not-display", "do-not-display")]
    [InlineData("OPENAI_API_KEY=do-not-display", "do-not-display")]
    [InlineData("Authorization: Bearer private-value", "private-value")]
    [InlineData("https://user:private-value@example.invalid/query?token=x", "private-value")]
    [InlineData("sk-abcdefghijklmnop", "sk-abcdefghijklmnop")]
    [InlineData("-----BEGIN PRIVATE KEY-----hidden", "hidden")]
    public void AllFreeTextPresentationFieldsRedactRecognizableSecrets(string input, string secret)
    {
        var text = Present(new()
        {
            Harness = input, TaskId = input, ExecutionId = input, Summary = input, Limitations = [input],
            Evidence = [new(input, input)], Assessment = input, MafApproval = input, HarnessPermission = input,
            AdapterDiagnostics = [input]
        }, true);
        Assert.DoesNotContain(secret, text);
        Assert.Contains("REDACTED", text);
        Assert.False(ownedWorkspace.IsValueCreated);
    }

    [Fact]
    public void PresenterNeutralizesTerminalControlsAndBoundsCollectionsAndFields()
    {
        var text = Present(new()
        {
            Summary = "hello\u001b[2J\r\nSpoof\u202E\u0085\u2028",
            Limitations = Enumerable.Repeat(new string('x', 10000), 100).ToArray()
        });
        Assert.DoesNotContain('\u001b', text);
        Assert.DoesNotContain('\u202E', text);
        Assert.DoesNotContain('\u0085', text);
        Assert.DoesNotContain('\u2028', text);
        Assert.DoesNotContain("\nSpoof", text);
        Assert.Contains("[truncated]", text);
        Assert.True(text.Length < 40000);
        Assert.False(ownedWorkspace.IsValueCreated);
    }

    [Theory]
    [InlineData("--external-task")]
    [InlineData("--recover")]
    [InlineData("--recover-pending")]
    public async Task ExistingExternalDenialStillPrecedesAnyConfigurationRead(string mode)
    {
        var error = await Assert.ThrowsAsync<ExternalHarnessException>(() => ExternalConsole.RunAsync([mode], "unused",
            _ => throw new InvalidOperationException("Must not read")));
        Assert.Equal("ProductionTrustUnavailable", error.Code);
        Assert.False(ownedWorkspace.IsValueCreated);
    }
}
