using System.Globalization;
using System.Text.RegularExpressions;
using MAF.Core;

namespace MAF.Host;

internal enum LocalViewKind { Observation, OfflineSample, ControlledOfflineFixture }
internal enum LocalHumanChoice { NotEstablished, Undecided, Accepted, Rejected }
internal sealed record LocalEvidenceView(string? Reference, string? Provenance);

// Display data only: never an authorization input or a restorable proof object.
internal sealed record LocalResultView
{
    public LocalViewKind Kind { get; init; }
    public string? Harness { get; init; }
    public string? TaskId { get; init; }
    public string? WorkflowRunId { get; init; }
    public string? ExecutionId { get; init; }
    public ExternalExecutionStatus? Status { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public string? Summary { get; init; }
    public IReadOnlyList<string> Limitations { get; init; } = [];
    public IReadOnlyList<LocalEvidenceView> Evidence { get; init; } = [];
    public string? Assessment { get; init; }
    public EvidenceAssessmentStatus? ReviewStatus { get; init; }
    public HumanGateStatus? Readiness { get; init; }
    public IReadOnlyList<string> Stages { get; init; } = [];
    public string? MafApproval { get; init; }
    public string? HarnessPermission { get; init; }
    public LocalHumanChoice HumanChoice { get; init; } = LocalHumanChoice.NotEstablished;
    public IReadOnlyList<string> AdapterDiagnostics { get; init; } = [];
}

internal static class LocalSoloPresentation
{
    // Pure bounded redaction: do not read secret environment variables to redact output.
    // Heuristic defense, not a guarantee of detecting arbitrary secrets. No raw transcript intake.
    internal static string Safe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Unavailable";
        var text = value.Length > 8192 ? value[..8192] : value;
        text = Regex.Replace(text, @"(?is)-----BEGIN .*?PRIVATE KEY-----.*", "[REDACTED]");
        text = Regex.Replace(text, @"(?i)\b(?:[a-z][a-z0-9_-]*[_-])?(?:api[_-]?key|password|passwd|pwd|secret|credential|\w*token|authorization|bearer|connectionstring)\s*[""']?\s*[:= ]\s*[^\r\n]*", "[REDACTED]");
        text = Regex.Replace(text, @"(?i)\b(?:sk-[a-z0-9_-]{8,}|gh[pousr]_[a-z0-9]{10,}|AKIA[A-Z0-9]{16}|eyJ[a-z0-9_-]+\.[a-z0-9_-]+\.[a-z0-9_-]+)\b", "[REDACTED]");
        text = Regex.Replace(text, @"(?i)\b[a-z][a-z0-9+.-]*://\S+", "[REDACTED URI]");
        text = string.Concat(text.Select(c => char.IsControl(c) || char.GetUnicodeCategory(c) is
            UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator ? ' ' : c));
        return text.Length > 4096 ? text[..4096] + " [truncated]" : text;
    }

    public static void Profile(TextWriter output, LocalProfile profile)
    {
        output.WriteLine("Local Solo / OfflineOnly / NotExecuted");
        output.WriteLine($"Harness: {Safe(profile.Harness)} (configuration only; not activated)");
        output.WriteLine($"Workspace: {Safe(profile.Workspace)}");
        output.WriteLine("Workspace approval: NotEstablished; local availability is not authorization.");
        output.WriteLine("Intent: ReadOnly; no write, shell or elevated operation.");
        output.WriteLine($"PCA model-role binding: {Binding(profile.PcaBindingPresent)}");
        output.WriteLine($"Reviewer model-role binding: {Binding(profile.ReviewerBindingPresent)}");
        output.WriteLine("Database: NotEstablished; no file opened, created or migrated.");
        output.WriteLine($"Activation: Blocked / {LocalSoloApplication.Blocker}");
        output.WriteLine("Required: genuine caller/policy/activation trust, approved data flows and applicable security clearance.");
    }

    // Prerequisite display only: no result DTO, mutable ready flag, probing or grant.
    public static void Readiness(TextWriter output, LocalProfile profile)
    {
        output.WriteLine("Local Solo readiness / AdvisoryOnly / CandidateOnly / No execution attempted");
        Profile(output, profile);
        output.WriteLine("Trusted caller verification: NotEstablished");
        output.WriteLine("Exact-operation human intention: NotEstablished; Local Solo/login/CLI flags are not proof.");
        output.WriteLine("Genuine policy issuer/current policy: NotEstablished");
        output.WriteLine("Exact operation/scope authorization: NotEstablished");
        output.WriteLine("Explicit capability activation and revocation: NotEstablished");
        output.WriteLine("Protected configuration/durable-state custody: NotEstablished");
        output.WriteLine("Restore/anti-rollback protection: NotEstablished");
        output.WriteLine("Provider credential/bypass protection: NotEstablished");
        output.WriteLine($"PCA -> native model: metadata {Binding(profile.PcaBindingPresent)}; recipient/data-flow authorization NotEstablished");
        output.WriteLine("Harness -> its model/provider and downstream tools: recipient/configuration NotEstablished; data-flow authorization NotEstablished; candidate Harness name is not activation.");
        output.WriteLine($"Reviewer -> native model: metadata {Binding(profile.ReviewerBindingPresent)}; recipient/data-flow authorization NotEstablished");
        output.WriteLine("Deployment execution clearance: NotEstablished here; applicable endpoint/security clearance required.");
        output.WriteLine("Corporate execution hold is not lifted; personal Windows validation is not corporate compatibility clearance.");
        output.WriteLine("Overall execution readiness: Blocked / ProductionTrustUnavailable");
        output.WriteLine("Inspection succeeded is not readiness granted. This display is not a PolicyDecision, permit, approval receipt or dispatch ticket.");
    }

