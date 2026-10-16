using MAF.Core;

namespace MAF.Host;

internal static class ExternalConsole
{
    public static async Task<int> RunAsync(string[] args, string project, Func<string, string> option)
    {
        _ = ExternalConsoleOutput.Parse(args);
        if (new[] { "--external-task", "--recover", "--recover-pending" }.Count(mode => args.Contains(mode)) != 1)
            throw new ArgumentException("Choose exactly one external execution or recovery mode.");
        if (args.Contains("--ask") || args.Contains("--review"))
        {
            throw new ArgumentException("Choose one CLI mode.");
        }
        // No production trust source is installed. Do not build a legacy dispatch composition.
        await Task.FromException(new ExternalHarnessException("ProductionTrustUnavailable"));
        return 1;
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
