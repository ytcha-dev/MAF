using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MAF.Core;
using MAF.Workflows.External;
using MAF.Workflows.External.OpenCode;

namespace MAF.Tests;

public sealed class OpenCodeLiveFactAttribute : FactAttribute
{
    public OpenCodeLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MAF_LIVE_OPENCODE") != "1")
        {
            Skip = "Opt-in live OpenCode test; requires an approved test-owned fixture and server.";
        }
    }
}

public sealed class OpenCodeLiveTests
{
    private static string Root()
    {
        var root = Environment.GetEnvironmentVariable("MAF_OPENCODE_TEST_ROOT") ?? throw new InvalidOperationException("Missing approved fixture root.");
        var full = Path.GetFullPath(root);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), full, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("maf-phase2a-", Path.GetFileName(full));
        Assert.True(File.Exists(Path.Combine(full, "opencode.json")));
        return full;
    }

    private static OpenCodeOptions Options(string root, bool approval = false) => new(
        new Uri(Environment.GetEnvironmentVariable("OPENCODE_BASE_URL") ?? "http://127.0.0.1:4096"), root,
        AllowLoopbackHttp: true, Username: Environment.GetEnvironmentVariable("OPENCODE_SERVER_USERNAME"),
        Password: Environment.GetEnvironmentVariable("OPENCODE_SERVER_PASSWORD"), RequireReadApproval: approval);

    private static Dictionary<string, string> Hashes(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => !Path.GetRelativePath(root, path).StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        .ToDictionary(path => Path.GetRelativePath(root, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    private static async Task<string> GitStatus(string root)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-c", "core.fsmonitor=false", "--no-optional-locks", "status", "--porcelain=v1", "--untracked-files=all" })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        await error;
        return await output;
    }

    private static void Save(string name, object result)
    {
        var directory = Environment.GetEnvironmentVariable("MAF_OPENCODE_RESULTS_DIR") ?? throw new InvalidOperationException("Missing approved result directory.");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(result, ExternalTaskResult.JsonOptions));
    }

    [OpenCodeLiveFact]
    public async Task ThreeReadOnlyTasksRunThroughOfficialWorkflow()
    {
        var root = Root();
        var before = Hashes(root);
        var index = File.ReadAllBytes(Path.Combine(root, ".git", "index"));
        var status = await GitStatus(root);
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        var client = new OpenCodeClient(http, Options(root));
        var workflow = new ExternalWorkflow(client);
        var sessions = new HashSet<string>();
        for (var trial = 1; trial <= 3; trial++)
        {
            var request = new ExternalTaskRequest(Guid.NewGuid().ToString("N"), root,
                "Read README.md and Program.cs. Summarize the repository structure, name its entry point, and cite both filenames. Do not edit files, run commands, or access other directories.",
                TimeSpan.FromSeconds(120));
            var run = await workflow.RunAsync(request);
            var unchanged = before.OrderBy(pair => pair.Key).SequenceEqual(Hashes(root).OrderBy(pair => pair.Key))
                && index.SequenceEqual(File.ReadAllBytes(Path.Combine(root, ".git", "index")))
                && status == await GitStatus(root);
            Save("read-only-" + trial, new { client.ServerVersion, run, unchanged });
            Assert.True(unchanged);
            Assert.Equal(ExternalExecutionStatus.Completed, run.Result.Status);
            Assert.Contains("README.md", run.Result.Summary);
            Assert.Contains("Program.cs", run.Result.Summary);
            Assert.Contains("WorkflowOutputEvent", run.WorkflowEvents);
            Assert.True(sessions.Add(run.Result.Handle.ExternalSessionId!));
        }
    }

    [OpenCodeLiveFact]
    public async Task ReadPermissionRequestIsSurfacedAndNeverApproved()
    {
        var root = Root();
        var before = Hashes(root);
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        var client = new OpenCodeClient(http, Options(root, approval: true));
        var request = new ExternalTaskRequest(Guid.NewGuid().ToString("N"), root,
            "Use the read tool to read Program.cs before replying. Do not infer its contents. Do not edit or run commands.", TimeSpan.FromSeconds(90));
        var run = await new ExternalWorkflow(client).RunAsync(request);
        Save("permission", new { run, unchanged = before.OrderBy(pair => pair.Key).SequenceEqual(Hashes(root).OrderBy(pair => pair.Key)) });
        try
        {
            Assert.Equal(ExternalExecutionStatus.WaitingForApproval, run.Result.Status);
            Assert.Equal(before.OrderBy(pair => pair.Key), Hashes(root).OrderBy(pair => pair.Key));
        }
        finally
        {
            if (run.Result.Handle.ExternalSessionId is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await client.AbortAsync(run.Result.Handle, cleanup.Token);
            }
        }
    }

    [OpenCodeLiveFact]
    public async Task CancellationAttemptsAbortAndReportsEvidence()
    {
        var root = Root();
        var before = Hashes(root);
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        var client = new OpenCodeClient(http, Options(root));
        var request = new ExternalTaskRequest(Guid.NewGuid().ToString("N"), root,
            "Read README.md and Program.cs, then explain the console entry point and code behavior in a detailed 2000-word tutorial. Never modify files or execute commands.", TimeSpan.FromSeconds(120));
        using var stop = new CancellationTokenSource();
        var task = new ExternalWorkflow(client).RunAsync(request, stop.Token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (client.GetRequestCount(request.TaskId) < 11 && !task.IsCompleted)
        {
            await Task.Delay(100, deadline.Token);
        }
        stop.Cancel();
        var run = await task;
        Save("cancellation", new { run, unchanged = before.OrderBy(pair => pair.Key).SequenceEqual(Hashes(root).OrderBy(pair => pair.Key)) });
        Assert.True(run.Result.AbortRequested);
        Assert.Contains(run.Result.Status, new[] { ExternalExecutionStatus.Cancelled, ExternalExecutionStatus.Unknown, ExternalExecutionStatus.Completed });
        Assert.Equal(before.OrderBy(pair => pair.Key), Hashes(root).OrderBy(pair => pair.Key));
    }
}
