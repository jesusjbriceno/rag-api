using Rag.HistoricalLoader.Core.Extraction;
using Rag.HistoricalLoader.Core.Lifecycle;
using Rag.HistoricalLoader.Core.Persistence;

namespace Rag.HistoricalLoader.UnitTests.Retry;

public sealed class AttemptAccountingTests
{
    [Fact]
    public async Task DispatchedOperation_PersistsAttemptBeforeDispatch()
    {
        var harness = new Harness();
        try
        {
            await harness.InitializeAsync();
            var document = await harness.AddPendingDocumentAsync();

            var fake = new FakeHistoricalApiClient();
            var engine = new DocumentLifecycleEngine(new FakeTextExtractor(), fake, harness.RunStore);

            int attemptsObservedAtDispatchTime = -1;
            var (result, documentAfter) = await engine.DispatchAsync(
                document,
                DocumentState.Reserving,
                async _ =>
                {
                    var persisted = await harness.RunStore.GetRunDocumentAsync(document.Id);
                    attemptsObservedAtDispatchTime = persisted!.ReserveAttempts;
                    return new ApiOperationResult(ApiOutcome.Success, RemoteId: "reserve-1");
                },
                CancellationToken.None);

            Assert.Equal(ApiOutcome.Success, result.Outcome);
            Assert.Equal(1, attemptsObservedAtDispatchTime);
            Assert.Equal(1, documentAfter.ReserveAttempts);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [Fact]
    public async Task DispatchedOperation_NeverDispatchesAFourthAttempt()
    {
        var harness = new Harness();
        try
        {
            await harness.InitializeAsync();
            var document = await harness.AddPendingDocumentAsync();

            var fake = new FakeHistoricalApiClient();
            var engine = new DocumentLifecycleEngine(new FakeTextExtractor(), fake, harness.RunStore);

            var failure = new ApiOperationResult(ApiOutcome.TransientFailure, ErrorCode: "timeout");

            var (_, first) = await engine.DispatchAsync(document, DocumentState.Reserving, _ => Task.FromResult(failure), CancellationToken.None);
            var (_, second) = await engine.DispatchAsync(first, DocumentState.Reserving, _ => Task.FromResult(failure), CancellationToken.None);
            var (_, third) = await engine.DispatchAsync(second, DocumentState.Reserving, _ => Task.FromResult(failure), CancellationToken.None);

            Assert.Equal(3, third.ReserveAttempts);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                engine.DispatchAsync(third, DocumentState.Reserving, _ => Task.FromResult(failure), CancellationToken.None));
        }
        finally
        {
            harness.Dispose();
        }
    }

    [Fact]
    public async Task PendingPoll_IsNotAFailedRetry()
    {
        var harness = new Harness();
        try
        {
            await harness.InitializeAsync();
            var document = await harness.AddPendingDocumentAsync();

            var fake = new FakeHistoricalApiClient();
            var engine = new DocumentLifecycleEngine(new FakeTextExtractor(), fake, harness.RunStore);

            var remotePending = await harness.SaveAsync(
                document with
                {
                    State = DocumentState.RemotePending,
                    NormalizedTextHash = "sha256",
                    RemoteOperationId = "op-1",
                    UpdatedAt = DateTimeOffset.UtcNow,
                },
                action: "committed",
                outcomeCode: null,
                attemptNumber: 0,
                measurements: null);

            fake.ScriptPoll(pending: true);

            var afterPoll = await engine.ProcessDocumentAsync(remotePending, CancellationToken.None);

            Assert.Equal(DocumentState.RemotePending, afterPoll.State);
            Assert.Equal(0, afterPoll.PollAttempts);
            Assert.Equal(1, fake.PollCalls);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [Fact]
    public async Task CancellationBeforeDispatch_DoesNotConsumeAnAttempt()
    {
        var harness = new Harness();
        try
        {
            await harness.InitializeAsync();
            var document = await harness.AddPendingDocumentAsync();

            var fake = new FakeHistoricalApiClient();
            var engine = new DocumentLifecycleEngine(new FakeTextExtractor(), fake, harness.RunStore);

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                engine.DispatchAsync(
                    document,
                    DocumentState.Reserving,
                    _ => Task.FromResult(new ApiOperationResult(ApiOutcome.Success, RemoteId: "reserve-1")),
                    new CancellationToken(canceled: true)));

            var persisted = await harness.RunStore.GetRunDocumentAsync(document.Id);
            Assert.NotNull(persisted);
            Assert.Equal(0, persisted.ReserveAttempts);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [Fact]
    public async Task UnknownOutcomeAfterDispatch_ConsumesAttempt_AndReconcilesViaIdempotentResource()
    {
        var harness = new Harness();
        try
        {
            await harness.InitializeAsync();
            var document = await harness.AddPendingDocumentAsync();

            var fake = new FakeHistoricalApiClient();
            var engine = new DocumentLifecycleEngine(new FakeTextExtractor(), fake, harness.RunStore);

            fake.ScriptNext(ApiOutcome.UnknownOutcome);

            var operation = new ApiOperation("idem-key-1", document.SourceDocumentKey, ContentSha256: "sha256");

            var (unknown, afterUnknown) = await engine.DispatchAsync(
                document,
                DocumentState.Reserving,
                ct => fake.ReserveAsync(operation, ct),
                CancellationToken.None);

            Assert.Equal(ApiOutcome.UnknownOutcome, unknown.Outcome);
            Assert.Equal(1, afterUnknown.ReserveAttempts);

            // Reconciliation replays the SAME idempotency key; the fake resolves it to the canonical success.
            var (reconciled, _) = await engine.DispatchAsync(
                afterUnknown,
                DocumentState.Reserving,
                ct => fake.ReserveAsync(operation, ct),
                CancellationToken.None);

            Assert.Equal(ApiOutcome.Success, reconciled.Outcome);
            Assert.Equal("reserve-1", reconciled.RemoteId);
            Assert.Equal(2, fake.OperationCalls);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [Fact]
    public async Task FullPipeline_ScriptedNetworkFailure_ExhaustsAfterThreeAndDoesNotSkipLaterDocuments()
    {
        var harness = new Harness();
        try
        {
            await harness.InitializeAsync();
            var first = await harness.AddPendingDocumentAsync();
            var second = await harness.AddPendingDocumentAsync();

            var fake = new FakeHistoricalApiClient();
            // Three transient reserve failures for the first document only.
            fake.ScriptNext(ApiOutcome.TransientFailure);
            fake.ScriptNext(ApiOutcome.TransientFailure);
            fake.ScriptNext(ApiOutcome.TransientFailure);

            var engine = new DocumentLifecycleEngine(new FakeTextExtractor(), fake, harness.RunStore);

            var firstResult = await engine.ProcessDocumentAsync(first, CancellationToken.None);

            Assert.Equal(DocumentState.RetryExhaustedNetwork, firstResult.State);
            Assert.Equal(3, firstResult.ReserveAttempts);

            // The second document is still processable end-to-end with the default-success fake.
            var secondResult = await engine.ProcessDocumentAsync(second, CancellationToken.None);
            Assert.Equal(DocumentState.Loaded, secondResult.State);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [Fact]
    public async Task FullPipeline_DocumentFailure_SkipsAndLaterDocumentRemainsProcessable()
    {
        var harness = new Harness();
        try
        {
            await harness.InitializeAsync();
            var failing = await harness.AddPendingDocumentAsync();
            var eligible = await harness.AddPendingDocumentAsync();

            // The fake extractor skips legacy .doc; use a custom extractor that skips the first document.
            var engine = new DocumentLifecycleEngine(new SkippingFirstExtractor(), new FakeHistoricalApiClient(), harness.RunStore);

            var firstResult = await engine.ProcessDocumentAsync(failing, CancellationToken.None);
            var secondResult = await engine.ProcessDocumentAsync(eligible, CancellationToken.None);

            Assert.Equal(DocumentState.SkippedDocumentError, firstResult.State);
            Assert.Equal(DocumentState.Loaded, secondResult.State);
        }
        finally
        {
            harness.Dispose();
        }
    }

    private sealed class SkippingFirstExtractor : IExtractor
    {
        private int _calls;

        public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                return Task.FromResult(new ExtractionResult(
                    ExtractionOutcome.SkippedDocument,
                    null,
                    ExtractionErrorCodes.ExtractorUnavailable));
            }

            return Task.FromResult(new ExtractionResult(ExtractionOutcome.Completed, "fake-normalized-text:txt", null));
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rag-attempts-{Guid.NewGuid():N}");

        public SqliteStore Store { get; private set; } = null!;
        public SqliteRunStore RunStore { get; private set; } = null!;
        private Guid _runId;

        public async Task InitializeAsync()
        {
            Directory.CreateDirectory(_directory);
            Store = new SqliteStore(Path.Combine(_directory, "store.sqlite"));
            await Store.InitializeAsync();
            RunStore = new SqliteRunStore(Store);

            _runId = Guid.NewGuid();
            await RunStore.CreateRunAsync(new Run(
                _runId,
                Guid.NewGuid(),
                "legacy",
                RunDesiredState.Running,
                RunObservedState.Running,
                "0.1.0",
                "{}",
                DateTimeOffset.UtcNow));
        }

        public async Task<RunDocument> AddPendingDocumentAsync()
        {
            var document = new RunDocument(
                Guid.NewGuid(),
                _runId,
                Guid.NewGuid(),
                $"source-{Guid.NewGuid():N}",
                DocumentState.Pending,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow);
            await RunStore.AddRunDocumentAsync(document);
            return document;
        }

        public Task<RunDocument> SaveAsync(RunDocument document, string action, string? outcomeCode, int attemptNumber, string? measurements)
            => RunStore.SaveAsync(document, action, outcomeCode, attemptNumber, measurements);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}
