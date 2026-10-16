using System.Runtime.CompilerServices;

// Trusted persistence is the sole continuation issuer; test/Host assemblies cannot mint one.
[assembly: InternalsVisibleTo("MAF.Persistence.Sqlite")]
