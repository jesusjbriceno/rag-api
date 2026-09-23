using Rag.HistoricalLoader.Core.Extraction;
using Rag.HistoricalLoader.Core.Lifecycle;
using Rag.HistoricalLoader.Core.Persistence;

namespace Rag.HistoricalLoader.UnitTests.Lifecycle;

public sealed class DocumentLifecycleStateMachineTests
{
    // --- Forward chain ------------------------------------------------------

    [Fact]
    public void Advance_FollowsDocumentedForwardChain()
    {
        var expected = new[]
        {
            DocumentState.Snapshotting,
            DocumentState.Extracting,
            DocumentState.Staged,
            DocumentState.Reserving,
            DocumentState.Uploading,
            DocumentState.Committing,
            DocumentState.RemotePending,
            DocumentState.Loaded,
        };

        var current = DocumentState.Pending;
        foreach (var next in expected)
        {
            Assert.True(DocumentLifecycle.CanAdvance(current), $"{current} should be advanceable");
            current = DocumentLifecycle.Advance(current);
            Assert.Equal(next, current);
        }

        Assert.Equal(DocumentState.Loaded, current);
        Assert.False(DocumentLifecycle.CanAdvance(DocumentState.Loaded));
    }

    [Fact]
    public void Advance_ThrowsForNonAdvanceableStates()
    {
        foreach (var state in new[]
        {
            DocumentState.Loaded,
            DocumentState.SkippedDocumentError,
            DocumentState.RetryWait,
            DocumentState.RetryExhaustedNetwork,
            DocumentState.Interrupted,
            DocumentState.BlockedAuth,
            DocumentState.BlockedOperatorAction,
        })
        {
            Assert.False(DocumentLifecycle.CanAdvance(state), $"{state} should not be advanceable");
            Assert.Throws<InvalidOperationException>(() => DocumentLifecycle.Advance(state));
        }
    }

    // --- Document-classified skip -------------------------------------------

    [Fact]
    public void DocumentFailure_SkipsOnlyFromSnapshottingOrExtracting()
    {
        Assert.True(DocumentLifecycle.CanSkip(DocumentState.Snapshotting));
        Assert.True(DocumentLifecycle.CanSkip(DocumentState.Extracting));

        Assert.False(DocumentLifecycle.CanSkip(DocumentState.Pending));
        Assert.False(DocumentLifecycle.CanSkip(DocumentState.Staged));
        Assert.False(DocumentLifecycle.CanSkip(DocumentState.Reserving));
        Assert.False(DocumentLifecycle.CanSkip(DocumentState.Loaded));

        Assert.Equal(DocumentState.SkippedDocumentError, DocumentLifecycle.OnDocumentFailure());
    }

    // --- Network retry ------------------------------------------------------

    [Fact]
    public void NetworkFailure_RetryableStages_EnterRetryWaitUntilCeiling()
    {
        var retryable = new[]
        {
            DocumentState.Reserving,
            DocumentState.Uploading,
            DocumentState.Committing,
            DocumentState.RemotePending,
        };

        foreach (var stage in retryable)
        {
            Assert.True(DocumentLifecycle.IsRetryable(stage), $"{stage} should be retryable");
            Assert.Equal(
                DocumentState.RetryWait,
                DocumentLifecycle.OnNetworkFailure(stage, AttemptCounter.From(2)));
            Assert.Equal(
                DocumentState.RetryExhaustedNetwork,
                DocumentLifecycle.OnNetworkFailure(stage, AttemptCounter.From(3)));
        }
    }

