using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using MAF.Core;
using MAF.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

// Explicitly invoked acceptance executable; never loaded by the production Host.
if (args.Length == 0) throw new ArgumentException("run <Host.dll> <new-local-run-directory> | seed <db> <checkpoint> | lock <db> | inspect <db>");
if (args[0] == "run") { await Acceptance.Run(args[1], args[2]); return; }
if (args[0] == "live") { await LiveAcceptance.Run(args[1], args[2], args[3], args[4]); return; }
var db = Path.GetFullPath(args[1]);
var store = new SqliteExecutionStore(db);
if (args[0] == "inspect")
{
    Console.WriteLine(JsonSerializer.Serialize(await Evidence.Inspect(db)));
    return;
}
await using var owner = await store.TryAcquireAsync("synthetic-task");
if (args[0] == "lock") { Console.WriteLine(owner is null ? "blocked" : "acquired"); return; }
if (owner is null) throw new InvalidOperationException("Seed ownership unavailable");
var now = DateTimeOffset.UtcNow;
var record = new DurableExecution("synthetic-task", new string('A', 64), new string('B', 64), "synthetic-root",
    "synthetic-run", new("synthetic-execution", "OpenCode", ExternalRequestId: "msg_synthetic"),
    DurableExecutionState.Created, SubmissionPhase.None, now, now);
await store.CreateAsync(record, owner);
var states = new[] { DurableExecutionState.Created, DurableExecutionState.SessionCreating, DurableExecutionState.ReadyToSubmit,
    DurableExecutionState.SubmissionIntended, DurableExecutionState.Running, DurableExecutionState.Completed };
var phases = new[] { SubmissionPhase.None, SubmissionPhase.SessionCreationIntended, SubmissionPhase.SessionKnown,
    SubmissionPhase.PromptIntended, SubmissionPhase.Acknowledged, SubmissionPhase.Acknowledged };
var target = Array.IndexOf(states, Enum.Parse<DurableExecutionState>(args[2]));
if (target < 0) throw new ArgumentException("Unsupported checkpoint");
for (var i = 1; i <= target; i++)
{
    var next = record with { State = states[i], SubmissionPhase = phases[i], Version = record.Version + 1,
        UpdatedAt = DateTimeOffset.UtcNow, Handle = i == 2 ? record.Handle with { ExternalSessionId = "ses_synthetic" } : record.Handle,
        CompletedAt = states[i] == DurableExecutionState.Completed ? DateTimeOffset.UtcNow : null };
    await store.UpdateAsync(record, next, owner);
    record = next;
}
// Demonstrate rejection without weakening the production validator or assertions.
try { await store.UpdateAsync(record, record with { WorkflowRunId = "rebound", Version = record.Version + 1 }, owner); throw new Exception("Invalid transition accepted"); }
catch (InvalidOperationException) { }
try { await store.CreateAsync(record with { State = DurableExecutionState.Created, SubmissionPhase = SubmissionPhase.None,
    Version = 0, CompletedAt = null, Handle = record.Handle with { ExternalSessionId = null } }, owner); throw new Exception("Duplicate accepted"); }
catch (SqliteException e) when (e.SqliteErrorCode == 19) { }
Console.WriteLine("committed");
await Task.Delay(TimeSpan.FromSeconds(60)); // bounded holding gate, driver kills only this process after signal.

static class Evidence
{
    public static async Task<DurableExecution?> Snapshot(string db)
    {
        if (!File.Exists(db)) return null;
        await using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await c.OpenAsync();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT snapshot FROM executions LIMIT 1";
        try { return await cmd.ExecuteScalarAsync() is string text ? JsonSerializer.Deserialize<DurableExecution>(text) : null; }
        catch (SqliteException e) when (e.SqliteErrorCode == 1) { return null; } // schema may not have committed yet
    }
    public static async Task<object> Inspect(string db)
    {
        Acceptance.Require(File.Exists(db), "Inspection requires an existing database");
        var snapshot = await Snapshot(db) ?? throw new InvalidOperationException("Missing snapshot");
        var record = await new SqliteExecutionStore(db).GetAsync(snapshot.TaskId);
        await using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await c.OpenAsync();
        async Task<object?> Scalar(string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return await cmd.ExecuteScalarAsync(); }
        var integrity = await Scalar("PRAGMA integrity_check");
        var schema = await Scalar("PRAGMA user_version");
        var count = await Scalar("SELECT count(*) FROM executions");
        var consistent = await Scalar("SELECT count(*) FROM executions WHERE task_id=json_extract(snapshot,'$.TaskId') AND version=json_extract(snapshot,'$.Version') AND terminal=(json_extract(snapshot,'$.CompletedAt') IS NOT NULL)");
        Acceptance.Require(Equals(integrity, "ok") && Convert.ToInt32(schema) == 1 && Convert.ToInt32(count) == 1 && Convert.ToInt32(consistent) == 1, "Integrity/schema/count/snapshot mismatch");
        return new { integrity, schema, count, consistent, sqliteVersion = await Scalar("SELECT sqlite_version()"), record };
    }
}

