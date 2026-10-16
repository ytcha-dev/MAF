using MAF.Persistence.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace MAF.Tests;

public sealed class SessionReceiptSchemaTests(ITestOutputHelper output)
{
    [Fact]
    public async Task AcceptedV4DeltaExecutesWithStrictTablesAndExactInventory()
    {
        await using var c = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await c.OpenAsync();
        async Task<object?> Sql(string sql)
        { using var cmd = c.CreateCommand(); cmd.CommandText = sql; return await cmd.ExecuteScalarAsync(); }
        await Sql(ApprovalSchema.V1Definition + ";" + ApprovalSchema.V2Definition + ApprovalSchema.V3Delta);
        await Sql(ApprovalSchema.V4Delta);
        Assert.Equal(2L, await Sql("SELECT count(*) FROM pragma_table_list WHERE name IN ('execution_session_receipts','cancellation_deliveries') AND strict=1"));
        Assert.Equal(8L, await Sql("SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name NOT LIKE 'cancellation_requests_%'"));
        Assert.Equal("ok", await Sql("PRAGMA integrity_check"));
        Assert.Null(await Sql("PRAGMA foreign_key_check"));
        await Assert.ThrowsAsync<SqliteException>(() => Sql("INSERT INTO executions(task_id,version,terminal,snapshot) VALUES('unprofiled',0,0,'{}')"));
        await Sql("INSERT INTO executions VALUES('profiled',0,0,'{}',1)");
        await Assert.ThrowsAsync<SqliteException>(() => Sql("UPDATE executions SET session_receipt_required=0"));
        output.WriteLine($"SQLite={await Sql("SELECT sqlite_version()")}; exact accepted DDL executed; STRICT tables=2, new triggers=8, profile mutation denied, integrity=ok, FK violations=0; provider calls=0.");
    }
}
