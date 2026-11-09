using MAF.Core;
using MAF.Core.Tools;
using MAF.Workflows.External.OpenCode;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;

namespace MAF.Host;

// Explicit personal opt-in route. Reads all settings from the environment; no secrets in arguments.
internal static class PipelineConsole
{
    public const string Help = "pca --project <directory> --pipeline <objective> [--stage-timeout <seconds 10-120>] [--output text|json]\n"
        + "Pipeline (personal read-only MVP): PCA -> OpenCode -> Evidence Reviewer -> Human gate via the official Workflow.\n"
        + "Pipeline environment: AI_BASE_URL, AI_API_KEY, AI_MODEL (PCA), optional AI_REVIEWER_MODEL; OPENCODE_BASE_URL (loopback), "
        + "OPENCODE_ALLOW_LOOPBACK_HTTP=1, optional OPENCODE_AGENT, OPENCODE_SERVER_USERNAME/PASSWORD. Exit: 0 Ready, 3 NeedsHumanReview, 5 WaitingForApproval, 1 Blocked.";

    internal sealed record Settings(string Objective, TimeSpan StageTimeout, bool Json);

    internal static Settings Parse(string[] args, Func<string, string> option)
    {
        if (args.Contains("--ask") || args.Contains("--review") || args.Contains("--external-task") || args.Contains("--recover") || args.Contains("--recover-pending"))
            throw new ArgumentException("Choose one CLI mode.");
        var objective = option("--pipeline");
        if (!HandoffValidation.Text(objective, 4000) || objective.Contains('\n') || objective.Contains('\r'))
            throw new ArgumentException("--pipeline requires a single-line objective of at most 4000 characters.");
        var seconds = 120;
        if (args.Contains("--stage-timeout") && !int.TryParse(option("--stage-timeout"), out seconds) || seconds is < 10 or > 120) throw new ArgumentException("--stage-timeout must be 10-120 seconds.");
        var output = args.Contains("--output") ? option("--output").ToLowerInvariant() : "text";
        if (output is not ("text" or "json")) throw new ArgumentException("--output must be text or json.");
        return new(objective, TimeSpan.FromSeconds(seconds), output == "json");
    }

    internal static OpenCodeOptions OpenCode(string workspace, Func<string, string?> env)
    {
        var baseUrl = env("OPENCODE_BASE_URL") is { Length: > 0 } value ? value : throw new ArgumentException("Please set OPENCODE_BASE_URL.");
        var options = new OpenCodeOptions(new Uri(baseUrl), workspace, env("OPENCODE_AGENT") is { Length: > 0 } agent ? agent : "maf_readonly",
            AllowLoopbackHttp: env("OPENCODE_ALLOW_LOOPBACK_HTTP") == "1", Username: env("OPENCODE_SERVER_USERNAME"),
            Password: env("OPENCODE_SERVER_PASSWORD"), RequestTimeoutSeconds: 30);
        options.Validate();
        return options;
    }

    public static async Task<int> RunAsync(string[] args, string project, Func<string, string> option)
    {
        var settings = Parse(args, option);
        var workspace = new ProjectTools(project).Root;
        string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new ArgumentException($"Please set {name}.");
        var endpoint = new Uri(Required("AI_BASE_URL"));
        if (endpoint.Scheme != "https" && !(endpoint.Scheme == "http" && endpoint.IsLoopback))
            throw new ArgumentException("AI_BASE_URL requires HTTPS, or HTTP on localhost.");
        var openAi = new OpenAIClient(new ApiKeyCredential(Required("AI_API_KEY")), new OpenAIClientOptions { Endpoint = endpoint });
        var pcaModel = Required("AI_MODEL");
        var reviewerModel = Environment.GetEnvironmentVariable("AI_REVIEWER_MODEL") is { Length: > 0 } m ? m : pcaModel;
        IChatClient pca = openAi.GetChatClient(pcaModel).AsIChatClient();
        IChatClient reviewer = openAi.GetChatClient(reviewerModel).AsIChatClient();
        var options = OpenCode(workspace, Environment.GetEnvironmentVariable);

        var id = Guid.NewGuid().ToString("N");
        var task = new HandoffTask("pipeline-" + id[..12], id, workspace, settings.Objective, settings.StageTimeout);
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.Error.WriteLine($"Pipeline started: PCA({pcaModel}) -> OpenCode({options.Agent}) -> Reviewer({reviewerModel}) -> HumanGate");
        using var transport = new HttpClientHandler();
        var result = await PersonalPipeline.RunAsync(task, pca, reviewer, options, transport, TimeSpan.FromSeconds(1), stop.Token);
        Console.WriteLine(settings.Json ? PersonalPipeline.Json(result) : PersonalPipeline.Text(result));
        return PersonalPipeline.ExitCode(result.Run.Outcome);
    }
}
