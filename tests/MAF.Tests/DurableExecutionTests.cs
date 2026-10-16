using System.Text;
using MAF.Core;
using MAF.Host;
using MAF.Persistence.Sqlite;
using MAF.Workflows.External;

namespace MAF.Tests;

// NOT EXECUTED: security-event pause applies to every case in this file.
// Uses bounded disposable SQLite resources, fake provider only, no child process or HTTP.
public sealed class DurableExecutionTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("maf-durable-").FullName;
    private string Database => Path.Combine(directory, "execution.db");
    private SqliteExecutionStore Store() => new(Database);
    private ExternalTaskRequest Request(string id = "task") => new(id, directory, "Read the synthetic README.", TimeSpan.FromSeconds(1));
    private static DurableExecutionService Service(IExecutionStore store, FakeRemote remote)
        => new(HistoricalRuntimeAccess.Instance, store, new FakeClient(remote), TimeSpan.FromMilliseconds(1));

    [Fact]
    public async Task CompletionAndIdentitySurviveNewServiceAndStoreInstances()
    {
        var remote = new FakeRemote();
        ExternalWorkflowRun first;
        using (var original = Service(Store(), remote)) first = await original.RunAsync(Request());
        var saved = await Store().GetAsync("task");
        Assert.NotNull(saved);
        Assert.Equal(DurableExecutionState.Completed, saved.State);
        Assert.NotNull(saved.CompletedAt);
        // The original service is no longer retained; a fresh client must reattach.
        var recovered = await Service(Store(), remote).RecoverAsync("task", directory, TimeSpan.FromSeconds(1));
        Assert.Equal(first.Result.Handle, recovered.Result.Handle);
        Assert.Equal(first.Result.WorkflowRunId, recovered.Result.WorkflowRunId);
        Assert.Equal(ExternalExecutionStatus.Completed, recovered.Result.Status);
        Assert.Equal("Synthetic final answer", recovered.Result.Summary);
        Assert.Equal(1, remote.Creates);
        Assert.Equal(1, remote.Submits);
        Assert.Equal(1, remote.Reattaches);
    }

    [Fact]
    public async Task SameTaskAndFingerprintDoNotSubmitTwice()
    {
        var remote = new FakeRemote();
        var first = await Service(Store(), remote).RunAsync(Request());
        var second = await Service(Store(), remote).RunAsync(Request());
        Assert.Equal(first.Result.Handle, second.Result.Handle);
        Assert.Equal(1, remote.Submits);
        Assert.Equal(1, remote.Creates);
    }

    [Fact]
    public async Task ChangedRequestIsRejectedBeforeProviderAccess()
    {
        var remote = new FakeRemote();
        await Service(Store(), remote).RunAsync(Request());
        await Assert.ThrowsAsync<ArgumentException>(() => Service(Store(), remote).RunAsync(Request() with { TaskText = "Different" }));
        Assert.Equal(1, remote.Submits);
        Assert.Equal(0, remote.Reattaches);
    }

    [Theory]
    [InlineData(DurableExecutionState.Created, false, 0)]
    [InlineData(DurableExecutionState.SessionCreating, false, 0)]
    [InlineData(DurableExecutionState.ReadyToSubmit, false, 0)]
    [InlineData(DurableExecutionState.SubmissionIntended, false, 0)]
    [InlineData(DurableExecutionState.Running, true, 1)]
    [InlineData(DurableExecutionState.Completed, true, 1)]
    public async Task InjectedCrashAtPersistenceBoundaryNeverResubmits(DurableExecutionState boundary, bool accepted, int submits)
    {
        var remote = new FakeRemote();
        var faulty = new FaultStore(Store(), (record, after) => record.State == boundary && after);
        await Assert.ThrowsAnyAsync<Exception>(() => Service(faulty, remote).RunAsync(Request()));
        var saved = await Store().GetAsync("task");
        Assert.NotNull(saved);
        Assert.Equal(boundary, saved.State);
        Assert.Equal(submits, remote.Submits);
        var recovered = await Service(Store(), remote).RecoverAsync("task", directory, TimeSpan.FromSeconds(1), observeOnce: true);
        Assert.Equal(accepted ? ExternalExecutionStatus.Completed :
            boundary == DurableExecutionState.SubmissionIntended ? ExternalExecutionStatus.Running : ExternalExecutionStatus.Unknown, recovered.Result.Status);
        Assert.Equal(saved.Handle, recovered.Result.Handle);
        Assert.Equal(submits, remote.Submits);
        Assert.True(remote.Creates <= 1);
    }

    [Fact]
    public async Task PersistenceFailureBeforeIntentPreventsPromptPost()
    {
        var remote = new FakeRemote();
        var faulty = new FaultStore(Store(), (record, after) => record.State == DurableExecutionState.SubmissionIntended && !after);
        await Assert.ThrowsAnyAsync<Exception>(() => Service(faulty, remote).RunAsync(Request()));
        Assert.Equal(0, remote.Submits);
        var recovered = await Service(Store(), remote).RecoverAsync("task", directory, TimeSpan.FromSeconds(1));
        Assert.Equal(ExternalExecutionStatus.Unknown, recovered.Result.Status);
        Assert.Equal(0, remote.Submits);
    }

    [Fact]
    public async Task FailureBeforeInitialDurableRecordNeverReachesProvider()
    {
        var remote = new FakeRemote();
        var faulty = new FaultStore(Store(), (record, after) => record.State == DurableExecutionState.Created && !after);
        await Assert.ThrowsAnyAsync<Exception>(() => Service(faulty, remote).RunAsync(Request()));
        Assert.Null(await Store().GetAsync("task"));
        Assert.Equal(0, remote.Creates);
        Assert.Equal(0, remote.Submits);
    }

    [Fact]
    public async Task LostCreateResponseDoesNotCreateReplacementSession()
    {
        var remote = new FakeRemote { LoseCreateResponse = true };
        var first = await Service(Store(), remote).RunAsync(Request());
        Assert.Equal(ExternalExecutionStatus.Unknown, first.Result.Status);
        var recovered = await Service(Store(), remote).RecoverAsync("task", directory, TimeSpan.FromSeconds(1));
        Assert.Equal(ExternalExecutionStatus.Unknown, recovered.Result.Status);
        Assert.Equal(1, remote.Creates);
        Assert.Equal(0, remote.Submits);
    }

    [Fact]
    public async Task AcceptedPostWithLostResponseRecoversOriginalMessage()
    {
        var remote = new FakeRemote { LoseSubmitResponse = true };
        var first = await Service(Store(), remote).RunAsync(Request());
        Assert.Equal(ExternalExecutionStatus.Completed, first.Result.Status);
        var recovered = await Service(Store(), remote).RecoverAsync("task", directory, TimeSpan.FromSeconds(1));
        Assert.Equal(first.Result.Handle.ExternalRequestId, recovered.Result.Handle.ExternalRequestId);
        Assert.Equal(first.Result.Handle.ExternalSessionId, recovered.Result.Handle.ExternalSessionId);
        Assert.Equal(1, remote.Submits);
    }

    [Fact]
    public async Task CrashAfterRemoteAcceptedBeforeLocalAcknowledgmentRecovers()
    {
        var remote = new FakeRemote { CrashAfterSubmit = true };
        await Assert.ThrowsAnyAsync<Exception>(() => Service(Store(), remote).RunAsync(Request()));
        Assert.Equal(DurableExecutionState.SubmissionIntended, (await Store().GetAsync("task"))!.State);
        remote.CrashAfterSubmit = false;
        var recovered = await Service(Store(), remote).RecoverAsync("task", directory, TimeSpan.FromSeconds(1));
        Assert.Equal(ExternalExecutionStatus.Completed, recovered.Result.Status);
        Assert.Equal(1, remote.Submits);
    }

    [Fact]
    public async Task ObservationTimeoutLeavesRemoteActiveAndLaterRecoveryFindsCompletion()
    {
        var remote = new FakeRemote { Status = ExternalExecutionStatus.Running };
        var first = await Service(Store(), remote).RunAsync(Request());
        Assert.Equal(ExternalExecutionStatus.Unknown, first.Result.Status);
        Assert.Equal(0, remote.Aborts);
        var running = await Service(Store(), remote).RecoverAsync("task", directory, TimeSpan.FromSeconds(1), observeOnce: true);
        Assert.Equal(ExternalExecutionStatus.Running, running.Result.Status);
        remote.Status = ExternalExecutionStatus.Completed;
        var final = await Service(Store(), remote).RecoverAsync("task", directory, TimeSpan.FromSeconds(1));
        Assert.Equal(ExternalExecutionStatus.Completed, final.Result.Status);
        Assert.Equal(first.Result.Handle, final.Result.Handle);
        Assert.Equal(1, remote.Submits);
    }

    [Fact]
    public async Task ProviderUnavailabilityIsUnknownAndSubsequentRecoveryDoesNotSubmit()
    {
        var remote = new FakeRemote { CrashAfterSubmit = true };
        await Assert.ThrowsAnyAsync<Exception>(() => Service(Store(), remote).RunAsync(Request()));
        var identity = (await Store().GetAsync("task"))!.Handle;
        remote.Unavailable = true;
        var unavailable = await Service(Store(), remote).RecoverAsync("task", directory, TimeSpan.FromSeconds(1));
        Assert.Equal(ExternalExecutionStatus.Unknown, unavailable.Result.Status);
        Assert.Equal("ReconciliationUnavailable", unavailable.Result.ErrorCode);
        Assert.Null((await Store().GetAsync("task"))!.CompletedAt);
        remote.Unavailable = false;
        var final = await Service(Store(), remote).RecoverAsync("task", directory, TimeSpan.FromSeconds(1));
        Assert.Equal(ExternalExecutionStatus.Completed, final.Result.Status);
        Assert.Equal(identity, final.Result.Handle);
        Assert.Equal(1, remote.Submits);
    }

    [Theory]
    [InlineData(ExternalExecutionStatus.WaitingForApproval)]
    [InlineData(ExternalExecutionStatus.Cancelled)]
    [InlineData(ExternalExecutionStatus.Partial)]
    [InlineData(ExternalExecutionStatus.Failed)]
    public async Task NonSuccessStatesRetainTheirSemantics(ExternalExecutionStatus status)
    {
        var remote = new FakeRemote { Status = status };
        var run = await Service(Store(), remote).RunAsync(Request());
        Assert.Equal(status, run.Result.Status);
        Assert.NotEqual(0, ExternalConsole.ExitCode(status));
        var saved = (await Store().GetAsync("task"))!;
        Assert.Equal(status != ExternalExecutionStatus.WaitingForApproval, saved.IsTerminal);
    }

    [Fact]
    public async Task ConcurrentOwnersAcrossStoreInstancesCannotSubmitSameTask()
    {
        await using var owner = await Store().TryAcquireAsync("task");
        Assert.NotNull(owner);
        var remote = new FakeRemote();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(Store(), remote).RunAsync(Request()));
        Assert.Equal(0, remote.Creates);
        Assert.Equal(0, remote.Submits);
    }

    [Fact]
    public async Task CompetingServicesWhileFirstObservesCannotCreateOrSubmitAgain()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remote = new FakeRemote { OnObservation = () => started.TrySetResult(), ObservationGate = release };
        using var firstService = Service(Store(), remote);
        var first = firstService.RunAsync(Request() with { Timeout = TimeSpan.FromSeconds(5) });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Service(Store(), remote).RunAsync(Request()));
        }
        finally { release.TrySetResult(); }
        Assert.Equal(ExternalExecutionStatus.Completed, (await first).Result.Status);
        Assert.Equal(1, remote.Creates);
        Assert.Equal(1, remote.Submits);
    }

    [Fact]
    public async Task InvalidTransitionAndStaleVersionAreRejected()
    {
        var store = Store();
        await using var owner = (await store.TryAcquireAsync("task"))!;
        var now = DateTimeOffset.UtcNow;
        var record = new DurableExecution("task", DurableExecution.Fingerprint(Request()), new FakeClient(new()).Scope,
            DurableExecution.NormalizeRoot(directory), "workflow", new FakeClient(new()).PrepareExecution(),
            DurableExecutionState.Created, SubmissionPhase.None, now, now);
        await store.CreateAsync(record, owner);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(record,
            record with { State = DurableExecutionState.Completed, Version = 1, CompletedAt = now }, owner));
        var next = record with { State = DurableExecutionState.SessionCreating, SubmissionPhase = SubmissionPhase.SessionCreationIntended, Version = 1 };
        await store.UpdateAsync(record, next, owner);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(record, next, owner));
        Assert.Equal(next, await store.GetAsync("task"));
    }

    [Fact]
    public async Task ScopeMismatchIsRejectedWithoutProviderRequests()
    {
        var remote = new FakeRemote();
        await Service(Store(), remote).RunAsync(Request());
        var other = new DurableExecutionService(HistoricalRuntimeAccess.Instance, Store(), new FakeClient(remote, DurableExecution.Hash("other endpoint")));
        await Assert.ThrowsAsync<ArgumentException>(() => other.RecoverAsync("task", directory, TimeSpan.FromSeconds(1)));
        Assert.Equal(0, remote.Reattaches);
    }

    [Fact]
    public async Task PersistenceContainsNeitherPromptNorProviderOutputNorExceptionText()
    {
        const string secret = "synthetic-sensitive-value-never-persist";
        var remote = new FakeRemote { Output = secret };
        await Service(Store(), remote).RunAsync(Request() with { TaskText = secret });
        var bytes = File.ReadAllBytes(Database);
        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("synthetic-credential", Encoding.UTF8.GetString(bytes));
        remote.Unavailable = true;
        var recovery = await Service(Store(), remote).RecoverAsync("task", directory, TimeSpan.FromSeconds(1));
        Assert.DoesNotContain("synthetic-credential", recovery.Result.ToJson());
        Assert.Equal(DurableExecutionState.Completed, (await Store().GetAsync("task"))!.State);
    }

    [Fact]
    public async Task FailedDiscoveryNeverCreatesOrSubmits()
    {
        var remote = new FakeRemote { Unavailable = true };
        var result = await Service(Store(), remote).RunAsync(Request());
        Assert.Equal(ExternalExecutionStatus.Failed, result.Result.Status);
        Assert.Equal(0, remote.Creates);
        Assert.Equal(0, remote.Submits);
    }

    [Fact]
    public async Task ExplicitCancellationRequiresConfirmedRemoteCancellation()
    {
        using var stop = new CancellationTokenSource();
        var remote = new FakeRemote { Status = ExternalExecutionStatus.Running, OnObservation = stop.Cancel };
        var result = await Service(Store(), remote).RunAsync(Request(), stop.Token);
        Assert.Equal(ExternalExecutionStatus.Cancelled, result.Result.Status);
        Assert.True(result.Result.AbortRequested);
        Assert.Equal(1, remote.Aborts);
        Assert.Equal(DurableExecutionState.Cancelled, (await Store().GetAsync("task"))!.State);
    }

    [Fact]
    public async Task WaitingCanRecoverToCompletionWithoutApprovalOrNewPrompt()
    {
        var remote = new FakeRemote { Status = ExternalExecutionStatus.WaitingForApproval };
        await Service(Store(), remote).RunAsync(Request());
        Assert.Single(await Store().ListRecoverableAsync());
        remote.Status = ExternalExecutionStatus.Completed; // Operator/provider evidence changes, MAF does not approve.
        var final = await Service(Store(), remote).RecoverAsync("task", directory, TimeSpan.FromSeconds(1));
        Assert.Equal(ExternalExecutionStatus.Completed, final.Result.Status);
        Assert.Equal(1, remote.Submits);
        Assert.Empty(await Store().ListRecoverableAsync());
    }

    [Fact]
    public async Task FutureSchemaIsRejectedWithoutMigration()
    {
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=2;";
            await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store().GetAsync("task"));
    }

    public void Dispose()
    {
        // Only known files owned by this fixture; no recursive deletion or unrelated Temp cleanup.
        foreach (var suffix in new[] { "", "-journal", "-wal", "-shm" })
            if (File.Exists(Database + suffix)) File.Delete(Database + suffix);
        var locks = Database + ".locks";
        if (Directory.Exists(locks))
        {
            foreach (var file in Directory.EnumerateFiles(locks, "*.lock")) File.Delete(file);
            Directory.Delete(locks);
        }
        Directory.Delete(directory);
    }

    private sealed class SimulatedCrash : Exception { }
    private sealed class FakeRemote
    {
        public int Creates, Submits, Reattaches, Aborts;
        public bool LoseCreateResponse, LoseSubmitResponse, CrashAfterSubmit, Unavailable;
        public ExternalExecutionStatus Status = ExternalExecutionStatus.Completed;
        public string Output = "Synthetic final answer";
        public Action? OnObservation;
        public TaskCompletionSource? ObservationGate;
        public ExternalExecutionHandle? Handle;
    }
    private sealed class FakeClient(FakeRemote remote, string? scope = null) : IResumableExternalHarnessClient
    {
        public string Provider => "Fake";
        public string Scope => scope ?? DurableExecution.Hash("fake readonly endpoint v1");
        public int GetRequestCount(string taskId) => 0;
        public ExternalExecutionHandle PrepareExecution() => new(Guid.NewGuid().ToString("N"), Provider, ExternalRequestId: "msg_" + Guid.NewGuid().ToString("N"));
        public Task ValidateAsync(ExternalTaskRequest request, CancellationToken cancellationToken)
        {
            if (remote.Unavailable) throw new ExternalHarnessException("Unavailable");
            return Task.CompletedTask;
        }
        public Task<ExternalExecutionHandle> CreateAsync(ExternalTaskRequest request, string workflowRunId,
            ExternalExecutionHandle handle, CancellationToken cancellationToken)
        {
            remote.Creates++;
            remote.Handle = handle with { ExternalSessionId = "ses_fake" };
            if (remote.LoseCreateResponse) throw new ExternalHarnessException("ResponseLost", true);
            return Task.FromResult(remote.Handle!);
        }
        public Task SubmitAsync(ExternalTaskRequest request, ExternalExecutionHandle handle, CancellationToken cancellationToken)
        {
            remote.Submits++;
            Assert.Equal(remote.Handle, handle);
            if (remote.CrashAfterSubmit) throw new SimulatedCrash();
            if (remote.LoseSubmitResponse) throw new ExternalHarnessException("ResponseLost", true);
            return Task.CompletedTask;
        }
        public Task ReattachAsync(string taskId, string projectRoot, ExternalExecutionHandle handle, CancellationToken cancellationToken)
        {
            if (remote.Unavailable) throw new ExternalHarnessException("synthetic-credential", true);
            Assert.Equal(remote.Handle, handle);
            remote.Reattaches++;
            return Task.CompletedTask;
        }
        public async Task<ExternalObservation> ObserveAsync(ExternalExecutionHandle handle, CancellationToken cancellationToken)
        {
            if (remote.Unavailable) throw new ExternalHarnessException("Unavailable", true);
            remote.OnObservation?.Invoke();
            if (remote.ObservationGate is not null) await remote.ObservationGate.Task.WaitAsync(cancellationToken);
            return new ExternalObservation(remote.Submits == 0 ? ExternalExecutionStatus.Running : remote.Status, remote.Output);
        }
        public Task<bool> AbortAsync(ExternalExecutionHandle handle, CancellationToken cancellationToken)
        { remote.Aborts++; remote.Status = ExternalExecutionStatus.Cancelled; return Task.FromResult(true); }
    }

    private sealed class FaultStore(IExecutionStore inner, Func<DurableExecution, bool, bool> crash) : IExecutionStore
    {
        public ValueTask<IExecutionOwnership?> TryAcquireAsync(string taskId, CancellationToken cancellationToken = default)
            => inner.TryAcquireAsync(taskId, cancellationToken);
        public Task<DurableExecution?> GetAsync(string taskId, CancellationToken cancellationToken = default) => inner.GetAsync(taskId, cancellationToken);
        public Task<IReadOnlyList<DurableExecution>> ListRecoverableAsync(CancellationToken cancellationToken = default) => inner.ListRecoverableAsync(cancellationToken);
        public async Task CreateAsync(DurableExecution execution, IExecutionOwnership ownership, CancellationToken cancellationToken = default)
        {
            if (crash(execution, false)) throw new SimulatedCrash();
            await inner.CreateAsync(execution, ownership, cancellationToken);
            if (crash(execution, true)) throw new SimulatedCrash();
        }
        public async Task UpdateAsync(DurableExecution expected, DurableExecution next, IExecutionOwnership ownership, CancellationToken cancellationToken = default)
        {
            if (crash(next, false)) throw new SimulatedCrash();
            await inner.UpdateAsync(expected, next, ownership, cancellationToken);
            if (crash(next, true)) throw new SimulatedCrash();
        }
    }
}
