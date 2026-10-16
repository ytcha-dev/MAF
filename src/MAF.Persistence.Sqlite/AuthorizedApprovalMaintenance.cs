namespace MAF.Persistence.Sqlite;

// Trusted deployment/broker integration must authenticate the maintenance operator and authorize
// this exact database resource. This is NOT a caller-supplied --yes/--actor boolean.
public interface ITrustedApprovalMaintenanceAuthorization
{
    bool CanMigrate(string canonicalDatabasePath);
}

// No production authorizer/CLI is installed. Existing ApprovalSchema is the trusted storage
// primitive; this entry supplies an explicit fail-closed authorization boundary for future tools.
public static class AuthorizedApprovalMaintenance
{
    public static Task InitializeOrMigrateAsync(string databasePath, OfflineMaintenance prerequisites,
        ITrustedApprovalMaintenanceAuthorization? authorization = null, CancellationToken token = default)
    {
        var canonical = ApprovalDatabase.PathFor(databasePath);
        bool allowed;
        try { allowed = authorization?.CanMigrate(canonical) == true; }
        catch (Exception) { throw new InvalidOperationException("MaintenanceAuthorizationUnavailable"); }
        if (!allowed) throw new InvalidOperationException("MaintenanceNotAuthorized");
        return ApprovalSchema.InitializeOrMigrateAsync(canonical, prerequisites, token);
    }
}
