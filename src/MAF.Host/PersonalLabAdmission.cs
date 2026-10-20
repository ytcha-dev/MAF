using System.Collections.Immutable;
using MAF.Core;

namespace MAF.Host;

internal enum PersonalLabState { Admitted, Running, Consumed, Expired, Revoked, Cancelled }
internal enum LabEffect { PcaInference, HarnessValidation, CreateSession, SubmitPrompt, Observe, ReviewerInference }
internal enum LabEffectTarget { ControlledOfflineFixture, Network }
internal sealed record LabEffectRequest(string OperationId, HandoffTask Task, string ExecutionId,
    LocalExecutionInput Binding, LocalEvidenceBudgets Budgets, LabEffect Effect, LabEffectTarget Target);
internal sealed record LabEffectObservation(string OperationId, string ExecutionId, LabEffect Effect, bool Returned)
{
    public string Provenance => "PersonalLab / ControlledOfflineFixture / NotLiveProvider";
}

// Cooperative research admission only. No production interfaces, policy, HTTP, database or
// model dependencies. Authorizes bounded OFFLINE fixture effects, never a network capability.
internal sealed class PersonalLabAdmission
{
    readonly object sync = new();
    readonly LocalExecutionPreparation preparation;
    readonly TimeProvider clock;
    readonly CancellationToken cancellation;
    readonly DateTimeOffset issuedAt;
    readonly long issuedTimestamp;
    readonly TimeSpan lifetime;
    readonly List<LabEffectObservation> observations = [];
    PersonalLabState state = PersonalLabState.Admitted;
    int attempts, pcaCalls, reviewerCalls;
    bool createReserved, promptReserved, createReturned;
    public string ExecutionId { get; }
    public DateTimeOffset ExpiresAt { get; }
    public PersonalLabState State { get { lock (sync) { Refresh(); return state; } } }
    public ImmutableArray<LabEffectObservation> Observations { get { lock (sync) return [..observations]; } }
    public int ReservedEffects { get { lock (sync) return attempts; } }

    private PersonalLabAdmission(LocalExecutionPreparation preparation, string executionId,
        TimeProvider clock, CancellationToken cancellation)
    {
        this.preparation = preparation; ExecutionId = executionId;
        this.clock = clock; this.cancellation = cancellation;
        issuedAt = clock.GetUtcNow(); issuedTimestamp = clock.GetTimestamp();
        lifetime = preparation.Task.StageTimeout * 4; // Existing four-stage journey; no background renewal.
        ExpiresAt = issuedAt + lifetime;
    }

    internal static PersonalLabAdmission AdmitOffline(LocalExecutionPreparation? preparation,
        LocalExecutionInput exactIntention, string executionId, TimeProvider clock, CancellationToken cancellation = default)
    {
        if (preparation is null) throw new ExternalHarnessException("LabPreparationRequired");
        preparation.DemandExact(exactIntention);
        if (exactIntention.Profile != LocalExecutionProfile.PersonalLab)
            throw new ExternalHarnessException("LabProfileRequired");
        if (!HandoffValidation.Text(executionId, 160) || LocalSoloApplication.UnsafeCharacters(executionId))
            throw new ArgumentException("InvalidLabExecutionIdentity");
        ArgumentNullException.ThrowIfNull(clock);
        cancellation.ThrowIfCancellationRequested();
        var admission = new PersonalLabAdmission(preparation, executionId, clock, cancellation);
        preparation.ClaimOfflineLabAdmission(); // One admission per prepared operation, including after expiry/revocation.
        return admission;
    }

    public void BeginOffline()
    {
        lock (sync)
        {
            Demand(PersonalLabState.Admitted);
            state = PersonalLabState.Running; // No new Task/WorkflowRun/Execution identity allocated.
        }
    }
    public void Revoke()
    { lock (sync) { Refresh(); if (state is PersonalLabState.Admitted or PersonalLabState.Running) state = PersonalLabState.Revoked; } }
    public void Consume()
    { lock (sync) { Refresh(); if (state is PersonalLabState.Admitted or PersonalLabState.Running) state = PersonalLabState.Consumed; } }

