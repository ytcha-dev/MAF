using MAF.ApprovalAcceptance;
using MAF.Core.Policy;
using MAF.Persistence.Sqlite;

namespace MAF.Tests;

public sealed class AuditProjectionTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "maf-audit-projection-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationProjectionPreservesIdentityAndOrdinaryNull(bool v4)
    {
        var fixture = new SyntheticCancellation(Path.Combine(root, "synthetic.db"), v4: v4);
        await fixture.Setup();
        var before = await fixture.Store().ReadAuditAsync();
        Assert.NotEmpty(before);
        Assert.All(before, row => Assert.Null(row.CancelRequestId));

        await fixture.Application().RequestAsync(fixture.Command, fixture.Canceller);
        var audit = await fixture.Store().ReadAuditAsync();
        Assert.Equal(before, audit.Take(before.Count).ToArray());
        var cancellation = Assert.Single(audit.Skip(before.Count));
        Assert.Equal(ControlAuditCode.CancellationRequested, cancellation.Code);
        Assert.Equal(fixture.Command.CancelRequestId, cancellation.CancelRequestId);
        await VerifyPaging(fixture.Store(), audit);
    }

    [Fact]
    public async Task V2WithoutCancellationColumnPreservesOrdinaryAuditAndPaging()
    {
        Directory.CreateDirectory(root);
        var db = Path.Combine(root, "synthetic.db");
        await ApprovalSchema.InitializeOrMigrateAsync(db, SyntheticApproval.Maintenance);
        var store = new SqliteApprovalStore(db, new SyntheticAuthority(), new SyntheticClock(), ApprovalStoreProfile.ExplicitV2);
        await store.ActivatePolicyAsync();
        await store.RequestAsync("approval-one", SyntheticApproval.Request(), SyntheticApproval.Caller);
        var audit = await store.ReadAuditAsync();
        Assert.Equal(2, audit.Count);
        Assert.All(audit, row => Assert.Null(row.CancelRequestId));
        await VerifyPaging(store, audit);
    }

    static async Task VerifyPaging(SqliteApprovalStore store, IReadOnlyList<ControlAudit> audit)
    {
        Assert.Equal(audit.OrderBy(row => row.Sequence).ToArray(), audit.ToArray());
        var paged = new List<ControlAudit>();
        long after = 0;
        foreach (var expected in audit)
        {
            var row = Assert.Single(await store.ReadAuditAsync(after, 1));
            Assert.Equal(expected, row);
            paged.Add(row);
            after = row.Sequence;
        }
        Assert.Equal(audit.ToArray(), paged.ToArray());
        Assert.Empty(await store.ReadAuditAsync(after, 100));
        Assert.Equal(audit.ToArray(), (await store.ReadAuditAsync(0, 100)).ToArray());
        foreach (var limit in new[] { -1, 0, 101 })
            Assert.Equal("InvalidPageLimit", (await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAuditAsync(0, limit))).Message);
        Assert.Equal("InvalidPageLimit", (await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAuditAsync(-1))).Message);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
