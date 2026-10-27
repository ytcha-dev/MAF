using System.Text.RegularExpressions;
using MAF.Core.Tools;

namespace MAF.Workflows.External.OpenCode;

public sealed record OpenCodeOptions(Uri BaseUri, string ApprovedProjectRoot, string Agent = "maf_readonly",
    bool AllowLoopbackHttp = false, string? Username = null, string? Password = null,
    int RequestTimeoutSeconds = 15, int ResponseLimitBytes = 1024 * 1024, int SummaryLimit = 32000,
    bool RequireReadApproval = false, string? ExpectedVersion = null)
{
    public void Validate()
    {
        // Null retains historical schema-based fixtures; new pinned paths must explicitly select
        // a reviewed V1 contract. Never infer a protocol from a successful POST or negotiate.
        if (ExpectedVersion is not (null or "1.18.35" or "1.19.2"))
            throw new ArgumentException("UnsupportedOpenCodeVersionPin");
        if (!BaseUri.IsAbsoluteUri || !BaseUri.IsLoopback || !string.IsNullOrEmpty(BaseUri.UserInfo)
            || BaseUri.AbsolutePath != "/" || !string.IsNullOrEmpty(BaseUri.Query) || !string.IsNullOrEmpty(BaseUri.Fragment)
            || BaseUri.Scheme != "https" && !(BaseUri.Scheme == "http" && AllowLoopbackHttp))
        {
            throw new ArgumentException("Phase 2A requires an explicitly approved loopback origin; HTTP requires explicit opt-in.");
        }
        if (!Regex.IsMatch(Agent, "^[a-zA-Z0-9_-]{1,80}$") || RequestTimeoutSeconds is < 1 or > 60
            || ResponseLimitBytes is < 1000 or > 3000000 || SummaryLimit is < 100 or > 64000)
        {
            throw new ArgumentException("Invalid bounded OpenCode configuration.");
        }
        _ = new ProjectTools(ApprovedProjectRoot);
    }
}
