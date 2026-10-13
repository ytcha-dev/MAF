using MAF.Agents.Comprehension;
using System.Diagnostics;

namespace MAF.Tests;

public sealed class ToolTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pca-" + Guid.NewGuid());
    private readonly ProjectTools tools;
    public ToolTests()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Program.cs"), "class Program\n// entry point\nvar password = \"do-not-send-this\";\n");
        File.WriteAllText(Path.Combine(root, ".env"), "AI_API_KEY=hidden-value");
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        File.WriteAllText(Path.Combine(root, "bin", "hidden.cs"), "hidden");
        tools = new(root);
    }
    [Fact] public void ListsAndSearchesWithEvidence()
    {
        Assert.Contains("Program.cs", tools.ListFiles());
        Assert.DoesNotContain(".env", tools.ListFiles());
        Assert.DoesNotContain("hidden.cs", tools.ListFiles());
        Assert.Contains("Program.cs:2:", tools.SearchCode("entry"));
        Assert.Contains("Program.cs:2: // entry point", tools.ReadFile("Program.cs", 2, 1));
    }
    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData(".env")]
    [InlineData("bin/hidden.cs")]
    [InlineData("Program.cs:stream")]
    [InlineData(".git/config")]
    public void RejectsUnsafePaths(string path) => Assert.Throws<UnauthorizedAccessException>(() => tools.ReadFile(path));
    [Fact] public void RedactsReadAndSearch()
    {
        Assert.DoesNotContain("do-not-send-this", tools.ReadFile("Program.cs"));
        Assert.DoesNotContain("do-not-send-this", tools.SearchCode("password"));
        Assert.Contains("REDACTED", tools.ReadFile("Program.cs"));
    }
    [Fact] public void RejectsBinaryAndLargeFiles()
    {
        File.WriteAllBytes(Path.Combine(root, "binary.cs"), [65, 0, 66]);
        File.WriteAllText(Path.Combine(root, "large.cs"), new string('x', 300000));
        Assert.Throws<UnauthorizedAccessException>(() => tools.ReadFile("binary.cs"));
        Assert.Throws<UnauthorizedAccessException>(() => tools.ReadFile("large.cs"));
    }
    [Fact] public void RejectsJunctions()
    {
        var link = Path.Combine(root, "link");
        if (OperatingSystem.IsWindows())
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", link, Path.GetTempPath() }, CreateNoWindow = true, RedirectStandardOutput = true });
            process!.WaitForExit(); Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, Path.GetTempPath());
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => tools.ReadFile("link/outside.cs"));
            Assert.Throws<UnauthorizedAccessException>(() => new ProjectTools(link));
            Assert.DoesNotContain("link", tools.ListFiles());
        }
        finally { Directory.Delete(link); }
    }
    private async Task Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode);
    }
    [Fact] public async Task ReadsStagedAndUnstagedDiffAndRedactsSecrets()
    {
        await Git("init"); await Git("add", "Program.cs");
        await Git("-c", "user.name=PCA Test", "-c", "user.email=pca@example.invalid", "commit", "-m", "baseline");
        File.AppendAllText(Path.Combine(root, "Program.cs"), "// staged change\n");
        await Git("add", "Program.cs");
        File.AppendAllText(Path.Combine(root, "Program.cs"), "// unstaged change\nvar apiKey = \"hidden-diff-value\";\n");
        await Git("add", "-f", ".env");
        var diff = await tools.GetGitDiff();
        Assert.Contains("[staged]", diff); Assert.Contains("[unstaged]", diff);
        Assert.Contains("staged change", diff); Assert.Contains("unstaged change", diff);
        Assert.DoesNotContain("hidden-diff-value", diff); Assert.DoesNotContain("hidden-value", diff);
        var status = await tools.GetGitStatus(); Assert.Contains("MM Program.cs", status); Assert.DoesNotContain(".env", status);
        File.Delete(Path.Combine(root, "Program.cs"));
        Assert.Contains("diff --git", await tools.GetGitDiff());
    }
    [Fact] public async Task RejectsParentGitRepository() => await Assert.ThrowsAsync<InvalidOperationException>(() => tools.GetGitStatus());
    [Theory]
    [InlineData("password = 'hidden'")]
    [InlineData("{\"ApiKey\": \"hidden\"}")]
    [InlineData("postgres://user:hidden@localhost/db")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nhidden\n-----END PRIVATE KEY-----")]
    public void RedactsCommonCredentials(string content) => Assert.DoesNotContain("hidden", ProjectTools.Redact(content));
    public void Dispose()
    {
        // Git objects are read-only on Windows. Only clean this test's owned temporary directory.
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root));
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }
}