sealed class Child : IDisposable
{
    public Process Process { get; }
    public int Id { get; }
    public DateTime StartTime { get; }
    public string Executable { get; }
    public string[] Arguments { get; }
    private readonly Task<string>? output;
    private readonly Task<string> error;
    public Child(string[] arguments, Dictionary<string, string>? environment = null, bool lineSignal = false, string? executable = null, string? workingDirectory = null)
    {
        Executable = executable ?? Environment.ProcessPath!;
        if (!Path.IsPathFullyQualified(Executable))
            Executable = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Select(directory => Path.Combine(directory, Executable)).FirstOrDefault(File.Exists)
                ?? throw new FileNotFoundException("Child executable unavailable");
        // The acceptance runner is invoked through dotnet, not its generated apphost.
        if (executable is null && !Path.GetFileNameWithoutExtension(Executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invoke this runner as dotnet MAF.Acceptance.dll");
        Arguments = arguments;
        var start = new ProcessStartInfo(Executable) { RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true };
        if (workingDirectory is not null) start.WorkingDirectory = workingDirectory;
        foreach (var a in arguments) start.ArgumentList.Add(a);
        foreach (var name in new[] { "OPENCODE_SERVER_USERNAME", "OPENCODE_SERVER_PASSWORD", "MAF_EXECUTION_DB", "MAF_LIVE_OPENCODE" }) start.Environment.Remove(name);
        if (executable is not null)
            foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("OPENCODE_", StringComparison.OrdinalIgnoreCase)
                || k.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase) || k == "ANTHROPIC_AUTH_TOKEN").ToArray()) start.Environment.Remove(name);
        if (environment is not null) foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        Process = Process.Start(start)!;
        Id = Process.Id; StartTime = Process.StartTime;
        if (!lineSignal) output = Process.StandardOutput.ReadToEndAsync();
        error = Process.StandardError.ReadToEndAsync();
    }
    public object Identity => new { pid = Id, startTimeUtc = StartTime.ToUniversalTime(), executable = Path.GetFileName(Executable), argumentTarget = Path.GetFileName(Arguments[0]) };
    public async Task<string> Signal() => await Process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)) ?? throw new Exception("Missing signal");
    public void Kill()
    {
        if (Process.HasExited) return;
        Acceptance.Require(Process.Id == Id && Process.StartTime == StartTime && Process.MainModule!.FileName == Executable, "Process identity changed");
        Process.Kill(entireProcessTree: false);
    }
    public async Task<(int Exit, string Output)> Finish(int seconds = 30)
    {
        await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(seconds));
        var stderr = await error;
        if (Process.ExitCode is not (0 or 4 or 5 or 130) && !string.IsNullOrWhiteSpace(stderr)) Console.Error.WriteLine(stderr);
        return (Process.ExitCode, output is null ? "" : await output);
    }
    public void Dispose() { Kill(); Process.WaitForExit(5000); Process.Dispose(); }
}

