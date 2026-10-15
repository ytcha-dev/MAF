using System.Text.Json;
using MAF.Core;
using Microsoft.Data.Sqlite;

namespace MAF.Persistence.Sqlite;

// One local host; OS file ownership survives neither disposal nor process death.
// The ownership file is not a SQLite transaction and contains no task data.
public sealed class SqliteExecutionStore : IExecutionStore
{
    private readonly string databasePath;
    private readonly string connectionString;
    private sealed class Ownership(string taskId, string database, FileStream stream) : IExecutionOwnership
    {
        public string TaskId { get; } = taskId;
        public string Database { get; } = database;
        public bool Disposed { get; private set; }
        public async ValueTask DisposeAsync() { Disposed = true; await stream.DisposeAsync(); }
    }

    public SqliteExecutionStore(string databasePath)
    {
        if (!Path.IsPathFullyQualified(databasePath) || databasePath.StartsWith("\\\\", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(Path.GetFileName(databasePath)))
            throw new ArgumentException("An absolute local database file path is required.");
        this.databasePath = Path.GetFullPath(databasePath);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = this.databasePath, Pooling = false, DefaultTimeout = 5, Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var connection = new SqliteConnection(connectionString);
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
            if (version is not (0 or 1)) throw new InvalidOperationException("Unsupported execution database schema.");
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
            transaction.Commit();
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    public ValueTask<IExecutionOwnership?> TryAcquireAsync(string taskId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        var directory = databasePath + ".locks";
        Directory.CreateDirectory(directory);
        try
        {
            IExecutionOwnership owner = new Ownership(taskId, databasePath,
                new FileStream(Path.Combine(directory, DurableExecution.Hash(taskId) + ".lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None));
            return ValueTask.FromResult<IExecutionOwnership?>(owner);
        }
        catch (IOException) { return ValueTask.FromResult<IExecutionOwnership?>(null); }
    }

    private void CheckOwnership(string taskId, IExecutionOwnership ownership)
    {
        if (ownership is not Ownership own || own.Disposed || own.TaskId != taskId || own.Database != databasePath)
            throw new InvalidOperationException("Execution ownership is required.");
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
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT task_id, version, terminal, snapshot FROM executions WHERE task_id=$id;";
        command.Parameters.AddWithValue("$id", taskId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<DurableExecution>> ListRecoverableAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT task_id, version, terminal, snapshot FROM executions WHERE terminal=0 ORDER BY task_id LIMIT 101;";
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var records = new List<DurableExecution>();
        while (await reader.ReadAsync(cancellationToken)) records.Add(Read(reader));
        if (records.Count > 100) throw new InvalidOperationException("Recovery batch exceeds 100 records; recover individual TaskIds.");
        return records;
    }

    public async Task CreateAsync(DurableExecution execution, IExecutionOwnership ownership, CancellationToken cancellationToken = default)
    {
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
        DurableExecutionTransitions.Validate(expected, next);
        await using var connection = await OpenAsync(cancellationToken);
        // Compare the complete prior snapshot as well as version: callers cannot invent a prior state.
        using var command = connection.CreateCommand();
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
    }
}