    [Fact]
    public void NetworkFailure_OnNonRetryableStage_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            DocumentLifecycle.OnNetworkFailure(DocumentState.Staged, AttemptCounter.Fresh));
    }

    [Fact]
    public void ResumeFromRetry_ReturnsTheSameRetryableStage()
    {
        foreach (var stage in new[]
        {
            DocumentState.Reserving,
            DocumentState.Uploading,
            DocumentState.Committing,
            DocumentState.RemotePending,
        })
        {
            Assert.Equal(stage, DocumentLifecycle.ResumeFromRetry(stage));
        }
    }

    // --- Blocked states -----------------------------------------------------

    [Fact]
    public void Blocked_AuthAndOperatorAction()
    {
        Assert.Equal(DocumentState.BlockedAuth, DocumentLifecycle.BlockedAuth());
        Assert.Equal(DocumentState.BlockedOperatorAction, DocumentLifecycle.BlockedOperatorAction());
    }

    // --- Terminal states ----------------------------------------------------

    [Fact]
    public void TerminalStates_AreLoadedSkippedAndRetryExhausted()
    {
        var terminal = new[]
        {
            DocumentState.Loaded,
            DocumentState.SkippedDocumentError,
            DocumentState.RetryExhaustedNetwork,
        };

        foreach (var state in terminal)
        {
            Assert.True(DocumentLifecycle.IsTerminal(state), $"{state} should be terminal");
        }

        foreach (var state in DocumentLifecycle.TerminalStates)
        {
            Assert.Contains(state, terminal);
        }

        foreach (var state in new[]
        {
            DocumentState.Pending,
            DocumentState.Snapshotting,
            DocumentState.Extracting,
            DocumentState.Staged,
            DocumentState.Reserving,
            DocumentState.Uploading,
            DocumentState.Committing,
            DocumentState.RemotePending,
            DocumentState.RetryWait,
            DocumentState.Interrupted,
            DocumentState.BlockedAuth,
            DocumentState.BlockedOperatorAction,
        })
        {
            Assert.False(DocumentLifecycle.IsTerminal(state), $"{state} should not be terminal");
        }
    }

    // --- Recovery -----------------------------------------------------------

    [Fact]
    public void Recover_ReturnsLastDurableSafeStage()
    {
        Assert.Equal(DocumentState.Pending, DocumentLifecycle.Recover(DocumentState.Pending));
        Assert.Equal(DocumentState.Pending, DocumentLifecycle.Recover(DocumentState.Snapshotting));
        Assert.Equal(DocumentState.Pending, DocumentLifecycle.Recover(DocumentState.Extracting));
        Assert.Equal(DocumentState.Pending, DocumentLifecycle.Recover(DocumentState.Reserving));
        Assert.Equal(DocumentState.Pending, DocumentLifecycle.Recover(DocumentState.Uploading));
        Assert.Equal(DocumentState.Pending, DocumentLifecycle.Recover(DocumentState.Committing));
        Assert.Equal(DocumentState.Pending, DocumentLifecycle.Recover(DocumentState.RetryWait));
        Assert.Equal(DocumentState.Pending, DocumentLifecycle.Recover(DocumentState.Interrupted));

        Assert.Equal(DocumentState.Staged, DocumentLifecycle.Recover(DocumentState.Staged));
        Assert.Equal(DocumentState.RemotePending, DocumentLifecycle.Recover(DocumentState.RemotePending));

        Assert.Equal(DocumentState.Loaded, DocumentLifecycle.Recover(DocumentState.Loaded));
        Assert.Equal(DocumentState.SkippedDocumentError, DocumentLifecycle.Recover(DocumentState.SkippedDocumentError));
        Assert.Equal(DocumentState.RetryExhaustedNetwork, DocumentLifecycle.Recover(DocumentState.RetryExhaustedNetwork));
        Assert.Equal(DocumentState.BlockedAuth, DocumentLifecycle.Recover(DocumentState.BlockedAuth));
        Assert.Equal(DocumentState.BlockedOperatorAction, DocumentLifecycle.Recover(DocumentState.BlockedOperatorAction));
    }

    [Fact]
    public void MarkInterrupted_PreservesTerminalAndBlockedStates()
    {
        Assert.Equal(DocumentState.Interrupted, DocumentLifecycle.MarkInterrupted(DocumentState.Snapshotting));
        Assert.Equal(DocumentState.Interrupted, DocumentLifecycle.MarkInterrupted(DocumentState.Extracting));
        Assert.Equal(DocumentState.Loaded, DocumentLifecycle.MarkInterrupted(DocumentState.Loaded));
        Assert.Equal(DocumentState.SkippedDocumentError, DocumentLifecycle.MarkInterrupted(DocumentState.SkippedDocumentError));
        Assert.Equal(DocumentState.RetryExhaustedNetwork, DocumentLifecycle.MarkInterrupted(DocumentState.RetryExhaustedNetwork));
        Assert.Equal(DocumentState.BlockedAuth, DocumentLifecycle.MarkInterrupted(DocumentState.BlockedAuth));
        Assert.Equal(DocumentState.BlockedOperatorAction, DocumentLifecycle.MarkInterrupted(DocumentState.BlockedOperatorAction));
    }

    [Fact]
    public void DurableReceipt_MatchesDurableSafeBoundaries()
    {
        foreach (var state in DocumentLifecycle.DurableSafeStages)
        {
            Assert.True(DocumentLifecycle.HasDurableReceipt(state), $"{state} should have a durable receipt");
        }

        foreach (var state in new[]
        {
            DocumentState.Snapshotting,
            DocumentState.Extracting,
            DocumentState.Reserving,
            DocumentState.Uploading,
            DocumentState.Committing,
            DocumentState.RetryWait,
            DocumentState.Interrupted,
        })
        {
            Assert.False(DocumentLifecycle.HasDurableReceipt(state), $"{state} should lack a durable receipt");
        }
    }

    // --- Store-backed atomicity / projection / immutability -----------------

    [Fact]
    public async Task Transition_CommitsStateAndAuditAtomically()
    {
        var directory = NewDirectory();
        try
        {
            await using var store = new SqliteStore(DatabasePath(directory));
            await store.InitializeAsync();
            var runStore = new SqliteRunStore(store);

            var run = NewRun();
            await runStore.CreateRunAsync(run);

            var document = NewDocument(run.Id, DocumentState.Pending);
            await runStore.AddRunDocumentAsync(document);

            var updated = document with { State = DocumentState.Snapshotting, UpdatedAt = DateTimeOffset.UtcNow };
            var saved = await runStore.SaveAsync(updated, action: "advance", outcomeCode: null, attemptNumber: 0, measurements: null);

            Assert.Equal(DocumentState.Snapshotting, saved.State);

            var persisted = await runStore.GetRunDocumentAsync(document.Id);
            Assert.NotNull(persisted);
            Assert.Equal(DocumentState.Snapshotting, persisted.State);

            var audit = await runStore.GetAuditEventsAsync(run.Id);
            var transition = Assert.Single(audit, e => e.Action == "advance");
            Assert.Equal("pending->snapshotting", transition.StateTransition);
            Assert.Equal(run.Id, transition.RunId);
            Assert.Equal(document.Id, transition.CandidateId);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task UiCounters_AreProjectionsFromDurableRows()
    {
        var directory = NewDirectory();
        try
        {
            await using var store = new SqliteStore(DatabasePath(directory));
            await store.InitializeAsync();
            var runStore = new SqliteRunStore(store);

            var run = NewRun();
            await runStore.CreateRunAsync(run);

            await runStore.AddRunDocumentAsync(NewDocument(run.Id, DocumentState.Pending));
            await runStore.AddRunDocumentAsync(NewDocument(run.Id, DocumentState.Pending));
            await runStore.AddRunDocumentAsync(NewDocument(run.Id, DocumentState.Loaded));
            await runStore.AddRunDocumentAsync(NewDocument(run.Id, DocumentState.Staged));

            var counts = await runStore.CountByStateAsync(run.Id);

            Assert.Equal(2, counts.GetValueOrDefault(DocumentState.Pending));
            Assert.Equal(1, counts.GetValueOrDefault(DocumentState.Loaded));
            Assert.Equal(1, counts.GetValueOrDefault(DocumentState.Staged));
            Assert.Equal(0, counts.GetValueOrDefault(DocumentState.Snapshotting));
            Assert.Equal(0, counts.GetValueOrDefault(DocumentState.SkippedDocumentError));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task TerminalState_IsImmutableAcrossReopen()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            var runId = Guid.NewGuid();
            var documentId = Guid.NewGuid();

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                var runStore = new SqliteRunStore(store);

                var run = NewRun(runId);
                await runStore.CreateRunAsync(run);
                var document = NewDocument(runId, DocumentState.Pending, documentId);
                await runStore.AddRunDocumentAsync(document);
                await runStore.SaveAsync(
                    document with { State = DocumentState.Loaded, UpdatedAt = DateTimeOffset.UtcNow },
                    action: "loaded",
                    outcomeCode: null,
                    attemptNumber: 0,
                    measurements: null);
            }

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                var runStore = new SqliteRunStore(store);

                var persisted = await runStore.GetRunDocumentAsync(documentId);
                Assert.NotNull(persisted);
                Assert.Equal(DocumentState.Loaded, persisted.State);
                Assert.False(DocumentLifecycle.CanAdvance(persisted.State));
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task Restart_DoesNotResetDispatchedAttempts()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            var runId = Guid.NewGuid();
            var documentId = Guid.NewGuid();

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                var runStore = new SqliteRunStore(store);

                var run = NewRun(runId);
                await runStore.CreateRunAsync(run);
                var document = NewDocument(runId, DocumentState.RetryWait, documentId);
                await runStore.AddRunDocumentAsync(document);
                await runStore.SaveAsync(
                    document with { ReserveAttempts = 2, UpdatedAt = DateTimeOffset.UtcNow },
                    action: "dispatch",
                    outcomeCode: null,
                    attemptNumber: 2,
                    measurements: null);
            }

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                var runStore = new SqliteRunStore(store);

                var persisted = await runStore.GetRunDocumentAsync(documentId);
                Assert.NotNull(persisted);
                Assert.Equal(2, persisted.ReserveAttempts);
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // --- Poll attempt durability -------------------------------------------

    [Fact]
    public async Task PollAttempt_IsPersistedBeforeDispatch_AndRolledBackWhenStillProcessing()
    {
        var directory = NewDirectory();
        try
        {
            await using var store = new SqliteStore(DatabasePath(directory));
            await store.InitializeAsync();
            var runStore = new SqliteRunStore(store);

            var run = NewRun();
            await runStore.CreateRunAsync(run);
            var document = NewDocument(run.Id, DocumentState.RemotePending);
            await runStore.AddRunDocumentAsync(document);

            var observedAtDispatch = -1;
            var api = new ObservingPollApi(runStore, document.Id, persistedPollAttempts =>
            {
                observedAtDispatch = persistedPollAttempts;
                return Task.FromResult(new ApiOperationResult(
                    ApiOutcome.Success,
                    RemoteId: "op-1",
                    Pending: true));
            });
            var engine = new DocumentLifecycleEngine(new FakeTextExtractor(), api, runStore);

            var result = await engine.ProcessDocumentAsync(document, CancellationToken.None);

            // The reservation must be persisted BEFORE the HTTP poll is dispatched.
            Assert.Equal(1, observedAtDispatch);
            // A successful still-processing poll is not a failed retry: the reservation is rolled back.
            Assert.Equal(DocumentState.RemotePending, result.State);
            Assert.Equal(0, result.PollAttempts);

            var persisted = await runStore.GetRunDocumentAsync(document.Id);
            Assert.Equal(0, persisted!.PollAttempts);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task PollAttempt_NoFourthDispatch_AfterThreeReservedKills()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            var runId = Guid.NewGuid();
            var documentId = Guid.NewGuid();

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                var runStore = new SqliteRunStore(store);
                await runStore.CreateRunAsync(NewRun(runId));
                await runStore.AddRunDocumentAsync(NewDocument(runId, DocumentState.RemotePending, documentId));
            }

            // Three dispatches each persist the reservation, then the process is "killed" before the result.
            for (var expected = 1; expected <= 3; expected++)
            {
                await using var store = new SqliteStore(path);
                await store.InitializeAsync();
                var runStore = new SqliteRunStore(store);
                var api = new KillPollApi();
                var engine = new DocumentLifecycleEngine(new FakeTextExtractor(), api, runStore);
                var document = (await runStore.GetRunDocumentAsync(documentId))!;

                await Assert.ThrowsAsync<KillException>(() =>
                    engine.ProcessDocumentAsync(document, CancellationToken.None));

                var persisted = await runStore.GetRunDocumentAsync(documentId);
                Assert.Equal(expected, persisted!.PollAttempts);
            }

            // A fourth dispatch is refused before the HTTP client is ever invoked.
            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                var runStore = new SqliteRunStore(store);
                var api = new KillPollApi();
                var engine = new DocumentLifecycleEngine(new FakeTextExtractor(), api, runStore);
                var document = (await runStore.GetRunDocumentAsync(documentId))!;

                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    engine.ProcessDocumentAsync(document, CancellationToken.None));

                Assert.Equal(0, api.PollCalls);
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    // --- Stage identity and stage idempotency keys -------------------------

    [Fact]
    public async Task StageOperations_KeepDistinctIdempotencyKeys_AndOneStableDocumentIdentity()
    {
        var directory = NewDirectory();
        try
        {
            await using var store = new SqliteStore(DatabasePath(directory));
            await store.InitializeAsync();
            var runStore = new SqliteRunStore(store);

            var run = NewRun();
            await runStore.CreateRunAsync(run);
            var document = NewDocument(run.Id, DocumentState.Pending);
            await runStore.AddRunDocumentAsync(document);

            var api = new RecordingApiClient();
            var engine = new DocumentLifecycleEngine(new FakeTextExtractor(), api, runStore);

            var result = await engine.ProcessDocumentAsync(document, CancellationToken.None);

            Assert.Equal(DocumentState.Loaded, result.State);
            Assert.Equal(
                new[] { "reserve", "upload", "commit", "poll" },
                api.Operations.Select(static recorded => recorded.Kind));

            // Each stage keys its own mutation, so a replayed stage operation stays distinguishable and
            // per-mutation idempotency is preserved.
            Assert.Equal(
                4,
                api.Operations.Select(static recorded => recorded.Operation.IdempotencyKey).Distinct(StringComparer.Ordinal).Count());

            // The same document sits behind all four operations: the source document key and the normalized
            // content hash are stable across stages, which is the identity a remote client must use to
            // correlate the ids it was given earlier. A client that correlated by the stage-scoped
            // idempotency key instead could not resolve its own upload or operation id.
            Assert.Single(api.Operations.Select(static recorded => recorded.Operation.SourceDocumentKey).Distinct(StringComparer.Ordinal));
            Assert.Single(api.Operations.Select(static recorded => recorded.Operation.ContentSha256).Distinct(StringComparer.Ordinal));
            Assert.All(api.Operations, static recorded => Assert.False(string.IsNullOrEmpty(recorded.Operation.ContentSha256)));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private sealed class RecordingApiClient : IHistoricalApiClient
    {
        private readonly object _gate = new();
        private readonly List<(string Kind, ApiOperation Operation)> _operations = new();

        public IReadOnlyList<(string Kind, ApiOperation Operation)> Operations
        {
            get
            {
                lock (_gate)
                {
                    return _operations.ToArray();
                }
            }
        }

        public Task<ApiOperationResult> ReserveAsync(ApiOperation operation, CancellationToken cancellationToken = default)
            => Record("reserve", operation);

        public Task<ApiOperationResult> UploadAsync(ApiOperation operation, CancellationToken cancellationToken = default)
            => Record("upload", operation);

        public Task<ApiOperationResult> CommitAsync(ApiOperation operation, CancellationToken cancellationToken = default)
            => Record("commit", operation);

        public Task<ApiOperationResult> PollAsync(ApiOperation operation, CancellationToken cancellationToken = default)
            => Record("poll", operation);

        private Task<ApiOperationResult> Record(string kind, ApiOperation operation)
        {
            lock (_gate)
            {
                _operations.Add((kind, operation));
            }

            return Task.FromResult(new ApiOperationResult(ApiOutcome.Success, RemoteId: "remote-1"));
        }
    }

    private sealed class ObservingPollApi : IHistoricalApiClient
    {
        private readonly SqliteRunStore _store;
        private readonly Guid _documentId;
        private readonly Func<int, Task<ApiOperationResult>> _poll;

        public ObservingPollApi(SqliteRunStore store, Guid documentId, Func<int, Task<ApiOperationResult>> poll)
        {
            _store = store;
            _documentId = documentId;
            _poll = poll;
        }

        public async Task<ApiOperationResult> PollAsync(ApiOperation operation, CancellationToken cancellationToken = default)
        {
            var persisted = await _store.GetRunDocumentAsync(_documentId, cancellationToken);
            return await _poll(persisted!.PollAttempts);
        }

        public Task<ApiOperationResult> ReserveAsync(ApiOperation operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new ApiOperationResult(ApiOutcome.Success, RemoteId: "reserve-1"));

        public Task<ApiOperationResult> UploadAsync(ApiOperation operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new ApiOperationResult(ApiOutcome.Success, RemoteId: "upload-1"));

        public Task<ApiOperationResult> CommitAsync(ApiOperation operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new ApiOperationResult(ApiOutcome.Success, RemoteId: "commit-1"));
    }

    private sealed class KillPollApi : IHistoricalApiClient
    {
        public int PollCalls { get; private set; }

        public Task<ApiOperationResult> PollAsync(ApiOperation operation, CancellationToken cancellationToken = default)
        {
            PollCalls++;
            throw new KillException();
        }

        public Task<ApiOperationResult> ReserveAsync(ApiOperation operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new ApiOperationResult(ApiOutcome.Success, RemoteId: "reserve-1"));

        public Task<ApiOperationResult> UploadAsync(ApiOperation operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new ApiOperationResult(ApiOutcome.Success, RemoteId: "upload-1"));

        public Task<ApiOperationResult> CommitAsync(ApiOperation operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new ApiOperationResult(ApiOutcome.Success, RemoteId: "commit-1"));
    }

    private sealed class KillException : Exception
    {
    }

    // --- Helpers ------------------------------------------------------------

    private static Run NewRun(Guid? id = null)
        => new(
            id ?? Guid.NewGuid(),
            SampleId: Guid.NewGuid(),
            CollectionId: "legacy",
            DesiredState: RunDesiredState.Running,
            ObservedState: RunObservedState.Running,
            EngineVersion: "0.1.0",
            ConfigurationSnapshot: "{}",
            StartedAt: DateTimeOffset.UtcNow);

    private static RunDocument NewDocument(Guid runId, DocumentState state, Guid? id = null)
        => new(
            id ?? Guid.NewGuid(),
            runId,
            CandidateId: Guid.NewGuid(),
            SourceDocumentKey: $"source-{Guid.NewGuid():N}",
            State: state,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rag-lifecycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string DatabasePath(string directory) => Path.Combine(directory, "store.sqlite");

    private static void DeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; the OS temp directory will reclaim leftovers.
        }
    }
}