static class Acceptance
{
    public static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    public static async Task Until(Func<Task<bool>> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (!await condition()) await Task.Delay(25, deadline.Token);
    }
    public static async Task Run(string host, string root)
    {
        host = Path.GetFullPath(host); root = Path.GetFullPath(root);
        Require(File.Exists(host) && !Directory.Exists(root), "Host missing or run directory already exists");
        Directory.CreateDirectory(root);
        var self = Assembly.GetExecutingAssembly().Location;
        var evidence = new List<object>();
        async Task Save(object item) { evidence.Add(item); await File.WriteAllTextAsync(Path.Combine(root, "evidence.json"), JsonSerializer.Serialize(evidence, ExternalTaskResult.JsonOptions)); Console.WriteLine($"Passed acceptance case {evidence.Count}"); }
        foreach (var checkpoint in new[] { "Created", "SessionCreating", "ReadyToSubmit", "SubmissionIntended", "Running", "Completed" })
        {
            var db = Path.Combine(root, "C-" + checkpoint + ".db");
            using var writer = new Child([self, "seed", db, checkpoint], lineSignal: true);
            Require(await writer.Signal() == "committed", "Missing committed checkpoint");
            var before = await Evidence.Snapshot(db);
            using (var competitor = new Child([self, "lock", db]))
            { var r = await competitor.Finish(); Require(r.Exit == 0 && r.Output.Trim() == "blocked", "Cross-process ownership not exclusive"); }
            Require(!writer.Process.HasExited, "Writer exited before crash");
            writer.Kill(); await writer.Finish();
            using (var successor = new Child([self, "lock", db]))
            { var r = await successor.Finish(); Require(r.Exit == 0 && r.Output.Trim() == "acquired", "OS did not release lock"); }
            using var reader = new Child([self, "inspect", db]);
            var inspected = await reader.Finish(); Require(inspected.Exit == 0, "Independent inspection failed");
            Require(before == await Evidence.Snapshot(db), "Snapshot changed after restart");
            await Save(new { stage = "C", checkpoint, writer = writer.Identity, reader = reader.Identity, snapshot = before,
                inspection = JsonSerializer.Deserialize<JsonElement>(inspected.Output), exclusive = true, releasedAfterKill = true, outcome = "Passed" });
        }
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "README.md"), "Synthetic acceptance fixture. No model access.");
        foreach (var variant in new[] { "active", "lost-response", "unavailable", "completed-while-stopped", "running", "permission", "cancelled" })
        {
            await using var provider = new ControlledProvider(source, variant == "lost-response");
            var db = Path.Combine(root, "D-" + variant + ".db");
            var env = new Dictionary<string,string> { ["OPENCODE_BASE_URL"] = provider.Origin, ["OPENCODE_PROJECT_ROOT"] = source,
                ["OPENCODE_AGENT"] = "maf_readonly", ["OPENCODE_ALLOW_LOOPBACK_HTTP"] = "1" };
            string[] HostArgs(bool recover, int timeout = 20) => [host, "--project", source, "--execution-db", db,
                recover ? "--recover" : "--task-id", "synthetic-task", ..(recover ? Array.Empty<string>() : new[] { "--external-task", "Read README.md only." }),
                "--external-timeout", timeout.ToString(), "--output", "json"];
            using var original = new Child(HostArgs(false), env);
            await provider.Accepted.Task.WaitAsync(TimeSpan.FromSeconds(25));
            DurableExecution? before = null;
            await Until(async () => { before = await Evidence.Snapshot(db); return before?.State == (variant == "lost-response" ? DurableExecutionState.SubmissionIntended : DurableExecutionState.Running); });
            Require(!original.Process.HasExited && provider.Sessions == 1 && provider.Prompts == 1, "Not an active one-submission checkpoint");
            original.Kill(); await original.Finish();
            var countersBefore = new { sessions = provider.Sessions, prompts = provider.Prompts };
            if (variant == "unavailable") provider.Mode = "unavailable";
            if (variant == "completed-while-stopped") provider.Mode = "completed";
            if (variant == "permission") provider.Mode = "permission";
            if (variant == "cancelled") provider.Mode = "cancelled";
            var observationsBefore = provider.Observations;
            using var recovered = new Child(HostArgs(true, variant == "running" ? 2 : 20), env);
            if (variant is "active" or "lost-response")
            {
                await Until(() => Task.FromResult(provider.Observations > observationsBefore));
                Require(!recovered.Process.HasExited, "Recovery did not observe active session");
                provider.Mode = "completed";
            }
            var result = await recovered.Finish();
            var expected = variant switch { "unavailable" or "running" => "Unknown", "permission" => "WaitingForApproval", "cancelled" => "Cancelled", _ => "Completed" };
            ValidateResult(result, expected, before!);
            object? subsequent = null;
            if (variant is "unavailable" or "running" or "permission")
            {
                provider.Mode = "completed";
                using var later = new Child(HostArgs(true), env);
                var final = await later.Finish(); ValidateResult(final, "Completed", before!);
                subsequent = new { process = later.Identity, status = "Completed" };
            }
            var after = await Evidence.Snapshot(db);
            Require(after!.TaskId == before!.TaskId && after.Handle == before.Handle && after.WorkflowRunId == before.WorkflowRunId
                && after.RequestFingerprint == before.RequestFingerprint && after.ProviderScope == before.ProviderScope, "Durable identity changed");
            Require(provider.Sessions == 1 && provider.Prompts == 1 && provider.Aborts == 0, "Recovery created, submitted, or aborted unexpectedly");
            using var inspection = new Child([self, "inspect", db]);
            var integrity = await inspection.Finish(); Require(integrity.Exit == 0, "Post-Host integrity failed");
            await Save(new { stage = "D", variant, original = original.Identity, recovered = recovered.Identity,
                checkpoint = before, recoveredSnapshot = after, status = expected, exit = result.Exit, countersBefore,
                countersAfter = new { sessions = provider.Sessions, prompts = provider.Prompts, aborts = provider.Aborts },
                subsequent, inspection = JsonSerializer.Deserialize<JsonElement>(integrity.Output), outcome = "Passed" });
        }
    }
    static void ValidateResult((int Exit, string Output) result, string status, DurableExecution before)
    {
        var expectedExit = status switch { "Completed" => 0, "Unknown" => 4, "WaitingForApproval" => 5, "Cancelled" => 130, _ => -1 };
        Require(result.Exit == expectedExit, $"Unexpected exit {result.Exit}; expected {expectedExit}. stdout: {result.Output}");
        var r = JsonDocument.Parse(result.Output).RootElement.GetProperty("result");
        Require(r.GetProperty("status").GetString() == status && r.GetProperty("taskId").GetString() == before.TaskId
            && r.GetProperty("workflowRunId").GetString() == before.WorkflowRunId
            && r.GetProperty("handle").Deserialize<ExternalExecutionHandle>(ExternalTaskResult.JsonOptions) == before.Handle, "Output identity/status mismatch");
        if (status == "Completed") Require(r.GetProperty("summary").GetString() == "Synthetic recovered result.", "Recovered wrong result body");
    }
}
