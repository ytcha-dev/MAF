using System.Text.Json;
using MAF.Core;
using Microsoft.Data.Sqlite;

namespace MAF.Persistence.Sqlite;

// Explicit profiles open exactly their schema for read/update; direct creation requires admission.
public enum ExecutionStoreProfile { LegacyV1, ExplicitV2, ExplicitV3, ExplicitV4 }

// One local host; OS file ownership survives neither disposal nor process death.
// The ownership file is not a SQLite transaction and contains no task data.
public sealed class SqliteExecutionStore : IExecutionStore
{
    private readonly string databasePath;
    private readonly ExecutionStoreProfile profile;
    private readonly string connectionString;
    private sealed class Ownership(string taskId, string database, FileStream stream, ExecutionStoreProfile profile) : IExecutionOwnership
    {
        public string TaskId { get; } = taskId;
        public string Database { get; } = database;
        public bool Disposed { get; private set; }
        public ExecutionStoreProfile Profile { get; } = profile;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public async ValueTask DisposeAsync() { await Gate.WaitAsync(); try { Disposed = true; await stream.DisposeAsync(); } finally { Gate.Release(); } }
    }

    public SqliteExecutionStore(string databasePath) : this(databasePath, ExecutionStoreProfile.LegacyV1) { }

    public SqliteExecutionStore(string databasePath, ExecutionStoreProfile profile)
    {
        if (!Path.IsPathFullyQualified(databasePath) || databasePath.StartsWith("\\\\", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(Path.GetFileName(databasePath)))
            throw new ArgumentException("An absolute local database file path is required.");
        if (!Enum.IsDefined(profile)) throw new ArgumentOutOfRangeException(nameof(profile));
        this.profile = profile;
        this.databasePath = profile != ExecutionStoreProfile.LegacyV1 ? ApprovalDatabase.PathFor(databasePath) : Path.GetFullPath(databasePath);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = this.databasePath, Pooling = false, DefaultTimeout = 5, Mode = profile != ExecutionStoreProfile.LegacyV1 ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var schemaLease = ApprovalDatabase.SharedGate(databasePath);
        var connection = new SqliteConnection(connectionString);
        connection.StateChange += (_, change) => { if (change.CurrentState == System.Data.ConnectionState.Closed) schemaLease.Dispose(); };
        try
        {
            await connection.OpenAsync(token);
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA synchronous=FULL;";
            await pragma.ExecuteNonQueryAsync(token);
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt32(await command.ExecuteScalarAsync(token));
            if (profile == ExecutionStoreProfile.LegacyV1 ? version is not (0 or 1) : version != (profile switch { ExecutionStoreProfile.ExplicitV4 => 4, ExecutionStoreProfile.ExplicitV3 => 3, _ => 2 })) throw new InvalidOperationException("Unsupported execution database schema.");
            if (version == 0)
            {
                command.CommandText = """
                    CREATE TABLE executions (
                        task_id TEXT PRIMARY KEY NOT NULL,
                        version INTEGER NOT NULL CHECK(version >= 0),
                        terminal INTEGER NOT NULL CHECK(terminal IN (0,1)),
                        snapshot TEXT NOT NULL
                    );
                    PRAGMA user_version=1;
                    """;
                await command.ExecuteNonQueryAsync(token);
            }
            if (profile == ExecutionStoreProfile.ExplicitV3) await ApprovalSchema.ValidateV3Async(connection, transaction, token);
            if (profile == ExecutionStoreProfile.ExplicitV4) await ApprovalSchema.ValidateV4Async(connection, transaction, token);
            transaction.Commit();
            return connection;
        }
        catch { await connection.DisposeAsync(); schemaLease.Dispose(); throw; }
    }

    public ValueTask<IExecutionOwnership?> TryAcquireAsync(string taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        using var schemaLease = ApprovalDatabase.SharedGate(databasePath);
        var directory = databasePath + ".locks";
        Directory.CreateDirectory(directory);
        try
        {
            IExecutionOwnership owner = new Ownership(taskId, databasePath,
                new FileStream(Path.Combine(directory, DurableExecution.Hash(taskId) + ".lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None), profile);
            return ValueTask.FromResult<IExecutionOwnership?>(owner);
        }
        catch (IOException) { return ValueTask.FromResult<IExecutionOwnership?>(null); }
    }

    private void CheckOwnership(string taskId, IExecutionOwnership ownership)
    {
        if (ownership is not Ownership own || own.Disposed || own.TaskId != taskId || own.Database != databasePath || own.Profile != profile)
            throw new InvalidOperationException("Execution ownership is required.");
    }

    internal static async ValueTask<IAsyncDisposable> UseAdmissionOwnershipAsync(string database, string task,
        IExecutionOwnership ownership, CancellationToken token, ExecutionStoreProfile requiredProfile = ExecutionStoreProfile.ExplicitV2)
    {
        if (ownership is not Ownership own || own.TaskId != task || own.Database != database
            || own.Profile != requiredProfile || requiredProfile == ExecutionStoreProfile.LegacyV1)
            throw new InvalidOperationException("ExecutionOwnershipRequired");
        await own.Gate.WaitAsync(token);
        if (own.Disposed) { own.Gate.Release(); throw new InvalidOperationException("ExecutionOwnershipRequired"); }
        return new OwnershipUse(own.Gate);
    }
    private sealed class OwnershipUse(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; }
    }

    private static DurableExecution Read(SqliteDataReader reader)
    {
        var record = JsonSerializer.Deserialize<DurableExecution>(reader.GetString(3))
            ?? throw new InvalidOperationException("Invalid durable record.");
        if (record.TaskId != reader.GetString(0) || record.Version != reader.GetInt64(1)
            || record.IsTerminal != reader.GetBoolean(2) || !Enum.IsDefined(record.State) || !Enum.IsDefined(record.SubmissionPhase)
            || record.Version < 0 || record.IsTerminal != record.CompletedAt.HasValue)
            throw new InvalidOperationException("Inconsistent durable record.");
        return record;
    }

    public async Task<DurableExecution?> GetAsync(string taskId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = profile == ExecutionStoreProfile.ExplicitV4 ? connection.BeginTransaction(deferred: true) : null;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT task_id, version, terminal, snapshot FROM executions WHERE task_id=$id;";
        command.Parameters.AddWithValue("$id", taskId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var record = await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
        await reader.DisposeAsync();
        if (record is not null && profile == ExecutionStoreProfile.ExplicitV4) await VerifyV4Record(connection, transaction, record, cancellationToken);
        transaction?.Commit();
        return record;
    }

    public async Task<IReadOnlyList<DurableExecution>> ListRecoverableAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = profile == ExecutionStoreProfile.ExplicitV4 ? connection.BeginTransaction(deferred: true) : null;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT task_id, version, terminal, snapshot FROM executions WHERE terminal=0 ORDER BY task_id LIMIT 101;";
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var records = new List<DurableExecution>();
        while (await reader.ReadAsync(cancellationToken)) records.Add(Read(reader));
        await reader.DisposeAsync();
        if (profile == ExecutionStoreProfile.ExplicitV4)
            foreach (var e in records) await VerifyV4Record(connection, transaction, e, cancellationToken);
        transaction?.Commit();
        if (records.Count > 100) throw new InvalidOperationException("Recovery batch exceeds 100 records; recover individual TaskIds.");
        return records;
    }

    public async Task CreateAsync(DurableExecution execution, IExecutionOwnership ownership, CancellationToken cancellationToken = default)
    {
        if (profile != ExecutionStoreProfile.LegacyV1) throw new InvalidOperationException("AtomicAdmissionRequired");
        CheckOwnership(execution.TaskId, ownership);
        if (execution.State != DurableExecutionState.Created || execution.Version != 0 || execution.CompletedAt is not null
            || execution.SubmissionPhase != SubmissionPhase.None || execution.Handle.ExternalSessionId is not null
            || string.IsNullOrEmpty(execution.Handle.ExternalRequestId) || execution.RequestFingerprint.Length != 64
            || execution.ProviderScope.Length != 64 || execution.ErrorCode is not null)
            throw new InvalidOperationException("Invalid initial durable record.");
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO executions(task_id,version,terminal,snapshot) VALUES($id,0,0,$snapshot);";
        command.Parameters.AddWithValue("$id", execution.TaskId);
        command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(execution));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateAsync(DurableExecution expected, DurableExecution next, IExecutionOwnership ownership,
        CancellationToken cancellationToken = default)
    {
        CheckOwnership(expected.TaskId, ownership);
        await using var use = profile == ExecutionStoreProfile.ExplicitV4
            ? await UseAdmissionOwnershipAsync(databasePath, expected.TaskId, ownership, cancellationToken, ExecutionStoreProfile.ExplicitV4) : null;
        DurableExecutionTransitions.Validate(expected, next);
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = profile == ExecutionStoreProfile.ExplicitV4 ? connection.BeginTransaction(deferred: false) : null;
        if (profile == ExecutionStoreProfile.ExplicitV4)
        {
            await VerifyV4Record(connection, transaction, expected, cancellationToken);
            if (expected.Handle.ExternalSessionId is null && next.Handle.ExternalSessionId is not null
                || expected.SubmissionPhase != next.SubmissionPhase && next.SubmissionPhase is SubmissionPhase.SessionCreationIntended or SubmissionPhase.PromptIntended)
                throw new InvalidOperationException("SessionReceiptRequired");
            var required = Convert.ToInt64(await ApprovalDatabase.Scalar(connection, transaction,
                "SELECT session_receipt_required FROM executions WHERE task_id=$t", cancellationToken, ("$t", expected.TaskId)));
            if (required == 0 && next.SubmissionPhase != expected.SubmissionPhase) throw new InvalidOperationException("LegacyExecutionRecoveryOnly");
        }
        // Compare the complete prior snapshot as well as version: callers cannot invent a prior state.
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE executions SET version=$next, terminal=$terminal, snapshot=$snapshot
            WHERE task_id=$id AND version=$version AND snapshot=$expected;
            """;
        command.Parameters.AddWithValue("$id", expected.TaskId);
        command.Parameters.AddWithValue("$version", expected.Version);
        command.Parameters.AddWithValue("$next", next.Version);
        command.Parameters.AddWithValue("$terminal", next.IsTerminal ? 1 : 0);
        command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(next));
        command.Parameters.AddWithValue("$expected", JsonSerializer.Serialize(expected));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("Durable execution version conflict.");
        transaction?.Commit();
    }

    static async Task VerifyV4Record(SqliteConnection c, SqliteTransaction? tx, DurableExecution e, CancellationToken token)
    {
        var required = Convert.ToInt64(await ApprovalDatabase.Scalar(c, tx, "SELECT session_receipt_required FROM executions WHERE task_id=$t", token, ("$t", e.TaskId)));
        if (required == 0) return;
        var receipt = await SqliteApprovalStore.ReadSessionReceipt(c, tx, e.TaskId, token);
        if (e.SubmissionPhase == SubmissionPhase.None ? receipt is not null : receipt is null)
            throw new InvalidOperationException("SessionReceiptRequired");
        if (e.SubmissionPhase >= SubmissionPhase.SessionKnown && receipt?.Response is null)
            throw new InvalidOperationException("SessionReceiptRequired");
    }
}
