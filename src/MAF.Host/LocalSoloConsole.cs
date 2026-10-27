using MAF.Core;

namespace MAF.Host;

internal static class LocalSoloConsole
{
    public const string Help = "Offline Local Solo: --local config [--workspace <absolute-local-directory>] | --local readiness [--workspace <absolute-local-directory>] | --local preview --workspace <directory> --objective <text> | --local demo --workspace <directory> --objective <text> [--scenario completed|failed|unknown|waiting] | --local sample [--diagnostics] | --local submit (blocked: ProductionTrustUnavailable). Demo uses scripted participants and the existing Workflow; no provider/model calls or database writes.";

    public static int Run(string[] args, TextWriter output, TextWriter error,
        Func<string, string?>? configuration = null, TextReader? input = null)
        => RunAsync(args, output, error, configuration, input).GetAwaiter().GetResult();

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        Func<string, string?>? configuration = null, TextReader? input = null)
    {
        try
        {
            var (action, options) = Parse(args);
            if (action == "submit") LocalSoloApplication.Submit();
            if (action == "sample")
                LocalSoloPresentation.Result(output, LocalSoloPresentation.Sample(), options.ContainsKey("--diagnostics"));
            else if (action == "help") output.WriteLine(Help);
            else
            {
                configuration ??= Environment.GetEnvironmentVariable;
                if (action == "demo")
                    return await LocalSoloDemo.RunAsync(LocalSoloApplication.Preview(options["--workspace"], options["--objective"], configuration),
                        options.GetValueOrDefault("--scenario", "completed"), input ?? Console.In, output, error);
                else if (action == "readiness")
                    LocalSoloPresentation.Readiness(output, LocalSoloApplication.Profile(options.GetValueOrDefault("--workspace"), configuration));
                else if (action == "config")
                    LocalSoloPresentation.Profile(output, LocalSoloApplication.Profile(options.GetValueOrDefault("--workspace"), configuration));
                else
                    LocalSoloPresentation.Preview(output, LocalSoloApplication.Preview(options["--workspace"], options["--objective"], configuration));
            }
            return 0;
        }
        catch (ExternalHarnessException)
        {
            error.WriteLine(LocalSoloApplication.Blocker);
            return 1;
        }
        catch (ArgumentException)
        {
            // Never echo arbitrary argv, paths or configuration values on errors.
            error.WriteLine("InvalidLocalInput: use --local help; no execution attempted.");
            return 2;
        }
        catch (Exception)
        {
            error.WriteLine("LocalInspectionUnavailable: no execution attempted.");
            return 1;
        }
    }

    private static (string Action, Dictionary<string, string> Options) Parse(string[] args)
    {
        if (args.Length < 2 || args.Length > 8 || args[0] != "--local"
            || args[1] is not ("config" or "readiness" or "preview" or "demo" or "sample" or "submit" or "help"))
            throw new ArgumentException("Invalid local mode.");
        var action = args[1];
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 2; i < args.Length; i++)
        {
            var key = args[i];
            if (action == "sample" && key == "--diagnostics")
            {
                if (!options.TryAdd(key, "true")) throw new ArgumentException("Duplicate option.");
                continue;
            }
            if ((action is not ("config" or "readiness" or "preview" or "demo" or "submit"))
                || (key != "--workspace" && !((action is "preview" or "demo" or "submit") && key == "--objective")
                    && !(action == "demo" && key == "--scenario"))
                || ++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)
                || !options.TryAdd(key, args[i]))
                throw new ArgumentException("Invalid local option.");
        }
        if ((action is "preview" or "demo") && (!options.ContainsKey("--workspace") || !options.ContainsKey("--objective")))
            throw new ArgumentException("Explicit workspace and objective required.");
        if (action == "demo" && options.GetValueOrDefault("--scenario", "completed") is not ("completed" or "failed" or "unknown" or "waiting"))
            throw new ArgumentException("Invalid offline scenario.");
        return (action, options);
    }
}
