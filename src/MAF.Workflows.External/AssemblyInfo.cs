using System.Runtime.CompilerServices;

// Historical fixtures explicitly inject test-owned access implementations; no production fallback.
[assembly: InternalsVisibleTo("MAF.Tests")]
[assembly: InternalsVisibleTo("MAF.ApprovalAcceptance")]

// Deliberate internal application composition; no public ingress or authorization grant.
[assembly: InternalsVisibleTo("MAF.Host")]
