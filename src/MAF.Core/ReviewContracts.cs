using System.Text.Json;
using System.Text.Json.Serialization;

namespace MAF.Core;

public enum ReviewScope { All, Staged, Unstaged }
public enum ReviewStatus { Completed, Partial, Failed }
public enum ReviewSeverity { Critical, Error, Warning, Info }
public enum ReviewCategory { Compiler, Analyzer, Formatting, Correctness, Security, Maintainability, Readability }
public enum ReviewConfidence { High, Medium, Low }

public sealed record ReviewRequest(string ProjectRoot, ReviewScope ReviewScope = ReviewScope.All,
    string ReviewFocus = "Correctness, Security, Maintainability, Readability", int ContextLimit = 64000,
    int MaxFiles = 40, string? DiagnosticsFile = null);

public sealed record ReviewFinding(
    [property: JsonRequired] string Id,
    [property: JsonRequired] ReviewSeverity Severity,
    [property: JsonRequired] ReviewCategory Category,
    [property: JsonRequired] string File,
    [property: JsonRequired] int Line,
    [property: JsonRequired] string Title,
    [property: JsonRequired] string Description,
    [property: JsonRequired] string Evidence,
    [property: JsonRequired] string Recommendation,
    [property: JsonRequired] ReviewConfidence Confidence);

public sealed record ReviewedFile(string File, string GitStatus, string Kind);

public sealed class ReviewResult
{
    public ReviewStatus Status { get; set; }
    public string Summary { get; set; } = "";
    public ReviewScope Scope { get; set; }
    public List<ReviewFinding> Findings { get; set; } = [];
    public List<ReviewedFile> ReviewedFiles { get; set; } = [];
    public List<string> EvidenceSources { get; set; } = [];
    public List<string> Limitations { get; set; } = [];
    public List<string> UnperformedChecks { get; set; } =
        ["Target build, test and restore were not executed.", "Full compiler, analyzer and formatting execution was not performed."];
    public List<string> SuggestedActions { get; set; } = [];
    public int FindingsCount => Findings.Count;

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}

public interface IReviewService
{
    Task<ReviewResult> ReviewAsync(ReviewRequest request, CancellationToken cancellationToken = default);
}
