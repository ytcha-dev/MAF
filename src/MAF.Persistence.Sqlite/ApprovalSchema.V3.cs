using System.Text.Json;
using MAF.Core;
using MAF.Core.Policy;
using Microsoft.Data.Sqlite;

namespace MAF.Persistence.Sqlite;

public static partial class ApprovalSchema
{
    public static Task MigrateV2ToV3Async(string databasePath, OfflineMaintenance maintenance,
        ITrustedApprovalMaintenanceAuthorization? authorization = null, CancellationToken token = default)
        => MigrateV3Async(databasePath, maintenance, authorization, null, token);

    internal static async Task MigrateV3Async(string databasePath, OfflineMaintenance maintenance,
        ITrustedApprovalMaintenanceAuthorization? authorization, Action<string>? checkpoint, CancellationToken token = default)
    {
        var path = ApprovalDatabase.PathFor(databasePath);
        bool allowed;
        try { allowed = authorization?.CanMigrate(path) == true; }
        catch { throw new InvalidOperationException("MaintenanceAuthorizationUnavailable"); }
        Need(allowed, "MaintenanceNotAuthorized");
        Need(maintenance is { ExplicitMaintenanceMode: true, AllHostsStopped: true, BackupAndRollbackProcedureConfirmed: true }, "OfflineMaintenanceRequired");
        using var gate = new FileStream(Path.Combine(path + ".locks", ".schema.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var locks = new List<FileStream>();
        string? originalBytes = null;
        try
        {
            foreach (var file in Directory.GetFiles(path + ".locks", "*.lock"))
                if (Path.GetFileName(file) != ".schema.lock") locks.Add(new(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            await using var c = new SqliteConnection(ApprovalDatabase.ConnectionString(path, SqliteOpenMode.ReadWrite));
            await c.OpenAsync(token);
            await ApprovalDatabase.Execute(c, null, "PRAGMA synchronous=FULL; PRAGMA locking_mode=EXCLUSIVE; BEGIN EXCLUSIVE", token);
            bool committed = false;
            try
            {
                Need(Convert.ToInt64(await ApprovalDatabase.Scalar(c, null, "PRAGMA user_version", token)) == 2, "MigrationSchemaV2Required");
                await ValidateManifest(c, null, false, token);
                await ValidateOldData(c, token);
                var before = await OldBytes(c, null, token);
                originalBytes = before;
                checkpoint?.Invoke("V3Validated");
                var alter = V3Delta.IndexOf("ALTER TABLE", StringComparison.Ordinal);
                var index = V3Delta.IndexOf("CREATE UNIQUE INDEX", alter, StringComparison.Ordinal);
                await ApprovalDatabase.Execute(c, null, V3Delta[..alter], token); checkpoint?.Invoke("V3TableCreated");
                await ApprovalDatabase.Execute(c, null, V3Delta[alter..index], token); checkpoint?.Invoke("V3AuditAltered");
                await ApprovalDatabase.Execute(c, null, V3Delta[index..], token);
                Need(before == await OldBytes(c, null, token), "V2ValuesChanged");
                await ValidateManifest(c, null, true, token);
                await Integrity(c, null, token);
                await ApprovalDatabase.Execute(c, null, "PRAGMA user_version=3", token); checkpoint?.Invoke("V3VersionWritten");
                await ApprovalDatabase.Execute(c, null, "COMMIT", token); committed = true;
                checkpoint?.Invoke("V3Committed");
            }
            catch
            {
                if (!committed) await ApprovalDatabase.Execute(c, null, "ROLLBACK", CancellationToken.None);
                throw;
            }
        }
        finally { foreach (var held in locks) held.Dispose(); }
        // A lost post-commit response never triggers downgrade. Keep the exclusive schema gate
        // through this fresh-connection validation; the former connection has closed.
        await using var verify = new SqliteConnection(ApprovalDatabase.ConnectionString(path, SqliteOpenMode.ReadWrite));
        await verify.OpenAsync(token);
        Need(Convert.ToInt64(await ApprovalDatabase.Scalar(verify, null, "PRAGMA user_version", token)) == 3, "MigrationSchemaV3Required");
        await ValidateManifest(verify, null, true, token); await Integrity(verify, null, token);
        Need(originalBytes == await OldBytes(verify, null, token), "V2ValuesChanged");
    }

    internal static Task ValidateV3Async(SqliteConnection c, SqliteTransaction tx, CancellationToken token)
        => ValidateManifest(c, tx, true, token);
    static void Need(bool test, string code) { if (!test) throw new InvalidOperationException(code); }
    static readonly string[] OldTables = ["executions", "policy_authority", "task_controls", "authorization_operations", "approval_requests", "control_audit", "sqlite_sequence"];
    static async Task<string> Manifest(SqliteConnection c, SqliteTransaction? tx, CancellationToken token)
    {
        using var cmd = ApprovalDatabase.Command(c, tx, "SELECT type,name,sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' ORDER BY type,name");
        using var r = await cmd.ExecuteReaderAsync(token); var parts = new List<string>();
        while (await r.ReadAsync(token)) parts.Add(r.GetString(0) + ":" + r.GetString(1) + ":" + NormalizeSql(r.GetString(2)));
        return string.Join("\n", parts);
    }
    static string NormalizeSql(string sql)
    {
        var result = new System.Text.StringBuilder(); bool quoted = false;
        foreach (var ch in sql.Trim().TrimEnd(';'))
        {
            if (ch == '\'') quoted = !quoted;
            if (quoted || !char.IsWhiteSpace(ch)) result.Append(ch);
        }
        return result.ToString();
    }
    static async Task ValidateManifest(SqliteConnection c, SqliteTransaction? tx, bool v3, CancellationToken token)
    {
        await using var expected = new SqliteConnection("Data Source=:memory:"); await expected.OpenAsync(token);
        await ApprovalDatabase.Execute(expected, null, V1Definition + ";" + V2Definition + (v3 ? V3Delta : ""), token);
        Need(await Manifest(c, tx, token) == await Manifest(expected, null, token), "IncompatibleApprovalSchema");
    }
    internal static async Task<string> OldBytes(SqliteConnection c, SqliteTransaction? tx, CancellationToken token)
    {
        var all = new List<string>();
        foreach (var table in OldTables)
        {
            var columns = new List<string>();
            using (var cmd = ApprovalDatabase.Command(c, tx, "PRAGMA table_info(" + table + ")"))
            using (var reader = await cmd.ExecuteReaderAsync(token))
                while (await reader.ReadAsync(token)) if (reader.GetString(1) != "cancel_request_id") columns.Add(reader.GetString(1));
            var expressions = columns.Select(col => "typeof(" + col + ")||':'||hex(CAST(" + col + " AS BLOB))");
            using var data = ApprovalDatabase.Command(c, tx, "SELECT " + string.Join(",", expressions) + " FROM " + table);
            using var rows = await data.ExecuteReaderAsync(token); var values = new List<string>();
            while (await rows.ReadAsync(token)) values.Add(JsonSerializer.Serialize(Enumerable.Range(0, rows.FieldCount).Select(i => rows.GetString(i))));
            values.Sort(StringComparer.Ordinal); all.Add(table + JsonSerializer.Serialize(values));
        }
        return string.Join("\n", all);
    }
    static async Task Integrity(SqliteConnection c, SqliteTransaction? tx, CancellationToken token)
    {
        Need(Equals(await ApprovalDatabase.Scalar(c, tx, "PRAGMA integrity_check", token), "ok"), "IntegrityCheckFailed");
        using var cmd = ApprovalDatabase.Command(c, tx, "PRAGMA foreign_key_check"); using var r = await cmd.ExecuteReaderAsync(token);
        Need(!await r.ReadAsync(token), "ForeignKeyCheckFailed");
    }
    static async Task ValidateOldData(SqliteConnection c, CancellationToken token)
    {
        await Integrity(c, null, token);
        var executions = new Dictionary<string, DurableExecution>(StringComparer.Ordinal);
        Need(Convert.ToInt64(await ApprovalDatabase.Scalar(c, null, "SELECT count(*) FROM authorization_operations WHERE kind=3", token)) == 0, "PreexistingCancellationOperation");
        using (var cmd = ApprovalDatabase.Command(c, null, "SELECT task_id,version,terminal,snapshot FROM executions"))
        using (var r = await cmd.ExecuteReaderAsync(token))
            while (await r.ReadAsync(token))
            {
                var e = JsonSerializer.Deserialize<DurableExecution>(r.GetString(3));
                Need(e is not null && e.TaskId == r.GetString(0) && e.Version == r.GetInt64(1) && e.IsTerminal == r.GetBoolean(2)
                    && e.IsTerminal == e.CompletedAt.HasValue && e.Version >= 0 && Enum.IsDefined(e.State) && Enum.IsDefined(e.SubmissionPhase), "InvalidV2Execution");
                executions.Add(e!.TaskId, e);
            }
        using (var cmd = ApprovalDatabase.Command(c, null, "SELECT a.approval_id,a.status,a.version,a.decision_id,a.snapshot,o.operation_id,o.task_id,o.execution_id,o.kind,o.generation FROM approval_requests a JOIN authorization_operations o ON a.operation_id=o.operation_id"))
        using (var r = await cmd.ExecuteReaderAsync(token))
            while (await r.ReadAsync(token))
            {
                var a = JsonSerializer.Deserialize<ApprovalRecord>(r.GetString(4));
                Need(a is not null && a.ApprovalId == r.GetString(0) && (int)a.Status == r.GetInt32(1) && a.Version == r.GetInt64(2)
                    && a.DecisionId == (r.IsDBNull(3) ? null : r.GetString(3)) && Enum.IsDefined(a.Status)
                    && a.Operation.OperationId == r.GetString(5) && a.Operation.TaskId == r.GetString(6) && a.Operation.ExecutionId == r.GetString(7)
                    && (int)a.Operation.Operation == r.GetInt32(8) && a.Operation.IntentGeneration == r.GetInt64(9), "InvalidV2Approval");
                Need(a!.Version >= 0 && a.CreatedAt < a.ExpiresAt && a.BindingDigest.Length == 64
                    && a.BindingDigest.All(char.IsAsciiHexDigit)
                    && (a.Status is not (ApprovalStatus.Approved or ApprovalStatus.Consumed) || a.DecidedBy is not null && a.DecisionId is not null && a.Choice == ApprovalChoice.Approve), "InvalidV2Approval");
                _ = OperationBinding.Digest(a.Operation, new(a.Operation.ConnectorId, "migration-validation", a.Operation.Capability,
                    EnforcementBoundary.TaskAdmission, [a.Operation.Operation], true, true));
            }
        Need(Convert.ToInt64(await ApprovalDatabase.Scalar(c, null, "SELECT count(*) FROM task_controls WHERE (cancel_id IS NULL AND (actor IS NOT NULL OR requested_at IS NOT NULL)) OR (cancel_id IS NOT NULL AND (actor IS NULL OR requested_at IS NULL OR version=0))", token)) == 0, "InvalidV2Control");
        Need(Convert.ToInt64(await ApprovalDatabase.Scalar(c, null, "SELECT count(*) FROM authorization_operations o JOIN task_controls t ON o.task_id=t.task_id WHERE o.execution_id<>t.execution_id OR o.kind NOT IN (1,2)", token)) == 0, "InvalidV2Operation");
        Need(Convert.ToInt64(await ApprovalDatabase.Scalar(c, null, "SELECT count(*) FROM control_audit", token)) <= 10000, "AuditCapacityReached");
        using var controls = ApprovalDatabase.Command(c, null, "SELECT task_id,execution_id,cancel_id,actor,requested_at FROM task_controls");
        using var cr = await controls.ExecuteReaderAsync(token);
        while (await cr.ReadAsync(token))
        {
            static bool MetadataId(string id) => id.Length is > 0 and <= 160 && id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.' or ':');
            Need(MetadataId(cr.GetString(0)) && MetadataId(cr.GetString(1)), "InvalidV2Control");
            Need(!executions.TryGetValue(cr.GetString(0), out var e) || e.Handle.ExecutionId == cr.GetString(1), "InvalidV2Control");
            if (!cr.IsDBNull(2)) Need(MetadataId(cr.GetString(2)) && MetadataId(cr.GetString(3))
                && DateTimeOffset.TryParseExact(cr.GetString(4), "O", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out _), "InvalidV2Control");
        }
    }
}