    public static void Preview(TextWriter output, LocalTaskPreview preview)
    {
        Profile(output, preview.Profile);
        output.WriteLine("Candidate task preview only; no Task or Execution identity allocated.");
        output.WriteLine($"Objective: {Safe(preview.Objective)}");
        output.WriteLine($"Proposed time budget: {LocalTaskPreview.BudgetSeconds} seconds; request/tool/output budgets: NotEstablished.");
        output.WriteLine("Future data flows require separate approval: PCA -> native model; Harness -> its model; Reviewer -> native model.");
        output.WriteLine("Project paths, source/tool results and task context may be disclosed; configuration is not an execution grant.");
    }

    public static void Result(TextWriter output, LocalResultView view, bool diagnostics = false)
    {
        var sample = view.Kind == LocalViewKind.OfflineSample;
        output.WriteLine(sample ? "OfflineSample / NotExecuted / ApplicationOwnedSample / NotVerified"
            : view.Kind == LocalViewKind.ControlledOfflineFixture ? "ControlledOfflineFixture / NotLiveProvider / display only; not execution authority"
            : "Result observation / display only; not execution authority");
        output.WriteLine($"Harness: {Safe(view.Harness)}");
        output.WriteLine($"Task: {Safe(view.TaskId)}");
        output.WriteLine($"Workflow run: {Safe(view.WorkflowRunId)}");
        output.WriteLine($"Execution: {Safe(view.ExecutionId)}");
        output.WriteLine($"Observed status: {(sample ? "NotExecuted" : view.Status?.ToString() ?? "Unavailable")}");
        output.WriteLine($"Observed at: {(sample ? "Unavailable" : view.ObservedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "Unavailable")}");
        output.WriteLine("Freshness: NotEstablished (no current source/provider check)");
        output.WriteLine($"Summary: {Safe(view.Summary)}");
        output.WriteLine("Limitations:");
        Limitations(output, view.Limitations);
        output.WriteLine("Evidence (display references only, not trusted proof):");
        if (view.Evidence.Count == 0) output.WriteLine("- Unavailable; provenance: NotEstablished");
        foreach (var evidence in view.Evidence.Take(8))
            output.WriteLine($"- {Safe(evidence.Reference)}; provenance: {Safe(evidence.Provenance)}");
        if (view.Evidence.Count > 8) output.WriteLine("- [truncated]");
        output.WriteLine($"Advisory assessment: {Safe(view.Assessment)}");
        output.WriteLine($"Review status: {view.ReviewStatus?.ToString() ?? "Unavailable"} (advisory; not semantic correctness)");
        output.WriteLine($"HumanGate readiness: {view.Readiness?.ToString() ?? "Unavailable"} (not Accepted)");
        if (view.Stages.Count > 0) { output.WriteLine("Workflow stages:"); Lines(output, view.Stages); }
        output.WriteLine($"MAF pre-dispatch approval: {Safe(view.MafApproval ?? "NotEstablished")}");
        output.WriteLine($"Harness tool permission: {Safe(view.HarnessPermission ?? "NotEstablished")}");
        output.WriteLine($"Human result choice: {view.HumanChoice} (display only; no consent proof or durable acceptance)");
        output.WriteLine("WaitingForApproval does not identify an approval authority. HumanGate readiness is not Accepted.");
        if (diagnostics)
        {
            output.WriteLine("Adapter diagnostics (not authority; no raw transcripts):");
            Lines(output, view.AdapterDiagnostics);
        }
    }

    internal static LocalResultView Sample() => new()
    {
        Kind = LocalViewKind.OfflineSample,
        Harness = "Example Harness (sample)",
        Summary = "Illustrative bounded read-only task result; no task was dispatched.",
        Limitations = ["No workspace content was read; no provider, model, source verification or Reviewer ran."],
        Evidence = [new("fixture/example.txt:1 (illustrative; not read)", "ApplicationOwnedSample / NotVerified")],
        Assessment = "Illustrative advisory text only; actual assessment Unavailable.",
        HumanChoice = LocalHumanChoice.Undecided,
        AdapterDiagnostics = ["Unavailable; no adapter instantiated."]
    };

    // Valid controlled outcomes have at most 50 distinct specialist/gate limitations,
    // plus five mapper summaries/disclaimers. Show all within a finite 64 x 512-char budget;
    // do not let eight early informational entries suppress a later critical limitation.
    private static void Limitations(TextWriter output, IReadOnlyList<string> values)
    {
        if (values.Count == 0) output.WriteLine("- Unavailable");
        foreach (var value in values.Take(64))
        {
            var safe = Safe(value);
            output.WriteLine("- " + (safe.Length > 512 ? safe[..512] + " [truncated]" : safe));
        }
        if (values.Count > 64) output.WriteLine("- [truncated; additional limitations omitted]");
    }

    private static string Binding(bool present) => present ? "ConfiguredMetadataOnly / NotValidated" : "Unavailable";
    private static void Lines(TextWriter output, IReadOnlyList<string> values)
    {
        if (values.Count == 0) output.WriteLine("- Unavailable");
        foreach (var value in values.Take(8)) output.WriteLine($"- {Safe(value)}");
        if (values.Count > 8) output.WriteLine("- [truncated]");
    }
}
