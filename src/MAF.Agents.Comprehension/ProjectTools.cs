using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace MAF.Agents.Comprehension;

/// <summary>All model-visible project data passes through this read-only boundary.</summary>
public sealed class ProjectTools
{
    public string Root { get; }
    private const int MaxBytes = 256 * 1024;
    private const int MaxFiles = 2000;
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
        { ".git", "bin", "obj", "node_modules", ".dotnet", ".vs", ".aws", ".ssh", ".codex", ".agents", "TestResults", "packages", "vendor", "dist" };
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".csproj", ".sln", ".slnx", ".md", ".txt", ".json", ".xml", ".yml", ".yaml", ".props", ".targets", ".js", ".ts", ".tsx", ".jsx", ".html", ".css", ".py", ".go", ".rs", ".java", ".sql", ".sh", ".ps1", ".razor", ".vue", ".toml" };

    public ProjectTools(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(Root)) throw new ArgumentException("Project directory does not exist.");
        // Check ancestors too: an apparently normal project may live below a junction.
        for (var dir = new DirectoryInfo(Root); dir is not null; dir = dir.Parent)
            RejectLink(dir.FullName);
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Reparse points are not accessible.");
    }

    private static bool Sensitive(string name) => name.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
        || Regex.IsMatch(name, @"(?i)(secret|credential|password|token|api[-_]?key|id_rsa|id_ed25519|^\.netrc$|^\.npmrc$|^\.pypirc$)")
        || new[] { ".pem", ".key", ".pfx", ".p12", ".keystore" }.Contains(Path.GetExtension(name).ToLowerInvariant());

    private string Resolve(string relative, bool directory = false)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new UnauthorizedAccessException("Use a project-relative path.");
        var parts = relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is ".." or "." || Ignored.Contains(p) || Sensitive(p)))
            throw new UnauthorizedAccessException("Path is excluded.");
        var full = Path.GetFullPath(Path.Combine(Root, relative));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Path is outside project.");
        var current = Root;
        RejectLink(Root);
        foreach (var part in parts) { current = Path.Combine(current, part); RejectLink(current); }
        if (!directory && (!Extensions.Contains(Path.GetExtension(full)) || new FileInfo(full).Length > MaxBytes))
            throw new UnauthorizedAccessException("Unsupported or oversized file.");
        return full;
    }

    private IEnumerable<string> Files()
    {
        var pending = new Stack<string>(); pending.Push(Root);
        var visited = 0;
        while (pending.Count > 0 && visited < 20000)
        {
            var dir = pending.Pop();
            RejectLink(dir);
            foreach (var path in Directory.EnumerateFileSystemEntries(dir))
            {
                if (++visited > 20000) yield break;
                var name = Path.GetFileName(path);
                var attributes = File.GetAttributes(path);
                if (Ignored.Contains(name) || Sensitive(name) || (attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                else if (Extensions.Contains(Path.GetExtension(path)) && new FileInfo(path).Length <= MaxBytes)
                    yield return Path.GetRelativePath(Root, path).Replace('\\', '/');
            }
        }
    }

    public static string Redact(string text)
    {
        if (text.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase)) return "[REDACTED private key content]";
        text = Regex.Replace(text, @"(?im)^.*(?:api[_-]?key|password|passwd|pwd|secret|credential|access[_-]?token|refresh[_-]?token|authorization|bearer|connectionstring|accountkey|sig=)\s*[""']?\s*[:=].*$", "[REDACTED sensitive line]");
        text = Regex.Replace(text, @"(?i)\b(?:sk-[a-z0-9_-]{8,}|gh[pousr]_[a-z0-9]{10,}|AKIA[A-Z0-9]{16}|eyJ[a-z0-9_-]+\.[a-z0-9_-]+\.[a-z0-9_-]+)\b", "[REDACTED credential]");
        text = Regex.Replace(text, @"(?i)\b(?:https?|postgres(?:ql)?|mysql|mongodb)://[^\s/@]+:[^\s/@]+@", "[REDACTED credential URL]@");
        foreach (var name in new[] { "AI_API_KEY", "AI_BASE_URL" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 3 } value) text = text.Replace(value, "[REDACTED configuration]", StringComparison.Ordinal);
        return text;
    }

    private string[] Lines(string relative)
    {
        var path = Resolve(relative);
        // Bound the actual read too, in case the file grows after the metadata check.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bytes = new byte[MaxBytes + 1];
        var count = 0;
        while (count < bytes.Length) { var n = stream.Read(bytes, count, bytes.Length - count); if (n == 0) break; count += n; }
        if (count > MaxBytes || bytes.AsSpan(0, count).Contains((byte)0)) throw new UnauthorizedAccessException("Binary or oversized file.");
        return Redact(new UTF8Encoding(false, true).GetString(bytes, 0, count)).Replace("\r\n", "\n").Split('\n');
    }

    [Description("List readable project-relative files. Excluded directories and sensitive files are omitted; output is bounded.")]
    public string ListFiles() => Redact(string.Join('\n', Files().Take(MaxFiles)) + "\n[limit: 2000 files; ignored files omitted]");

    [Description("Read a UTF-8 source file with original 1-based line numbers. Use a project-relative path, startLine >= 1, maxLines <= 200.")]
    public string ReadFile(string path, int startLine = 1, int maxLines = 120)
    {
        if (startLine < 1 || maxLines < 1 || maxLines > 200) throw new ArgumentOutOfRangeException(nameof(startLine));
        var lines = Lines(path);
        var result = Redact(string.Join('\n', lines.Skip(startLine - 1).Take(maxLines).Select((line, i) => $"{path}:{startLine + i}: {line}")));
        return result.Length <= 24000 ? result : result[..24000] + "\n[truncated]";
    }

    [Description("Search readable project source for a literal keyword, case-insensitive. Returns up to 100 file:line matches; no regex or shell.")]
    public string SearchCode(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword) || keyword.Length > 200) throw new ArgumentException("Use a keyword of 1-200 characters.");
        var result = new StringBuilder(); var hits = 0;
        foreach (var file in Files().Take(MaxFiles))
        {
            string[] lines;
            try { lines = Lines(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException) { continue; }
            for (var i = 0; i < lines.Length; i++)
                if (lines[i].Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    result.AppendLine($"{file}:{i + 1}: {lines[i][..Math.Min(lines[i].Length, 500)]}");
                    if (++hits >= 100 || result.Length > 24000) return Redact(result + "\n[truncated]");
                }
        }
        return Redact(result.Length == 0 ? "No matches in readable files." : result.ToString());
    }

    private async Task<string> Git(params string[] arguments)
    {
        // Require an ordinary .git directory inside the selected root. No parent repositories or worktree pointers.
        var metadata = Path.Combine(Root, ".git");
        if (!Directory.Exists(metadata)) throw new InvalidOperationException("No standalone Git repository at project root.");
        RejectLink(metadata); RejectLink(Root);
        var start = new ProcessStartInfo("git") { WorkingDirectory = Root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        // Ignore inherited Git path/config overrides; never inherit arbitrary execution settings.
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        foreach (var arg in new[] { "--no-pager", "-c", "core.fsmonitor=false", "-c", "core.hooksPath=", "-c", "diff.external=" }.Concat(arguments)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Git unavailable.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        async Task<string> Bounded(StreamReader reader)
        {
            var result = new StringBuilder(); var buffer = new char[4096]; int n;
            while ((n = await reader.ReadAsync(buffer.AsMemory(), timeout.Token)) > 0)
            {
                result.Append(buffer, 0, n);
                if (result.Length > 1024 * 1024) throw new IOException("Git output exceeds limit.");
            }
            return result.ToString();
        }
        try
        {
            var stdout = Bounded(process.StandardOutput); var stderr = Bounded(process.StandardError);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token));
            if (process.ExitCode != 0) throw new InvalidOperationException("Git read failed.");
            return await stdout;
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    [Description("Get Git status for readable files within this root. Includes staged, unstaged and untracked status; excludes sensitive files.")]
    public async Task<string> GetGitStatus()
    {
        var allowed = Files().Take(MaxFiles).ToHashSet(StringComparer.Ordinal);
        var raw = await Git("status", "--porcelain=v1", "-z", "--untracked-files=all", "--no-renames");
        return Redact(string.Join('\n', raw.Split('\0').Where(row => row.Length > 3 && allowed.Contains(row[3..])).Select(row => row[..2] + " " + row[3..])));
    }

    [Description("Get staged and unstaged Git diffs for readable project files. Untracked files have status but no diff. Output may be truncated; never infer author intent as fact.")]
    public async Task<string> GetGitDiff()
    {
        var result = new StringBuilder();
        var readable = Files().Take(MaxFiles).ToHashSet(StringComparer.Ordinal);
        // Include deleted source paths only when their parent still passes the same path checks.
        var changes = await Git("status", "--porcelain=v1", "-z", "--untracked-files=all", "--no-renames");
        foreach (var row in changes.Split('\0').Where(r => r.Length > 3 && !r.StartsWith("??")))
        {
            var file = row[3..];
            if (!readable.Contains(file))
            {
                if (!row[..2].Contains('D') || Sensitive(Path.GetFileName(file)) || !Extensions.Contains(Path.GetExtension(file))) continue;
                try
                {
                    if (Path.IsPathRooted(file) || file.Contains(':') || file.Split(['/', '\\']).Any(p => p is ".." or "." || Ignored.Contains(p) || Sensitive(p))) continue;
                    var parent = Path.GetDirectoryName(file);
                    if (string.IsNullOrEmpty(parent)) RejectLink(Root); else Resolve(parent, directory: true);
                }
                catch { continue; }
            }
            else Resolve(file);
            foreach (var staged in new[] { false, true })
            {
                var args = new List<string> { "--literal-pathspecs", "diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--no-color", "--unified=3" };
                if (staged) args.Add("--cached");
                args.Add("--"); args.Add(file);
                var diff = Redact(await Git(args.ToArray()));
                if (diff.Length == 0) continue;
                result.AppendLine(staged ? "[staged]" : "[unstaged]");
                result.Append(diff.AsSpan(0, Math.Min(diff.Length, 32000 - result.Length)));
                if (result.Length >= 31000) return Redact(result + "\n[diff truncated]");
            }
        }
        return result.Length == 0 ? "No visible staged or unstaged diff. Untracked or excluded files may exist; use GetGitStatus." : Redact(result.ToString());
    }
}
