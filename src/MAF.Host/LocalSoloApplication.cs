using System.Globalization;
using System.Text.RegularExpressions;
using MAF.Core;

namespace MAF.Host;

// Candidate configuration only. No authority, clients, storage or executable selection.
internal sealed record LocalProfile(string Harness, string? Workspace, bool PcaBindingPresent,
    bool ReviewerBindingPresent);
internal sealed record LocalTaskPreview(LocalProfile Profile, string Objective)
{
    public const int BudgetSeconds = 120;
}

internal static class LocalSoloApplication
{
    internal const int MaxObjectiveLength = 4000;
    internal const string Blocker = "ProductionTrustUnavailable";

    public static LocalProfile Profile(string? workspace, Func<string, string?> configuration)
    {
        // Deliberate allowlist of non-secret metadata; no AI_API_KEY, endpoints or credentials.
        var harness = configuration("MAF_LOCAL_HARNESS") ?? "OpenCode";
        if (!Regex.IsMatch(harness, @"\A[A-Za-z][A-Za-z0-9._-]{0,63}\z"))
            throw new ArgumentException("Invalid configured Harness identity (1-64 identifier characters).");
        var root = workspace ?? configuration("MAF_LOCAL_WORKSPACE");
        return new(harness, root is null ? null : ValidateWorkspace(root),
            !string.IsNullOrWhiteSpace(configuration("MAF_LOCAL_PCA_MODEL")),
            !string.IsNullOrWhiteSpace(configuration("MAF_LOCAL_REVIEWER_MODEL")));
    }

    public static LocalTaskPreview Preview(string workspace, string objective, Func<string, string?> configuration)
    {
        if (string.IsNullOrWhiteSpace(objective) || objective.Length > MaxObjectiveLength || UnsafeCharacters(objective))
            throw new ArgumentException("Objective requires 1-4000 characters without control or format characters.");
        return new(Profile(workspace, configuration), objective.Trim());
    }

    internal static bool UnsafeCharacters(string value) => value.Any(c =>
        char.IsControl(c) || char.GetUnicodeCategory(c) is UnicodeCategory.Format
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator);

    internal static string ValidateWorkspace(string value)
    {
        // Reject remote/device syntax before any filesystem access. Availability is not approval.
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || UnsafeCharacters(value)
            || value.StartsWith(@"\") || value.StartsWith("//", StringComparison.Ordinal)
            || value.Contains('~') || value.IndexOfAny(['*', '?', '"', '<', '>', '|']) >= 0
            || value.Contains("://", StringComparison.Ordinal) || !Path.IsPathFullyQualified(value)
            || value.Skip(2).Contains(':')
            || value.Split(['/', '\\']).Any(part => part is "." or ".."
                || part.EndsWith(' ') || part.EndsWith('.')))
            throw new ArgumentException("Workspace requires an absolute local directory without traversal or device syntax.");
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            var drive = new DriveInfo(Path.GetPathRoot(root)!);
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
                throw new ArgumentException("Workspace requires a local filesystem drive.");
            // Inspect ancestors from the drive down; never follow a junction to inspect its child.
            var ancestors = new Stack<string>();
            for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
                ancestors.Push(directory.FullName);
            foreach (var ancestor in ancestors)
            {
                var attributes = File.GetAttributes(ancestor);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("Workspace reparse points are unsupported.");
                if ((attributes & FileAttributes.Directory) == 0)
                    throw new ArgumentException("Workspace directory is unavailable.");
            }
            return root;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ArgumentException("Workspace directory is unavailable.");
        }
    }

    // Unconditional denial. No configuration access, authority acquisition or effect dependency.
    public static void Submit() => throw new ExternalHarnessException(Blocker);
}
