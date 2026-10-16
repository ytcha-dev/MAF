using System.Text.Json;
using MAF.Core;
using Microsoft.Data.Sqlite;

namespace MAF.Persistence.Sqlite;

public static partial class ApprovalSchema
{
    public static Task MigrateV3ToV4Async(string databasePath, OfflineMaintenance maintenance,
        ITrustedApprovalMaintenanceAuthorization? authorization = null, CancellationToken token = default)
        => MigrateV4Async(databasePath, maintenance, authorization, null, token);

    internal static async Task MigrateV4Async(string databasePath, OfflineMaintenance maintenance,
        ITrustedApprovalMaintenanceAuthorization? authorization, Action<string>? checkpoint, CancellationToken token = default)
    {
        var path = ApprovalDatabase.PathFor(databasePath);
        bool allowed;
        try { allowed = authorization?.CanMigrate(path) == true; }
        catch { throw new InvalidOperationException("MaintenanceAuthorizationUnavailable"); }
        Need(allowed, "MaintenanceNotAuthorized");
        Need(maintenance is { ExplicitMaintenanceMode: true, AllHostsStopped: true, BackupAndRollbackProcedureConfirmed: true }, "OfflineMaintenanceRequired");
        using var gate = new FileStream(Path.Combine(path + ".locks", ".schema.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var locks = new List<FileStream>(); string before;
        try
        {
            foreach (var file in Directory.GetFiles(path + ".locks", "*.lock").Order(StringComparer.Ordinal))
                if (Path.GetFileName(file) != ".schema.lock")
                {
                    Need((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0, "LinkedDatabasePath");
                    locks.Add(new(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
                }
            var deliveryDirectory = path + ".delivery-locks";
            if (Directory.Exists(deliveryDirectory))
            {
                Need((File.GetAttributes(deliveryDirectory) & FileAttributes.ReparsePoint) == 0, "LinkedDatabasePath");
                foreach (var file in Directory.GetFiles(deliveryDirectory, "*.lock").Order(StringComparer.Ordinal))
                {
                    Need((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0, "LinkedDatabasePath");
                    locks.Add(new(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
                }
            }
            await using var c = new SqliteConnection(ApprovalDatabase.ConnectionString(path, SqliteOpenMode.ReadWrite));
            await c.OpenAsync(token);
            await ApprovalDatabase.Execute(c, null, "PRAGMA synchronous=FULL; PRAGMA locking_mode=EXCLUSIVE; BEGIN EXCLUSIVE", token);
            var committed = false;
            try
            {
                Need(Convert.ToInt64(await ApprovalDatabase.Scalar(c, null, "PRAGMA user_version", token)) == 3, "MigrationSchemaV3Required");
                await ValidateManifest(c, null, true, token); await ValidateV3DataForV4(c, token);
                before = await V3Bytes(c, null, token); checkpoint?.Invoke("V4Validated");
                var receipt = V4Delta.IndexOf("CREATE TABLE execution_session_receipts", StringComparison.Ordinal);
                var delivery = V4Delta.IndexOf("CREATE TABLE cancellation_deliveries", StringComparison.Ordinal);
                await ApprovalDatabase.Execute(c, null, V4Delta[..receipt], token); checkpoint?.Invoke("V4ProfileAdded");
                await ApprovalDatabase.Execute(c, null, V4Delta[receipt..delivery], token); checkpoint?.Invoke("V4ReceiptCreated");
                await ApprovalDatabase.Execute(c, null, V4Delta[delivery..], token); checkpoint?.Invoke("V4DeliveryCreated");
                Need(before == await V3Bytes(c, null, token), "V3ValuesChanged");
                await ValidateV4Manifest(c, null, token); await Integrity(c, null, token);
                await ApprovalDatabase.Execute(c, null, "PRAGMA user_version=4", token); checkpoint?.Invoke("V4VersionWritten");
                await ApprovalDatabase.Execute(c, null, "COMMIT", token); committed = true; checkpoint?.Invoke("V4Committed");
            }
            catch { if (!committed) await ApprovalDatabase.Execute(c, null, "ROLLBACK", CancellationToken.None); throw; }
        }
        finally { foreach (var held in locks) held.Dispose(); }
        await using var verify = new SqliteConnection(ApprovalDatabase.ConnectionString(path, SqliteOpenMode.ReadWrite));
        await verify.OpenAsync(token);
        Need(Convert.ToInt64(await ApprovalDatabase.Scalar(verify, null, "PRAGMA user_version", token)) == 4, "MigrationSchemaV4Required");
        await ValidateV4Manifest(verify, null, token); await Integrity(verify, null, token);
        Need(before == await V3Bytes(verify, null, token), "V3ValuesChanged");
    }

    internal static Task ValidateV4Async(SqliteConnection c, SqliteTransaction tx, CancellationToken token)
        => ValidateV4Manifest(c, tx, token);
    static async Task ValidateV4Manifest(SqliteConnection c, SqliteTransaction? tx, CancellationToken token)
    {
        await using var expected = new SqliteConnection("Data Source=:memory:"); await expected.OpenAsync(token);
        await ApprovalDatabase.Execute(expected, null, V1Definition + ";" + V2Definition + V3Delta + V4Delta, token);
        Need(await Manifest(c, tx, token) == await Manifest(expected, null, token), "IncompatibleApprovalSchema");
    }

    internal static async Task<string> V3Bytes(SqliteConnection c, SqliteTransaction? tx, CancellationToken token)
    {
        var all = new List<string>();
        foreach (var table in OldTables.Append("cancellation_requests"))
        {
            var columns = new List<string>();
            using (var cmd = ApprovalDatabase.Command(c, tx, "PRAGMA table_info(" + table + ")"))
            using (var reader = await cmd.ExecuteReaderAsync(token))
                while (await reader.ReadAsync(token)) if (reader.GetString(1) != "session_receipt_required") columns.Add(reader.GetString(1));
            using var data = ApprovalDatabase.Command(c, tx, "SELECT " + string.Join(",", columns.Select(col => "typeof(" + col + ")||':'||hex(CAST(" + col + " AS BLOB))")) + " FROM " + table);
            using var rows = await data.ExecuteReaderAsync(token); var values = new List<string>();
            while (await rows.ReadAsync(token)) values.Add(JsonSerializer.Serialize(Enumerable.Range(0, rows.FieldCount).Select(i => rows.GetString(i))));
            values.Sort(StringComparer.Ordinal); all.Add(table + JsonSerializer.Serialize(values));
        }
        return string.Join("\n", all);
    }

    static async Task ValidateV3DataForV4(SqliteConnection c, CancellationToken token)
    {
        await Integrity(c, null, token);
        using (var cmd = ApprovalDatabase.Command(c, null, "SELECT task_id,version,terminal,snapshot FROM executions"))
        using (var r = await cmd.ExecuteReaderAsync(token)) while (await r.ReadAsync(token))
        {
            var e = JsonSerializer.Deserialize<DurableExecution>(r.GetString(3));
            Need(e is not null && e.TaskId == r.GetString(0) && e.Version == r.GetInt64(1) && e.IsTerminal == r.GetBoolean(2)
                && e.IsTerminal == e.CompletedAt.HasValue && e.Version >= 0 && Enum.IsDefined(e.State) && Enum.IsDefined(e.SubmissionPhase), "InvalidV3Execution");
            foreach (var id in new[] { e!.TaskId, e.WorkflowRunId, e.Handle.ExecutionId, e.Handle.Provider, e.Handle.ExternalRequestId })
                Need(id is { Length: > 0 and <= 160 } && id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.' or ':'), "InvalidV3ExecutionIdentity");
            Need(e.RequestFingerprint.Length == 64 && e.RequestFingerprint.All(char.IsAsciiHexDigit)
                && e.ProviderScope.Length == 64 && e.ProviderScope.All(char.IsAsciiHexDigit)
                && Path.IsPathFullyQualified(e.ProjectRoot) && e.CreatedAt <= e.UpdatedAt
                && (e.CompletedAt is null || e.CompletedAt >= e.CreatedAt && e.CompletedAt <= e.UpdatedAt)
                && (e.SubmissionPhase < SubmissionPhase.SessionKnown ? e.Handle.ExternalSessionId is null : !string.IsNullOrEmpty(e.Handle.ExternalSessionId))
                && (e.ErrorCode is null || e.ErrorCode.Length <= 80 && e.ErrorCode.All(char.IsAsciiLetterOrDigit)), "InvalidV3Execution");
            var validPhase = e.State switch
            {
                DurableExecutionState.Created => e.SubmissionPhase == SubmissionPhase.None,
                DurableExecutionState.SessionCreating => e.SubmissionPhase == SubmissionPhase.SessionCreationIntended,
                DurableExecutionState.ReadyToSubmit => e.SubmissionPhase == SubmissionPhase.SessionKnown,
                DurableExecutionState.SubmissionIntended or DurableExecutionState.SubmissionUncertain => e.SubmissionPhase == SubmissionPhase.PromptIntended,
                DurableExecutionState.Unknown or DurableExecutionState.Failed => true,
                _ => e.SubmissionPhase >= SubmissionPhase.PromptIntended
            };
            Need(validPhase, "InvalidV3ExecutionPhase");
        }
        await SqliteApprovalStore.ValidateV3ForMigration(c, token);
        Need(Convert.ToInt64(await ApprovalDatabase.Scalar(c, null, "SELECT count(*) FROM control_audit", token)) <= 10000, "AuditCapacityReached");
        Need(Convert.ToInt64(await ApprovalDatabase.Scalar(c, null, "SELECT count(*) FROM authorization_operations o JOIN task_controls t ON o.task_id=t.task_id WHERE o.kind NOT IN (1,2,3) OR o.execution_id<>t.execution_id", token)) == 0,
            "InvalidV3Operation");
        Need(Convert.ToInt64(await ApprovalDatabase.Scalar(c, null, "SELECT count(*) FROM task_controls WHERE (cancel_id IS NULL AND (actor IS NOT NULL OR requested_at IS NOT NULL)) OR (cancel_id IS NOT NULL AND (actor IS NULL OR requested_at IS NULL OR version=0))", token)) == 0, "InvalidV3Control");
    }
}