    void Refresh()
    {
        if (state is not (PersonalLabState.Admitted or PersonalLabState.Running)) return;
        if (cancellation.IsCancellationRequested) state = PersonalLabState.Cancelled;
        else if (clock.GetUtcNow() < issuedAt || clock.GetUtcNow() >= ExpiresAt
            || clock.GetElapsedTime(issuedTimestamp) >= lifetime) state = PersonalLabState.Expired;
    }
    void Demand(PersonalLabState expected)
    {
        Refresh();
        if (state != expected) throw new ExternalHarnessException("LabAdmission" + state);
    }

    public static OfflineAttempt Reserve(PersonalLabAdmission? admission, LabEffectRequest request,
        CancellationToken cancellation = default)
        => (admission ?? throw new ExternalHarnessException("LabAdmissionRequired")).Reserve(request, cancellation);

    OfflineAttempt Reserve(LabEffectRequest request, CancellationToken token)
    {
        lock (sync)
        {
            Demand(PersonalLabState.Running);
            token.ThrowIfCancellationRequested();
            if (request.OperationId != preparation.OperationId || request.Task != preparation.Task
                || request.ExecutionId != ExecutionId || request.Binding != preparation.Binding
                || request.Budgets != preparation.EvidenceBudgets)
                throw new ExternalHarnessException("LabEffectBindingMismatch");
            if (request.Target != LabEffectTarget.ControlledOfflineFixture || !Enum.IsDefined(request.Effect))
                throw new ExternalHarnessException("LabEffectUnavailable");
            preparation.DemandExact(request.Binding); // Recheck directory metadata; no paths supplied by a model.
            Demand(PersonalLabState.Running); // Metadata IO cannot extend the admitted lifetime.
            token.ThrowIfCancellationRequested();
            if (attempts >= 128) throw new ExternalHarnessException("LabEffectBudgetExceeded");
            switch (request.Effect)
            {
                case LabEffect.PcaInference when pcaCalls >= 8:
                case LabEffect.ReviewerInference when reviewerCalls >= preparation.EvidenceBudgets.ReviewerModelCalls:
                    throw new ExternalHarnessException("LabModelBudgetExceeded");
                case LabEffect.CreateSession when createReserved:
                case LabEffect.SubmitPrompt when promptReserved || !createReturned:
                    throw new ExternalHarnessException("LabSubmissionUnavailable");
            }
            if (request.Effect == LabEffect.CreateSession) createReserved = true;
            if (request.Effect == LabEffect.SubmitPrompt) promptReserved = true;
            if (request.Effect == LabEffect.PcaInference) pcaCalls++;
            if (request.Effect == LabEffect.ReviewerInference) reviewerCalls++;
            attempts++; // Reserve BEFORE the simulated effect. Failure/ambiguity never refunds POST intent.
            return new(this, request);
        }
    }

    // A fixture callback records what returned, not policy approval, a v4 receipt, provider
    // terminal state or human acceptance. An unreported/throwing effect remains uncertain.
    internal sealed class OfflineAttempt : IDisposable
    {
        readonly PersonalLabAdmission owner;
        readonly LabEffectRequest request;
        bool finished;
        internal OfflineAttempt(PersonalLabAdmission owner, LabEffectRequest request)
        { this.owner = owner; this.request = request; }
        public void RecordReturned()
        {
            lock (owner.sync)
            {
                if (finished) throw new ExternalHarnessException("LabObservationAlreadyRecorded");
                finished = true;
                owner.observations.Add(new(request.OperationId, request.ExecutionId, request.Effect, true));
                if (request.Effect == LabEffect.CreateSession) owner.createReturned = true;
            }
        }
        public void Dispose()
        {
            lock (owner.sync)
            {
                if (finished) return;
                finished = true;
                owner.observations.Add(new(request.OperationId, request.ExecutionId, request.Effect, false));
            }
        }
    }
}
