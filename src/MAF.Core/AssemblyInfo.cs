using System.Runtime.CompilerServices;

// Issuer implementations stay in trusted persistence. The protected adapter bridge uses
// receipt lifecycle internals; visibility is not malicious same-principal isolation.
[assembly: InternalsVisibleTo("MAF.Persistence.Sqlite")]
[assembly: InternalsVisibleTo("MAF.Workflows.External")]
