using System.Runtime.CompilerServices;

// Historical fixtures explicitly inject test-owned access implementations; no production fallback.
[assembly: InternalsVisibleTo("MAF.Tests")]
[assembly: InternalsVisibleTo("MAF.ApprovalAcceptance")]
