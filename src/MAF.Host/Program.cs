using MAF.Core.Tools;
using MAF.Agents.Review;
using MAF.Agents.Comprehension;
using MAF.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;
using System.ClientModel;

if (args.Contains("--help") || args.Length == 0)
{
    Console.WriteLine("pca --project <directory> [--ask <question>]\npca --project <directory> --review [--scope All|Staged|Unstaged] [--focus <text>] [--context-limit <characters>] [--diagnostics <relative.json>]\npca --project <approved-directory> --external-task <text> [--task-id <id>] [--external-timeout <seconds>] [--output json|text|verbose]\nExternal output: json (default, UTF-8), text (summary), verbose (diagnostics). Invalid output arguments: exit 2.\nExternal environment: OPENCODE_BASE_URL, OPENCODE_PROJECT_ROOT, OPENCODE_AGENT, OPENCODE_ALLOW_LOOPBACK_HTTP=1; optional OPENCODE_SERVER_USERNAME/PASSWORD\nReview emits JSON; exit codes: 0 Completed, 3 Partial, 1 Failed.\nEnvironment: AI_BASE_URL, AI_API_KEY, AI_MODEL\nExit: /exit; reset session: /reset");
    return 0;
}
try
{
    string Option(string name) => Array.IndexOf(args, name) is var index && index >= 0 && index + 1 < args.Length
        ? args[index + 1] : throw new ArgumentException($"Missing {name}");
    var project = Path.GetFullPath(Option("--project"));
    if (!Directory.Exists(project)) throw new ArgumentException("Project directory does not exist.");
    if (args.Contains("--external-task"))
    {
        return await MAF.Host.ExternalConsole.RunAsync(args, project, Option);
    }
    string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new ArgumentException($"Please set {name}.");
    var endpoint = new Uri(Required("AI_BASE_URL"));
    if (endpoint.Scheme != "https" && !(endpoint.Scheme == "http" && endpoint.IsLoopback))
        throw new ArgumentException("AI_BASE_URL requires HTTPS, or HTTP on localhost.");
    var client = new OpenAIClient(new ApiKeyCredential(Required("AI_API_KEY")),
        new OpenAIClientOptions { Endpoint = endpoint }).GetChatClient(Required("AI_MODEL")).AsIChatClient();
    var services = new ServiceCollection();
    services.AddSingleton<IChatClient>(client);
    services.AddSingleton(new ProjectTools(project));
    services.AddSingleton<IAgentFactory, ComprehensionAgentFactory>();
    services.AddSingleton<IReviewService, ReviewService>();
    using var provider = services.BuildServiceProvider();
    if (args.Contains("--review"))
    {
        var scope = args.Contains("--scope") ? Enum.Parse<ReviewScope>(Option("--scope"), ignoreCase: true) : ReviewScope.All;
        var focus = args.Contains("--focus") ? Option("--focus") : "Correctness, Security, Maintainability, Readability";
        var limit = args.Contains("--context-limit") ? int.Parse(Option("--context-limit")) : 64000;
        var diagnostics = args.Contains("--diagnostics") ? Option("--diagnostics") : null;
        using var reviewStop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            reviewStop.Cancel();
        };
        var result = await provider.GetRequiredService<IReviewService>()
            .ReviewAsync(new(project, scope, focus, limit, DiagnosticsFile: diagnostics), reviewStop.Token);
        Console.WriteLine(result.ToJson());
        return result.Status == ReviewStatus.Completed ? 0 : result.Status == ReviewStatus.Partial ? 3 : 1;
    }
    AIAgent agent = provider.GetRequiredService<IAgentFactory>().Create();
    var tools = provider.GetRequiredService<ProjectTools>();
    var session = await agent.CreateSessionAsync();
    Console.WriteLine($"PCA Agent Ready\nProject: {project}");
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    var once = args.Contains("--ask");
    while (!stop.IsCancellationRequested)
    {
        if (!once) Console.Write("You > ");
        var question = once ? Option("--ask") : Console.ReadLine();
        if (question is null || question == "/exit") break;
        if (question == "/reset") { session = await agent.CreateSessionAsync(); continue; }
        if (string.IsNullOrWhiteSpace(question)) continue;
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        turn.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var before = tools.SuccessfulCalls;
            var answer = await agent.RunAsync(question, session, cancellationToken: turn.Token);
            Console.WriteLine($"Agent > {answer}");
            if (tools.SuccessfulCalls == before)
                Console.WriteLine("[本回合未成功取得新工具證據。若詢問專案內容，請確認模型支援 Function Calling；回答可能僅依據先前對話。]");
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Turn cancelled or timed out; session reset."); session = await agent.CreateSessionAsync(); }
        catch (Exception) { Console.Error.WriteLine("Model request failed. Check endpoint, credentials, model and Function Calling support. Session reset."); session = await agent.CreateSessionAsync(); if (once) return 1; }
        if (once) break;
    }
    return 0;
}
catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 2; }
catch (Exception) { Console.Error.WriteLine("PCA could not start. Check configuration and project access."); return 1; }
