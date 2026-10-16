using Microsoft.Data.Sqlite;

namespace MAF.Persistence.Sqlite;

internal enum ApprovalCheckpoint { MigrationValidated, TablesCreated, SchemaVersionWritten, PendingInserted, DecisionWritten, ApprovalConsumed, ExecutionInserted, CancellationWritten, BeforeCommit, AdmissionCommitted, DispatchIntentWritten, DispatchIntentCommitted, ConsumptionCommitted }

public sealed record OfflineMaintenance(bool ExplicitMaintenanceMode, bool AllHostsStopped,
    bool BackupAndRollbackProcedureConfirmed);

public static class ApprovalSchema
{
    const string V1Definition = "CREATE TABLE executions(task_id TEXT PRIMARY KEY NOT NULL, version INTEGER NOT NULL CHECK(version>=0), terminal INTEGER NOT NULL CHECK(terminal IN (0,1)), snapshot TEXT NOT NULL)";
    public static Task InitializeOrMigrateAsync(string databasePath, OfflineMaintenance maintenance, CancellationToken token = default)
        => MigrateAsync(databasePath, maintenance, null, token);

    internal static async Task MigrateAsync(string databasePath, OfflineMaintenance maintenance,
        Action<ApprovalCheckpoint>? fault, CancellationToken token = default)
    {
        if (maintenance is not { ExplicitMaintenanceMode: true, AllHostsStopped: true, BackupAndRollbackProcedureConfirmed: true })
            throw new InvalidOperationException("OfflineMaintenanceRequired");
        var path = ApprovalDatabase.PathFor(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Directory.CreateDirectory(path + ".locks");
        using var gate = new FileStream(System.IO.Path.Combine(path + ".locks", ".schema.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var held = new List<FileStream>();
        try
        {
            if (Directory.Exists(path + ".locks"))
                foreach (var file in Directory.GetFiles(path + ".locks", "*.lock"))
                    if (System.IO.Path.GetFileName(file) != ".schema.lock") held.Add(new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            await using var c = new SqliteConnection(ApprovalDatabase.ConnectionString(path, SqliteOpenMode.ReadWriteCreate));
            await c.OpenAsync(token);
            using (var setup = c.CreateCommand())
            { setup.CommandText = "PRAGMA synchronous=FULL; PRAGMA locking_mode=EXCLUSIVE;"; await setup.ExecuteNonQueryAsync(token); }
            // Explicit SQL guarantees EXCLUSIVE, rather than relying on BEGIN IMMEDIATE lock promotion.
            SqliteTransaction? tx = null;
            await ApprovalDatabase.Execute(c, tx, "BEGIN EXCLUSIVE", token);
            try
            {
                var version = Convert.ToInt32(await ApprovalDatabase.Scalar(c, tx, "PRAGMA user_version", token));
                if (version is not (0 or 1)) throw new InvalidOperationException("UnsupportedMigrationSchema");
                var tableCount = Convert.ToInt32(await ApprovalDatabase.Scalar(c, tx,
                    "SELECT count(*) FROM sqlite_master WHERE type IN ('table','view','trigger','index') AND name NOT LIKE 'sqlite_%'", token));
                if (version == 0)
                {
                    if (tableCount != 0) throw new InvalidOperationException("NonEmptyUnversionedDatabase");
                    await ApprovalDatabase.Execute(c, tx, V1Definition, token);
                }
                else
                {
                    await ValidateV1(c, tx, token);
                    if (tableCount != 1) throw new InvalidOperationException("UnexpectedV1SchemaObjects");
                }
                var before = await ExecutionBytes(c, tx, token);
                if (!Equals(await ApprovalDatabase.Scalar(c, tx, "PRAGMA integrity_check", token), "ok")) throw new InvalidOperationException("IntegrityCheckFailed");
                fault?.Invoke(ApprovalCheckpoint.MigrationValidated);
                await ApprovalDatabase.Execute(c, tx, """
                    CREATE TABLE policy_authority(id INTEGER PRIMARY KEY CHECK(id=1), authority TEXT NOT NULL, epoch INTEGER NOT NULL CHECK(epoch>0), version INTEGER NOT NULL CHECK(version>0), digest TEXT NOT NULL);
                    CREATE TABLE task_controls(task_id TEXT PRIMARY KEY NOT NULL, execution_id TEXT NOT NULL, version INTEGER NOT NULL CHECK(version>=0), cancel_id TEXT UNIQUE, actor TEXT, requested_at TEXT);
                    CREATE TABLE authorization_operations(operation_id TEXT PRIMARY KEY NOT NULL, task_id TEXT NOT NULL REFERENCES task_controls(task_id), execution_id TEXT NOT NULL, kind INTEGER NOT NULL, generation INTEGER NOT NULL CHECK(generation>0), UNIQUE(task_id,kind,generation));
                    CREATE UNIQUE INDEX one_initial_admission ON authorization_operations(task_id) WHERE kind=1;
                    CREATE TABLE approval_requests(approval_id TEXT PRIMARY KEY NOT NULL, operation_id TEXT UNIQUE NOT NULL REFERENCES authorization_operations(operation_id), status INTEGER NOT NULL CHECK(status BETWEEN 0 AND 5), version INTEGER NOT NULL CHECK(version>=0), decision_id TEXT UNIQUE, snapshot TEXT NOT NULL);
                    CREATE TABLE control_audit(sequence INTEGER PRIMARY KEY AUTOINCREMENT, code INTEGER NOT NULL CHECK(code BETWEEN 0 AND 7), actor TEXT NOT NULL, task_id TEXT NOT NULL, approval_id TEXT, version INTEGER NOT NULL, recorded_at TEXT NOT NULL, policy_epoch INTEGER NOT NULL CHECK(policy_epoch>0), policy_version INTEGER NOT NULL CHECK(policy_version>0));
                    """, token);
                fault?.Invoke(ApprovalCheckpoint.TablesCreated);
                if (!before.SequenceEqual(await ExecutionBytes(c, tx, token))) throw new InvalidOperationException("ExecutionSnapshotsChanged");
                await ApprovalDatabase.Execute(c, tx, "PRAGMA user_version=2", token);
                fault?.Invoke(ApprovalCheckpoint.SchemaVersionWritten);
                await ApprovalDatabase.Execute(c, tx, "COMMIT", token);
            }
            catch
            {
                await ApprovalDatabase.Execute(c, tx, "ROLLBACK", CancellationToken.None);
                throw;
            }
        }
        finally { foreach (var file in held) file.Dispose(); }
    }

    static async Task ValidateV1(SqliteConnection c, SqliteTransaction? tx, CancellationToken token)
    {
        static string Normalize(string sql) => new string(sql.Where(c => !char.IsWhiteSpace(c)).ToArray()).TrimEnd(';').ToUpperInvariant();
        var definition = await ApprovalDatabase.Scalar(c, tx, "SELECT sql FROM sqlite_master WHERE type='table' AND name='executions'", token) as string;
        if (definition is null || Normalize(definition) != Normalize(V1Definition)) throw new InvalidOperationException("IncompatibleV1Schema");
        using var command = c.CreateCommand(); command.Transaction = tx; command.CommandText = "PRAGMA table_info(executions)";
        using (var r = await command.ExecuteReaderAsync(token))
        {
            var columns = new List<(string Name, string Type, long NotNull, long Pk)>();
            while (await r.ReadAsync(token)) columns.Add((r.GetString(1), r.GetString(2).ToUpperInvariant(), r.GetInt64(3), r.GetInt64(5)));
            if (!columns.SequenceEqual(new[] { ("task_id", "TEXT", 1L, 1L), ("version", "INTEGER", 1L, 0L), ("terminal", "INTEGER", 1L, 0L), ("snapshot", "TEXT", 1L, 0L) }))
                throw new InvalidOperationException("IncompatibleV1Schema");
        }
        command.CommandText = "SELECT task_id, version, terminal, snapshot FROM executions";
        using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var record = System.Text.Json.JsonSerializer.Deserialize<MAF.Core.DurableExecution>(reader.GetString(3));
            if (record is null || record.TaskId != reader.GetString(0) || record.Version != reader.GetInt64(1)
                || record.IsTerminal != reader.GetBoolean(2) || record.IsTerminal != record.CompletedAt.HasValue
                || record.Version < 0 || !Enum.IsDefined(record.State) || !Enum.IsDefined(record.SubmissionPhase))
                throw new InvalidOperationException("InvalidV1Snapshot");
        }
    }

    static async Task<List<string>> ExecutionBytes(SqliteConnection c, SqliteTransaction? tx, CancellationToken token)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT hex(CAST(task_id AS BLOB))||':'||version||':'||terminal||':'||hex(CAST(snapshot AS BLOB)) FROM executions ORDER BY task_id";
        using var r = await cmd.ExecuteReaderAsync(token); var rows = new List<string>();
        while (await r.ReadAsync(token)) rows.Add(r.GetString(0)); return rows;
    }
}

internal static class ApprovalDatabase
{
    internal static string PathFor(string path)
    {
        if (!System.IO.Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || string.IsNullOrEmpty(System.IO.Path.GetFileName(path)))
            throw new ArgumentException("AbsoluteLocalDatabaseRequired");
        var full = System.IO.Path.GetFullPath(path);
        for (var d = new DirectoryInfo(System.IO.Path.GetDirectoryName(full)!); d is not null; d = d.Parent)
            if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("LinkedDatabasePath");
        foreach (var p in new[] { full, full + ".locks", System.IO.Path.Combine(full + ".locks", ".schema.lock") })
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("LinkedDatabasePath");
        return full;
    }
    internal static string ConnectionString(string path, SqliteOpenMode mode) => new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 5, Mode = mode, ForeignKeys = true }.ToString();
    internal static FileStream SharedGate(string path)
    {
        Directory.CreateDirectory(path + ".locks");
        return new(System.IO.Path.Combine(path + ".locks", ".schema.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
    }
    internal static async Task<int> Execute(SqliteConnection c, SqliteTransaction? tx, string sql, CancellationToken token, params (string, object?)[] args)
    {
        using var cmd = Command(c, tx, sql, args); return await cmd.ExecuteNonQueryAsync(token);
    }
    internal static async Task<object?> Scalar(SqliteConnection c, SqliteTransaction? tx, string sql, CancellationToken token, params (string, object?)[] args)
    { using var cmd = Command(c, tx, sql, args); return await cmd.ExecuteScalarAsync(token); }
    internal static SqliteCommand Command(SqliteConnection c, SqliteTransaction? tx, string sql, params (string, object?)[] args)
    {
        var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value); return cmd;
    }
}
