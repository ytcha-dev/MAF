using System.Text;
using MAF.Core;
using MAF.Persistence.Sqlite;
using MAF.Workflows.External;
using MAF.Workflows.External.OpenCode;
using Microsoft.Extensions.DependencyInjection;

namespace MAF.Host;

internal static class ExternalConsole
{
    public static async Task<int> RunAsync(string[] args, string project, Func<string, string> option)
    {
        var outputFormat = ExternalConsoleOutput.Parse(args);
        if (new[] { "--external-task", "--recover", "--recover-pending" }.Count(mode => args.Contains(mode)) != 1)
            throw new ArgumentException("Choose exactly one external execution or recovery mode.");
        if (args.Contains("--ask") || args.Contains("--review"))
        {
            throw new ArgumentException("Choose one CLI mode.");
        }
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new ArgumentException($"Please set {name}.");
        var configuration = new OpenCodeOptions(new Uri(Required("OPENCODE_BASE_URL")), Required("OPENCODE_PROJECT_ROOT"),
            Environment.GetEnvironmentVariable("OPENCODE_AGENT") ?? "maf_readonly",
            Environment.GetEnvironmentVariable("OPENCODE_ALLOW_LOOPBACK_HTTP") == "1",
            Environment.GetEnvironmentVariable("OPENCODE_SERVER_USERNAME"), Environment.GetEnvironmentVariable("OPENCODE_SERVER_PASSWORD"));
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        var client = new OpenCodeClient(http, configuration);
        services.AddSingleton<IExternalHarnessClient>(client);
        services.AddSingleton(provider => new ExternalWorkflow(provider.GetRequiredService<IExternalHarnessClient>()));
        var database = args.Contains("--execution-db") ? option("--execution-db") : Environment.GetEnvironmentVariable("MAF_EXECUTION_DB");
        if (database is not null)
        {
            services.AddSingleton<IExecutionStore>(new SqliteExecutionStore(DurableDatabasePath.Validate(database, project)));
            services.AddSingleton(provider => new DurableExecutionService(provider.GetRequiredService<IExecutionStore>(), client));
        }
        else if (args.Contains("--recover") || args.Contains("--recover-pending"))
            throw new ArgumentException("Recovery requires --execution-db or MAF_EXECUTION_DB.");
        using var provider = services.BuildServiceProvider();
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stop.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            var taskId = args.Contains("--task-id") ? option("--task-id") : Guid.NewGuid().ToString("N");
            var timeout = args.Contains("--external-timeout") ? int.Parse(option("--external-timeout")) : 120;
            if (timeout < 1 || timeout > 600) throw new ArgumentException("External timeout must be 1 through 600 seconds.");
            Console.Error.WriteLine("Official Workflow graph: ExternalTaskRequest -> OpenCodeExecutor -> ExternalTaskResult");
            var durable = database is null ? null : provider.GetRequiredService<DurableExecutionService>();
            if (args.Contains("--recover-pending"))
            {
                var records = await durable!.ListRecoverableAsync(stop.Token);
                var runs = new List<ExternalWorkflowRun>();
                foreach (var record in records)
                    runs.Add(await durable.RecoverAsync(record.TaskId, project, TimeSpan.FromSeconds(timeout), observeOnce: true, cancellationToken: stop.Token));
                ExternalConsoleOutput.WriteMany(Console.Out, runs, outputFormat);
                return runs.Count == 0 ? 0 : runs.Select(item => ExitCode(item.Result.Status)).FirstOrDefault(code => code != 0);
            }
            ExternalWorkflowRun run;
            if (args.Contains("--recover"))
                run = await durable!.RecoverAsync(option("--recover"), project, TimeSpan.FromSeconds(timeout), cancellationToken: stop.Token);
            else
            {
                var request = new ExternalTaskRequest(taskId, project, option("--external-task"), TimeSpan.FromSeconds(timeout));
                run = durable is null ? await provider.GetRequiredService<ExternalWorkflow>().RunAsync(request, stop.Token)
                    : await durable.RunAsync(request, stop.Token);
            }
            ExternalConsoleOutput.Write(Console.Out, run, outputFormat);
            return ExitCode(run.Result.Status);
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    internal static int ExitCode(ExternalExecutionStatus status) => status switch
        {
            ExternalExecutionStatus.Completed => 0,
            ExternalExecutionStatus.Partial => 3,
            ExternalExecutionStatus.Unknown => 4,
            ExternalExecutionStatus.WaitingForApproval => 5,
            ExternalExecutionStatus.Cancelled => 130,
            _ => 1
        };
}
