using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MAF.Core;

namespace MAF.Host;

internal sealed record LabProvenanceIdentity(string OperationId, HandoffTask Task,
    ExternalExecutionHandle Handle, LocalExecutionInput Binding, LocalEvidenceBudgets Budgets);
internal sealed record LabJournalHeader(int Version, LabProvenanceIdentity Identity, string Fingerprint, DateTimeOffset ExpiresAt);
internal sealed record LabJournalEvent(int Sequence, string Kind, int Attempt = 0, LabEffect? Effect = null,
    string? Session = null, ExternalExecutionStatus? Status = null, EvidenceAssessmentStatus? Review = null, HumanGateStatus? Gate = null,
    LabOpenCodeProtocol? Protocol = null);
internal sealed record LabProvenanceInspection(LabJournalHeader Header, LabJournalEvent[] Events)
{
    public bool CanResume => false; // Inspection never reconstructs admission or an execution capability.
    public string Receipt => Events.Any(e => e.Kind == "LabReceiptVerified") ? "FixtureVerifiedOriginalResponse / NotProductionAuthenticity" : "NotEstablished";
    public string Recovery => "Blocked / UnknownUntilOriginalEvidenceVerified";
    public string HumanAcceptance => "NotEstablished";
    public string Provenance => "PersonalLab / ControlledOfflineFixture / NotLiveProvider";
    public void DemandExact(LocalExecutionPreparation p, ExternalExecutionHandle handle)
    {
        p.DemandExact(p.Binding);
        if (Header.Fingerprint != PersonalLabOfflineOperation.Fingerprint(new(p.OperationId, p.Task, handle, p.Binding, p.EvidenceBudgets)))
            throw new ExternalHarnessException("LabProvenanceMismatch");
    }
}

