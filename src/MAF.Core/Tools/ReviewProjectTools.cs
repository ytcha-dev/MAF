using System.Text;

namespace MAF.Core.Tools;

public sealed record ProjectChange(string File, string Status, bool Readable, string Kind);
public sealed record ChangeSet(IReadOnlyList<ProjectChange> Changes, IReadOnlyList<string> Limitations);
public sealed record SourceDocument(string File, IReadOnlyList<string> Lines);

public sealed partial class ProjectTools
{
    private static bool ReadableExtension(string path) => Extensions.Contains(Path.GetExtension(path))
        || Path.GetFileName(path).Equals(".editorconfig", StringComparison.OrdinalIgnoreCase);

    // These typed methods are used by the pipeline; they are not exposed as shell tools.
    public SourceDocument ReadDocument(string path) => new(path, Lines(path));

    public async Task<ChangeSet> DiscoverChanges(ReviewScope scope, int maxFiles, CancellationToken cancellationToken)
    {
        var raw = await Git("status", "--porcelain=v1", "-z", "--untracked-files=all", "--no-renames");
        var changes = new List<ProjectChange>();
        var limitations = new List<string>();
        foreach (var row in raw.Split('\0').Where(row => row.Length > 3))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = row[..2];
            if (scope == ReviewScope.Staged && (status[0] is ' ' or '?'))
            {
                continue;
            }
            if (scope == ReviewScope.Unstaged && status != "??" && status[1] == ' ')
            {
                continue;
            }
            if (changes.Count >= maxFiles)
            {
                limitations.Add("Changed file count exceeds the review limit.");
                break;
            }
            var file = row[3..];
            var deleted = scope == ReviewScope.Staged ? status[0] == 'D' : status[1] == 'D' || status == "D ";
            var kind = status == "??" ? "Untracked" : deleted ? "Deleted" : "Tracked";
            var readable = true;
            try
            {
                ValidateChangePath(file, (kind == "Deleted" || scope == ReviewScope.Staged) && !File.Exists(Path.Combine(Root, file)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                readable = false;
                // Do not reveal excluded path names, which can themselves contain secrets.
                file = "[excluded file]";
                limitations.Add("A changed file was excluded or inaccessible under the read-only policy.");
            }
            changes.Add(new(file, status, readable, kind));
        }
        return new(changes, limitations.Distinct().ToArray());
    }

    public async Task<SourceDocument> ReadChangeDocument(ProjectChange change, ReviewScope scope)
    {
        if (scope != ReviewScope.Staged)
        {
            return ReadDocument(change.File);
        }
        ValidateChangePath(change.File, !File.Exists(Path.Combine(Root, change.File)));
        var content = await Git("show", ":" + change.File);
        if (Encoding.UTF8.GetByteCount(content) > MaxBytes || content.Contains('\0'))
        {
            throw new UnauthorizedAccessException("Binary or oversized index file.");
        }
        return new(change.File, Redact(content).Replace("\r\n", "\n").Split('\n'));
    }

    private void ValidateChangePath(string file, bool deleted)
    {
        if (!deleted)
        {
            Resolve(file);
            return;
        }
        if (Path.IsPathRooted(file) || file.Contains(':') || !ReadableExtension(file)
            || file.Split(['/', '\\']).Any(p => p is ".." or "." || Ignored.Contains(p) || Sensitive(p)))
        {
            throw new UnauthorizedAccessException("Path is excluded.");
        }
        var parent = Path.GetDirectoryName(file);
        while (!string.IsNullOrEmpty(parent) && !Directory.Exists(Path.Combine(Root, parent)))
        {
            parent = Path.GetDirectoryName(parent);
        }
        if (string.IsNullOrEmpty(parent))
        {
            RejectLink(Root);
        }
        else
        {
            Resolve(parent, directory: true);
        }
    }

    public async Task<string> ReadChangeDiff(ProjectChange change, ReviewScope scope, int limit,
        CancellationToken cancellationToken)
    {
        if (!change.Readable)
        {
            throw new UnauthorizedAccessException("Excluded change.");
        }
        ValidateChangePath(change.File, (change.Kind == "Deleted" || scope == ReviewScope.Staged) && !File.Exists(Path.Combine(Root, change.File)));
        var output = new StringBuilder();
        foreach (var staged in new[] { true, false })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (scope == ReviewScope.Staged && !staged || scope == ReviewScope.Unstaged && staged
                || change.Status == "??")
            {
                continue;
            }
            var arguments = new List<string>
            {
                "--literal-pathspecs", "diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--no-color", "--unified=3"
            };
            if (staged)
            {
                arguments.Add("--cached");
            }
            arguments.Add("--");
            arguments.Add(change.File);
            var diff = Redact(await Git(arguments.ToArray()));
            if (diff.Length > 0)
            {
                output.AppendLine(staged ? "[staged]" : "[unstaged]");
                output.Append(diff);
            }
            if (output.Length > limit)
            {
                return output.ToString()[..limit] + "\n[diff truncated]";
            }
        }
        return output.ToString();
    }
}
