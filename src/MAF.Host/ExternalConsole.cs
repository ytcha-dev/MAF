using System.Text.Json;
using MAF.Core;
using MAF.Workflows.External;
using MAF.Workflows.External.OpenCode;
using Microsoft.Extensions.DependencyInjection;

namespace MAF.Host;

internal static class ExternalConsole
{
    public static async Task<int> RunAsync(string[] args, string project, Func<string, string> option)
    {
        if (args.Contains("--ask") || args.Contains("--review"))
        {
            throw new ArgumentException("Choose one CLI mode.");
        }
        string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new ArgumentException($"Please set {name}.");
        var configuration = new OpenCodeOptions(new Uri(Required("OPENCODE_BASE_URL")), Required("OPENCODE_PROJECT_ROOT"),
            Environment.GetEnvironmentVariable("OPENCODE_AGENT") ?? "maf_readonly",
            Environment.GetEnvironmentVariable("OPENCODE_ALLOW_LOOPBACK_HTTP") == "1",
            Environment.GetEnvironmentVariable("OPENCODE_SERVER_USERNAME"), Environment.GetEnvironmentVariable("OPENCODE_SERVER_PASSWORD"));
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSingleton<IExternalHarnessClient>(new OpenCodeClient(http, configuration));
        services.AddSingleton(provider => new ExternalWorkflow(provider.GetRequiredService<IExternalHarnessClient>()));
        using var provider = services.BuildServiceProvider();
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stop.Cancel();
        };
        var taskId = args.Contains("--task-id") ? option("--task-id") : Guid.NewGuid().ToString("N");
        var timeout = args.Contains("--external-timeout") ? int.Parse(option("--external-timeout")) : 120;
        var request = new ExternalTaskRequest(taskId, project, option("--external-task"), TimeSpan.FromSeconds(timeout));
        Console.Error.WriteLine("Official Workflow graph: ExternalTaskRequest -> OpenCodeExecutor -> ExternalTaskResult");
        var run = await provider.GetRequiredService<ExternalWorkflow>().RunAsync(request, stop.Token);
        Console.WriteLine(JsonSerializer.Serialize(run, ExternalTaskResult.JsonOptions));
        return run.Result.Status switch
        {
            ExternalExecutionStatus.Completed => 0,
            ExternalExecutionStatus.Partial => 3,
            ExternalExecutionStatus.Unknown => 4,
            ExternalExecutionStatus.WaitingForApproval => 5,
            ExternalExecutionStatus.Cancelled => 130,
            _ => 1
        };
    }
}