// A per-operation, exclusive, flushed journal. Never opens/migrates a production execution DB.
// Same-principal writable data is provenance, not protected consent, policy or receipt evidence.
internal sealed class PersonalLabOfflineOperation : IDisposable
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    const int MaxBytes = 1024 * 1024;
    readonly object sync = new();
    readonly LocalExecutionPreparation preparation;
    readonly PersonalLabAdmission admission;
    readonly FileStream journal;
    readonly LabProvenanceIdentity identity;
    int sequence, attempt;
    bool disposed, faulted, outcomeRecorded;
    string? session;
    internal LocalExecutionPreparation Preparation => preparation;
    internal ExternalExecutionHandle OriginalHandle => identity.Handle;
    bool protocolBound;
    int receiptStep;
    internal string? DemandActiveSession()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (faulted) throw new IOException("LabJournalUnavailable");
            admission.DemandRunning(preparation);
            return receiptStep == 3 ? session : null;
        }
    }
    internal void BindLabProtocol(LabOpenCodeProtocol protocol)
    {
        lock (sync)
        {
            if (protocolBound || attempt != 0 || protocol.Recipient != preparation.Binding.HarnessRecipient)
                throw new ExternalHarnessException("LabProtocolBindingMismatch");
            admission.DemandRunning(preparation);
            Write(new LabJournalEvent(++sequence, "LabProtocolBound", Protocol: protocol));
            protocolBound = true;
        }
    }
    internal void RecordSessionEvidence(string kind, string originalSession)
    {
        lock (sync)
        {
            var expected = receiptStep switch { 0 => "LabSessionCandidate", 1 => "LabResponseCorrelated", 2 => "LabReceiptVerified", _ => "" };
            if (!protocolBound || kind != expected || originalSession != session || session is null)
                throw new ExternalHarnessException("LabReceiptSequenceMismatch");
            admission.DemandRunning(preparation);
            Write(new LabJournalEvent(++sequence, kind, Session: session));
            receiptStep++;
        }
    }
    PersonalLabOfflineOperation(LocalExecutionPreparation p, PersonalLabAdmission admission, ExternalExecutionHandle handle, FileStream journal)
    { preparation = p; this.admission = admission; identity = new(p.OperationId, p.Task, handle, p.Binding, p.EvidenceBudgets); this.journal = journal; }

    internal static string Fingerprint(LabProvenanceIdentity identity)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(identity, Json)));
    static string FileName(string directory, string operation)
    {
        if (!Regex.IsMatch(operation, "\\A[a-f0-9]{32}\\z")) throw new ArgumentException("InvalidLabOperationId");
        return Path.Combine(LocalSoloApplication.ValidateWorkspace(directory), operation + ".lab-journal");
    }
    public static PersonalLabOfflineOperation Begin(LocalExecutionPreparation p, PersonalLabAdmission admission,
        ExternalExecutionHandle original, string applicationStorageDirectory)
    {
        admission.DemandPreparation(p);
        p.DemandExact(p.Binding);
        if (p.Binding.Profile != LocalExecutionProfile.PersonalLab || original.Provider != "OpenCode"
            || original.ExecutionId != admission.ExecutionId || original.ExternalSessionId is not null
            || !HandoffValidation.Text(original.ExternalRequestId!, 160) || LocalSoloApplication.UnsafeCharacters(original.ExternalRequestId!))
            throw new ExternalHarnessException("LabProvenanceBindingRequired");
        var relativeStorage = Path.GetRelativePath(p.Task.Workspace, LocalSoloApplication.ValidateWorkspace(applicationStorageDirectory));
        if (!Path.IsPathRooted(relativeStorage) && relativeStorage != ".."
            && !relativeStorage.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ExternalHarnessException("LabStorageOutsideWorkspaceRequired");
        admission.BeginOffline();
        PersonalLabOfflineOperation? operation = null;
        try
        {
            var stream = new FileStream(FileName(applicationStorageDirectory, p.OperationId), FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            operation = new(p, admission, original, stream);
            operation.Write(new LabJournalHeader(1, operation.identity, Fingerprint(operation.identity), admission.ExpiresAt));
            return operation;
        }
        catch { admission.Revoke(); operation?.journal.Dispose(); throw; }
    }
    void Write<T>(T value)
    {
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (faulted || sequence >= 260) throw new IOException("LabJournalUnavailable");
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json) + "\n");
            if (journal.Length + bytes.Length > MaxBytes) throw new IOException("LabJournalBoundExceeded");
            journal.Write(bytes); journal.Flush(flushToDisk: true);
        }
        catch { faulted = true; admission.Revoke(); throw; }
    }
    public DurableAttempt Reserve(LabEffect effect, LabEffectTarget target = LabEffectTarget.ControlledOfflineFixture,
        CancellationToken token = default)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (faulted) throw new IOException("LabJournalUnavailable");
            if (protocolBound && effect is LabEffect.SubmitPrompt or LabEffect.Observe && receiptStep != 3)
                throw new ExternalHarnessException("LabReceiptRequired");
            var request = new LabEffectRequest(preparation.OperationId, preparation.Task, identity.Handle.ExecutionId,
                preparation.Binding, preparation.EvidenceBudgets, effect, target);
            var reserved = PersonalLabAdmission.Reserve(admission, request, token);
            try
            {
                var id = ++attempt;
                Write(new LabJournalEvent(++sequence, "Intent", id, effect)); // Durable BEFORE any fixture effect.
                admission.DemandRunning(preparation); // Flush IO cannot extend lifetime or bypass revocation.
                token.ThrowIfCancellationRequested();
                return new(this, reserved, id, effect);
            }
            catch { reserved.Dispose(); throw; }
        }
    }
    public LocalResultView RecordOutcome(WorkflowOutcome outcome)
    {
        lock (sync)
        {
            if (outcomeRecorded) throw new ExternalHarnessException("LabOutcomeAlreadyRecorded");
            if (outcome.TaskId != identity.Task.TaskId || outcome.WorkflowRunId != identity.Task.WorkflowRunId)
                throw new ExternalHarnessException("LabProvenanceMismatch");
            if (outcome.ExternalResult is { } result && (result.Handle.ExecutionId != identity.Handle.ExecutionId
                || result.TaskId != identity.Task.TaskId || result.WorkflowRunId != identity.Task.WorkflowRunId
                || result.Handle.Provider != identity.Handle.Provider || result.Handle.ExternalRequestId != identity.Handle.ExternalRequestId
                || result.Handle.ExternalSessionId != session)) throw new ExternalHarnessException("LabProvenanceMismatch");
            Write(new LabJournalEvent(++sequence, "ProviderObservation", Status: outcome.ExternalResult?.Status,
                Review: outcome.Review?.Status, Gate: outcome.HumanGate));
            Write(new LabJournalEvent(++sequence, "Consumed")); admission.Consume();
            outcomeRecorded = true;
            return LocalSoloOutcomeMapper.FromControlledWorkflow(outcome);
        }
    }
    public void Revoke()
    { lock (sync) { admission.Revoke(); Write(new LabJournalEvent(++sequence, "Revoked")); } }
    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            try { admission.Consume(); }
            finally { disposed = true; journal.Dispose(); }
        }
    }
    public static LabProvenanceInspection Inspect(string applicationStorageDirectory, string operationId)
    {
        var path = FileName(applicationStorageDirectory, operationId);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("LabJournalReparsePoint");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        if (file.Length > MaxBytes) throw new IOException("LabJournalBoundExceeded");
        using var reader = new StreamReader(file, new UTF8Encoding(false, true));
        var text = reader.ReadToEnd();
        if (!text.EndsWith('\n')) throw new IOException("LabJournalIncomplete");
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length is < 1 or > 261) throw new IOException("LabJournalBoundExceeded");
        var header = JsonSerializer.Deserialize<LabJournalHeader>(lines[0], Json) ?? throw new IOException("LabJournalInvalid");
        if (header.Version != 1 || header.Identity.OperationId != operationId || header.Identity.Binding.Profile != LocalExecutionProfile.PersonalLab
            || header.Identity.Binding.Access != ExternalExecutionPolicy.ReadOnly || header.Identity.Handle.Provider != "OpenCode"
            || header.Fingerprint != Fingerprint(header.Identity)) throw new IOException("LabJournalInvalid");
        var events = lines.Skip(1).Select(line => JsonSerializer.Deserialize<LabJournalEvent>(line, Json) ?? throw new IOException("LabJournalInvalid")).ToArray();
        var pending = new Dictionary<int, LabEffect>();
        bool create = false, prompt = false, createReturned = false, terminalAdmission = false;
        int nextAttempt = 0, receipt = 0;
        string? originalSession = null;
        bool labProtocol = false;
        for (var i = 0; i < events.Length; i++)
        {
            var e = events[i];
            if (e.Sequence != i + 1) throw new IOException("LabJournalSequenceInvalid");
            if (e.Kind == "Intent")
            {
                if (terminalAdmission || e.Attempt != ++nextAttempt || e.Effect is not { } effect || !Enum.IsDefined(effect) || !pending.TryAdd(e.Attempt, effect)) throw new IOException("LabJournalInvalid");
                if (effect == LabEffect.CreateSession) { if (create) throw new IOException("LabJournalDuplicatePost"); create = true; }
                if (effect == LabEffect.SubmitPrompt) { if (prompt || !createReturned || labProtocol && receipt != 3) throw new IOException("LabJournalDuplicatePost"); prompt = true; }
            }
            else if (e.Kind is "Returned" or "Uncertain")
            {
                if (!pending.Remove(e.Attempt, out var effect) || e.Effect != effect) throw new IOException("LabJournalInvalid");
                if (e.Kind == "Returned" && effect == LabEffect.CreateSession) createReturned = true;
                if (e.Kind == "Returned" && effect == LabEffect.CreateSession) originalSession = e.Session;
                if (e.Session is not null && (e.Kind != "Returned" || effect != LabEffect.CreateSession || !Regex.IsMatch(e.Session, "\\Ases_[A-Za-z0-9_-]{1,100}\\z"))) throw new IOException("LabJournalInvalid");
            }
            else if (e.Kind == "LabProtocolBound")
            {
                if (labProtocol || nextAttempt != 0 || e.Protocol is not { } protocol || protocol.Recipient != header.Identity.Binding.HarnessRecipient)
                    throw new IOException("LabJournalInvalid");
                LabOpenCodeBoundary.ValidateProtocol(protocol); labProtocol = true;
            }
            else if (e.Kind is "LabSessionCandidate" or "LabResponseCorrelated" or "LabReceiptVerified")
            {
                var expected = receipt switch { 0 => "LabSessionCandidate", 1 => "LabResponseCorrelated", 2 => "LabReceiptVerified", _ => "" };
                if (!labProtocol || terminalAdmission || !createReturned || originalSession is null || e.Session != originalSession || e.Kind != expected)
                    throw new IOException("LabJournalInvalid");
                receipt++;
            }
            else if (e.Kind is not ("ProviderObservation" or "Consumed" or "Revoked")) throw new IOException("LabJournalInvalid");
            if (e.Protocol is not null && e.Kind != "LabProtocolBound") throw new IOException("LabJournalInvalid");
            if (e.Status is { } status && (e.Kind != "ProviderObservation" || !Enum.IsDefined(status))) throw new IOException("LabJournalInvalid");
            if (e.Review is { } review && (e.Kind != "ProviderObservation" || !Enum.IsDefined(review))
                || e.Gate is { } gate && (e.Kind != "ProviderObservation" || !Enum.IsDefined(gate))) throw new IOException("LabJournalInvalid");
            if (e.Kind is "Consumed" or "Revoked") terminalAdmission = true;
        }
        return new(header, events); // Pending intents are preserved, never converted to returned/verified.
    }
    internal sealed class DurableAttempt(PersonalLabOfflineOperation owner, PersonalLabAdmission.OfflineAttempt reserved, int id, LabEffect effect) : IDisposable
    {
        bool finished;
        public void RecordReturned(string? observedSession = null)
        {
            lock (owner.sync)
            {
                if (finished) throw new ExternalHarnessException("LabObservationAlreadyRecorded");
                if (observedSession is not null && (effect != LabEffect.CreateSession || !Regex.IsMatch(observedSession, "\\Ases_[A-Za-z0-9_-]{1,100}\\z")))
                    throw new ArgumentException("InvalidLabSessionObservation");
                owner.Write(new LabJournalEvent(++owner.sequence, "Returned", id, effect, observedSession));
                reserved.RecordReturned(); finished = true;
                if (observedSession is not null) owner.session = observedSession; // Candidate only, never a verified receipt.
            }
        }
        public void Dispose()
        {
            lock (owner.sync)
            {
                if (finished) return;
                try { owner.Write(new LabJournalEvent(++owner.sequence, "Uncertain", id, effect)); }
                finally { reserved.Dispose(); finished = true; }
            }
        }
    }
}
