using System.Globalization;
using System.Text;

namespace MAF.Core.Policy;

// Preserve the M2-A policy_authority digest encoding, including its domain and ordering.
// A digest detects mismatched content; it does not authenticate the source.
public static class PolicySnapshotBinding
{
    static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    public static string Digest(PolicySnapshot p)
    {
        if (!DeterministicPolicyEvaluator.ValidSnapshot(p, p.NotBefore)) throw new InvalidOperationException("InvalidAuthorityPolicy");
        if (!PolicyValidation.Text(p.AuthorityId)) throw new InvalidOperationException("InvalidAuthorityPolicy");
        // Fixed fields and ordinal-sorted collections with BinaryWriter length-prefixed UTF-8 strings.
        using var m = new MemoryStream(); using var w = new BinaryWriter(m, new UTF8Encoding(false, true), leaveOpen: true);
        w.Write("MAF.PolicySnapshot.v1"); w.Write(p.AuthorityId); w.Write(p.Epoch.Value); w.Write(p.Version.Value);
        w.Write(Utc(p.NotBefore)); w.Write(Utc(p.NotAfter)); w.Write(p.DecisionLifetime.Ticks);
        w.Write(p.Capabilities.Length);
        foreach (var d in p.Capabilities.OrderBy(d => d.ConnectorId, StringComparer.Ordinal).ThenBy(d => d.Capability))
        {
            w.Write(d.ConnectorId); w.Write(d.AdapterVersion); w.Write((int)d.Capability); w.Write((int)d.Boundary); w.Write(d.Enabled); w.Write(d.Healthy);
            w.Write(d.SupportedOperations.Length); foreach (var op in d.SupportedOperations.Order()) w.Write((int)op);
        }
        var rules = p.Rules.Select(r =>
        {
            using var rm = new MemoryStream(); using var rw = new BinaryWriter(rm, new UTF8Encoding(false, true));
            rw.Write(r.CallerSubject); rw.Write(r.ConnectorId); rw.Write((int)r.Capability); rw.Write((int)r.Operation);
            rw.Write(r.Scope.ResourceId); rw.Write(r.Scope.ProviderScopeDigest); rw.Write(r.Scope.AgentPolicyDigest);
            rw.Write(r.Scope.ConfigurationRevision); rw.Write(r.Scope.ScopeRevision); rw.Write((int)r.Scope.Access); rw.Write(r.Scope.BudgetTicks);
            rw.Write((int)r.Outcome); rw.Write(r.RequiredOperatorScope ?? ""); rw.Flush(); return Convert.ToHexString(rm.ToArray());
        }).Order(StringComparer.Ordinal).ToArray();
        w.Write(rules.Length); foreach (var r in rules) w.Write(r); w.Flush();
        return DurableExecution.Hash(Convert.ToHexString(m.ToArray()));
    }

}
