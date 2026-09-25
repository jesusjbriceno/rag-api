using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Rag.HistoricalLoader.Core.Extraction;
using Rag.HistoricalLoader.Core.Lifecycle;
using Rag.HistoricalLoader.Core.Persistence;
using Rag.HistoricalLoader.Engine.Api;
using Rag.HistoricalLoader.Engine.Extraction;
using Rag.HistoricalLoader.Engine.Pipeline;
using Rag.HistoricalLoader.Engine.Security;

namespace Rag.HistoricalLoader.UnitTests.Pipeline;

public sealed class HistoricalPipelineTests
{
    // --- Retry ceiling -------------------------------------------------------

    [Fact]
    public async Task TransientSequence_ExhaustsAfterThree_NeverFourth()
    {
        var harness = new Harness();
        try
        {
            await harness.InitializeAsync();
            await harness.AddPendingDocumentAsync();

            var api = new ScriptedApiClient();
            api.Script(
                Transient("timeout"),
                Transient("429"),
                Transient("502"),
                Transient("503"),
                Transient("504"));

            var pipeline = NewPipeline(harness.RunStore, api);
            var result = await pipeline.RunAsync(harness.RunId);

            var document = await harness.RunStore.GetRunDocumentAsync(harness.SingleDocumentId);
            Assert.Equal(DocumentState.RetryExhaustedNetwork, document!.State);
            Assert.Equal(3, document.ReserveAttempts);
            Assert.Equal(3, api.OperationCalls);
            Assert.Equal(1, result.RetryExhaustedCount);
            Assert.Equal(0, result.LoadedCount);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [Fact]
    public async Task ThreeAttemptCeiling_IsDurableAcrossRestart()
    {
        var harness = new Harness();
        try
        {
            await harness.InitializeAsync();
            var exhausted = await harness.AddPendingDocumentAsync();
            var loaded = await harness.AddPendingDocumentAsync();

            var api = new ScriptedApiClient();
            api.Script(Transient("timeout"), Transient("429"), Transient("502"));

            var pipeline = NewPipeline(harness.RunStore, api);
            var first = await pipeline.RunAsync(harness.RunId);
            Assert.Equal(1, first.RetryExhaustedCount);
            Assert.Equal(1, first.LoadedCount);

            // Forced restart: new store, new pipeline, new client with no scripted faults.
            var reopened = await harness.ReopenAsync();
            var apiAfter = new ScriptedApiClient();
            var afterRestart = NewPipeline(reopened, apiAfter);
            var second = await afterRestart.RunAsync(harness.RunId);

            Assert.Equal(0, second.ClaimedCount);
            Assert.Equal(0, apiAfter.OperationCalls);

            var persistedExhausted = await reopened.GetRunDocumentAsync(exhausted.Id);
            var persistedLoaded = await reopened.GetRunDocumentAsync(loaded.Id);
            Assert.Equal(DocumentState.RetryExhaustedNetwork, persistedExhausted!.State);
            Assert.Equal(3, persistedExhausted.ReserveAttempts);
            Assert.Equal(DocumentState.Loaded, persistedLoaded!.State);
        }
        finally
        {
            harness.Dispose();
        }
    }

    // --- Document skip continuity -------------------------------------------

    [Fact]
    public async Task DocumentFailure_SkipsAndContinuesLaterDocuments()
    {
        var harness = new Harness();
        try
        {
            await harness.InitializeAsync();
            await harness.AddPendingDocumentAsync();
            await harness.AddPendingDocumentAsync();

            var pipeline = new HistoricalPipeline(
                new SkippingFirstExtractor(),
                new ScriptedApiClient(),
                harness.RunStore,
                DefaultOptions());

            var result = await pipeline.RunAsync(harness.RunId);

            Assert.Equal(1, result.SkippedCount);
            Assert.Equal(1, result.LoadedCount);
            Assert.Equal(2, result.ClaimedCount);
        }
        finally
        {
            harness.Dispose();
        }
    }

    // --- Bounded staged watermarks ------------------------------------------

    [Fact]
    public async Task StagedWatermark_StopsExtraction_AndCommitReleases()
    {
        var harness = new Harness();
        try
        {
            await harness.InitializeAsync();
            await harness.AddPendingDocumentAsync();
            await harness.AddPendingDocumentAsync();

            // A pre-filled scheduler already at the byte watermark stops claiming before the run starts.
            var watermarks = new WatermarkScheduler(stagedByteWatermark: 10, stagedCountWatermark: 100);
            watermarks.RecordStaged("blocked", 11);

            var pipeline = NewPipeline(harness.RunStore, new ScriptedApiClient(), watermarks);
            var stopped = await pipeline.RunAsync(harness.RunId);

            Assert.Equal(0, stopped.ClaimedCount);
            Assert.Equal(0, stopped.LoadedCount);

            // Releasing the blocked staged data lets the pipeline continue and load both documents.
            Assert.True(pipeline.Watermarks.ReleaseCommitted("blocked"));

            var completed = await pipeline.RunAsync(harness.RunId);
            Assert.Equal(2, completed.LoadedCount);
            Assert.True(pipeline.Watermarks.CommittedCount >= 2);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [Fact]
    public async Task CommittedWatermark_AdvancesOnlyAfterCommit_NotOnRetryExhausted()
    {
        var harness = new Harness();
        try
        {
            await harness.InitializeAsync();
            await harness.AddPendingDocumentAsync(); // staged, then exhausts at reserve
            await harness.AddPendingDocumentAsync(); // loads

            var api = new ScriptedApiClient();
            api.Script(Transient("timeout"), Transient("429"), Transient("502"));

            var pipeline = NewPipeline(harness.RunStore, api);
            var result = await pipeline.RunAsync(harness.RunId);

            Assert.Equal(1, result.RetryExhaustedCount);
            Assert.Equal(1, result.LoadedCount);
            // The committed watermark advances only for the document that reached Loaded (post-commit),
            // never for the retry-exhausted document that was staged but never committed.
            Assert.Equal(1, result.CommittedCount);
            Assert.True(result.CommittedBytes > 0);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [Fact]
    public async Task Restart_RehydratesCommittedWatermark_FromDurableRows()
    {
        var harness = new Harness();
        try
        {
            await harness.InitializeAsync();
            await harness.AddPendingDocumentAsync();

            var pipeline = NewPipeline(harness.RunStore, new ScriptedApiClient());
            var first = await pipeline.RunAsync(harness.RunId);
            Assert.Equal(1, first.LoadedCount);
            Assert.Equal(1, first.CommittedCount);

            // Forced restart: fresh store + fresh pipeline + fresh (not-yet-rehydrated) scheduler.
            var reopened = await harness.ReopenAsync();
            var afterRestart = NewPipeline(reopened, new ScriptedApiClient());
            var second = await afterRestart.RunAsync(harness.RunId);

            // Nothing new is claimed, but the committed watermark is recovered from durable rows.
            Assert.Equal(0, second.ClaimedCount);
            Assert.Equal(1, afterRestart.Watermarks.CommittedCount);
            Assert.True(afterRestart.Watermarks.CommittedBytes > 0);
            Assert.Equal(0, afterRestart.Watermarks.StagedCount);
        }
        finally
        {
            harness.Dispose();
        }
    }

    // --- Durable staged-capacity rehydration across restart ------------------

    [Fact]
    public async Task Restart_RehydratesStagedWatermark_AndStillBoundsClaiming()
    {
        var harness = new Harness();
        try
        {
            await harness.InitializeAsync();
            var document = await harness.AddPendingDocumentAsync();

            // The document stages locally but the poll never confirms, so its staged bytes stay uncommitted.
            var pipeline = NewPipeline(harness.RunStore, new ScriptedApiClient { PollPending = true });
            var first = await pipeline.RunAsync(harness.RunId);

            Assert.Equal(1, first.ClaimedCount);
            Assert.Equal(0, first.LoadedCount);
            Assert.Equal(1, pipeline.Watermarks.StagedCount);
            Assert.True(pipeline.Watermarks.StagedBytes > 0);
            Assert.Equal(0, pipeline.Watermarks.CommittedCount);

            // Forced restart with a one-byte stage watermark: the recovered staged capacity must stop the
            // restarted pipeline before it claims anything, and the byte count must match exactly.
            var reopened = await harness.ReopenAsync();
            var api = new ScriptedApiClient { PollPending = true };
            var afterRestart = new HistoricalPipeline(
                new FakeTextExtractor(),
                api,
                reopened,
                new PipelineOptions(Concurrency: 1, StagedByteWatermark: 1, StagedCountWatermark: 100));

            var second = await afterRestart.RunAsync(harness.RunId);

            Assert.Equal(0, second.ClaimedCount);
            Assert.Equal(0, api.OperationCalls);
            Assert.Equal(pipeline.Watermarks.StagedBytes, afterRestart.Watermarks.StagedBytes);
            Assert.Equal(1, afterRestart.Watermarks.StagedCount);
            Assert.Equal(0, afterRestart.Watermarks.CommittedCount);

                // Rehydration is read-only: the durable document state is untouched.
                Assert.Equal(DocumentState.RemotePending, (await reopened.GetRunDocumentAsync(document.Id))!.State);
            }
            finally
            {
                harness.Dispose();
            }
        }

    // --- Crash window: staged accounting must survive a kill at any later stage ---

    [Fact]
    public async Task CrashDuringPoll_AfterCommit_SurvivesRestart_WithStagedCapacity()
    {
        var harness = new Harness();
        try
        {
            await harness.InitializeAsync();
            var sourceKey = $"source-{Guid.NewGuid():N}";
            var document = await harness.AddDocumentWithKeyAsync(sourceKey, DocumentState.Pending);
            var stagedBytes = await StagedBytesForAsync(sourceKey);

            // The kill lands after the commit advance (durable remote_pending) and before the process could
            // record any accounting for the finished document: the poll dispatch is persisted, then it dies.
            using var kill = new CancellationTokenSource();
            var pipeline = NewPipeline(harness.RunStore, new KillDuringPollApi(kill));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.RunAsync(harness.RunId, kill.Token));

            var crashed = await harness.RunStore.GetRunDocumentAsync(document.Id);
            Assert.Equal(DocumentState.RemotePending, crashed!.State);
            Assert.Equal(1, crashed.PollAttempts);

            // Forced restart: fresh store + fresh pipeline + fresh (not-yet-rehydrated) scheduler.
            var reopened = await harness.ReopenAsync();
            var afterRestart = NewPipeline(reopened, new ScriptedApiClient());
            var second = await afterRestart.RunAsync(harness.RunId);

            // The committed-but-unconfirmed document keeps its staged capacity: the server never confirmed it.
            Assert.Equal(0, second.ClaimedCount);
            Assert.Equal(1, afterRestart.Watermarks.StagedCount);
            Assert.Equal(stagedBytes, afterRestart.Watermarks.StagedBytes);
            Assert.Equal(0, afterRestart.Watermarks.CommittedCount);
            Assert.Equal(0, afterRestart.Watermarks.CommittedBytes);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [Fact]
    public async Task CrashDuringPoll_ThenRestart_WithTinyStagedWatermark_StopsBeforeClaiming()
    {
        var harness = new Harness();
        try
        {
            await harness.InitializeAsync();
            var sourceKey = $"source-{Guid.NewGuid():N}";
            await harness.AddDocumentWithKeyAsync(sourceKey, DocumentState.Pending);
            var stagedBytes = await StagedBytesForAsync(sourceKey);

            using var kill = new CancellationTokenSource();
            var pipeline = NewPipeline(harness.RunStore, new KillDuringPollApi(kill));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.RunAsync(harness.RunId, kill.Token));

            // A second document is still pending when the process restarts.
            await harness.AddDocumentWithKeyAsync($"source-{Guid.NewGuid():N}", DocumentState.Pending);
            var reopened = await harness.ReopenAsync();
            var api = new ScriptedApiClient();
            var afterRestart = new HistoricalPipeline(
                new FakeTextExtractor(),
                api,
                reopened,
                new PipelineOptions(Concurrency: 1, StagedByteWatermark: 1, StagedCountWatermark: 100));

            var second = await afterRestart.RunAsync(harness.RunId);

            // The rehydrated staged bytes already reach the one-byte watermark, so the restarted pipeline
            // must stop before claiming the pending document.
            Assert.True(stagedBytes > 1, $"Staged bytes must exceed the one-byte watermark (observed {stagedBytes}).");
            Assert.Equal(0, second.ClaimedCount);
            Assert.Equal(0, api.OperationCalls);
            Assert.Equal(stagedBytes, afterRestart.Watermarks.StagedBytes);
        }
        finally
        {
            harness.Dispose();
        }
    }

    // --- Loopback: real extractor + real API client -------------------------

    [Fact]
    public async Task Loopback_RealExtractorAndRealApiClient_LoadsDocument()
    {
        var harness = new Harness();
        var corpus = Path.Combine(Path.GetTempPath(), $"rag-loopback-{Guid.NewGuid():N}");
        try
        {
            await harness.InitializeAsync();

            var companionPath = LocateCompanionAssembly();
            Assert.True(File.Exists(companionPath), $"Rag.Companion assembly not found at '{companionPath}'.");

            Directory.CreateDirectory(corpus);
            var markdownPath = Path.Combine(corpus, "note.md");
            await File.WriteAllTextAsync(markdownPath, "# Loopback\n\nReal markdown extraction.");

            using var extractor = new CompanionReflectionExtractor(companionPath);
            var extraction = await extractor.ExtractAsync(
                new ExtractionRequest(markdownPath, "txt"),
                CancellationToken.None);
            Assert.Equal(ExtractionOutcome.Completed, extraction.Outcome);
            var expectedText = extraction.NormalizedText!;
            var expectedBytes = Encoding.UTF8.GetByteCount(expectedText);
            var expectedHash = ComputeSha256(expectedText);

            var handler = new RecordingHandler(expectedHash, expectedText, expectedBytes);
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://loopback.invalid") };
            var client = new HistoricalApiClient(
                http,
                new HistoricalApiClientOptions(
                    new Uri("http://loopback.invalid"),
                    LoopbackCollectionId,
                    _ => Task.FromResult(new RagServiceCredential("loopback-key", "loopback-rag-secret")),
                    _ => Task.FromResult(new CloudflareServiceToken("loopback-cf-id", "loopback-cf-secret")),
                    ContentResolver: (key, _) => Task.FromResult(new HistoricalContent(expectedText, expectedBytes))));

            var document = await harness.AddDocumentWithKeyAsync(markdownPath);
            var pipeline = new HistoricalPipeline(extractor, client, harness.RunStore, DefaultOptions());

            var result = await pipeline.RunAsync(harness.RunId);

            Assert.Equal(1, result.LoadedCount);
            Assert.Equal(1, result.CommittedCount);
            Assert.True(result.CommittedBytes > 0);

            var persisted = await harness.RunStore.GetRunDocumentAsync(document.Id);
            Assert.Equal(DocumentState.Loaded, persisted!.State);
            Assert.NotNull(persisted.RemoteOperationId);

            // Boundary proof: the real extractor output crossed the real API-client request/response seam.
            Assert.True(handler.RequestCount >= 5, $"Expected at least 5 HTTP requests, got {handler.RequestCount}.");
            Assert.Equal(expectedHash, handler.ReserveSha256);
            Assert.Equal(expectedBytes, handler.ReserveDeclaredBytes);
            Assert.Equal(expectedText, handler.UploadBody);
        }
        finally
        {
            TryDeleteDirectory(corpus);
            harness.Dispose();
        }
    }

    // --- Loopback regression: extracted content crosses the real HTTP boundary -------

    [Fact]
    public async Task Loopback_RealExtractor_ExtractedContentSentInRequest_AndRealResponseDrivesResult()
    {
        var harness = new Harness();
        var corpus = Path.Combine(Path.GetTempPath(), $"rag-loopback-{Guid.NewGuid():N}");
        try
        {
            await harness.InitializeAsync();

            var companionPath = LocateCompanionAssembly();
            Assert.True(File.Exists(companionPath), $"Rag.Companion assembly not found at '{companionPath}'.");

            Directory.CreateDirectory(corpus);
            var markdownPath = Path.Combine(corpus, "note.md");
            await File.WriteAllTextAsync(markdownPath, "# Loopback regression\n\nActual extracted content must cross the wire.");

            using var extractor = new CompanionReflectionExtractor(companionPath);

            // Independent, deterministic export of what the real extractor produces for this source key.
            var expected = await extractor.ExtractAsync(
                new ExtractionRequest(markdownPath, "txt"),
                CancellationToken.None);
            Assert.Equal(ExtractionOutcome.Completed, expected.Outcome);
            var expectedText = expected.NormalizedText!;
            var expectedBytes = Encoding.UTF8.GetByteCount(expectedText);
            var expectedHash = ComputeSha256(expectedText);

            var handler = new RecordingHandler(expectedHash, expectedText, expectedBytes);
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://loopback.invalid") };
            var client = new HistoricalApiClient(
                http,
                new HistoricalApiClientOptions(
                    new Uri("http://loopback.invalid"),
                    LoopbackCollectionId,
                    _ => Task.FromResult(new RagServiceCredential("loopback-key", "loopback-rag-secret")),
                    _ => Task.FromResult(new CloudflareServiceToken("loopback-cf-id", "loopback-cf-secret")),
                    ContentResolver: (key, ct) => ResolveExtractedContent(extractor, key, ct)));

            var document = await harness.AddDocumentWithKeyAsync(markdownPath);
            var pipeline = new HistoricalPipeline(extractor, client, harness.RunStore, DefaultOptions());

            var result = await pipeline.RunAsync(harness.RunId);

            // The real client response drove the pipeline result.
            Assert.Equal(1, result.LoadedCount);
            Assert.Equal(1, result.CommittedCount);
            Assert.True(result.CommittedBytes > 0);
            Assert.True(handler.RequestCount >= 5, $"Expected at least 5 HTTP requests, got {handler.RequestCount}.");

            var persisted = await harness.RunStore.GetRunDocumentAsync(document.Id);
            Assert.Equal(DocumentState.Loaded, persisted!.State);
            Assert.NotNull(persisted.RemoteOperationId);

            // The content that crossed the wire must be exactly the pipeline's extracted content:
            // the staged durable hash is what the reserve request declares, and the PUT body is
            // the normalized text the extractor produced for this source key.
            Assert.Equal(persisted.NormalizedTextHash, handler.ReserveSha256);
            Assert.Equal(expectedBytes, handler.ReserveDeclaredBytes);
            Assert.Equal(expectedText, handler.UploadBody);
        }
        finally
        {
            TryDeleteDirectory(corpus);
            harness.Dispose();
        }
    }

    // --- Real loopback: real extractor + real API client over a real HTTP server -------------------

    [Fact]
    public async Task Loopback_RealExtractorAndRealApiClient_OverRealLoopbackHttpServer_LoadsDocument()
    {
        var harness = new Harness();
        var corpus = Path.Combine(Path.GetTempPath(), $"rag-loopback-{Guid.NewGuid():N}");
        LoopbackHttpApi? server = null;
        try
        {
            await harness.InitializeAsync();

            var companionPath = LocateCompanionAssembly();
            Assert.True(File.Exists(companionPath), $"Rag.Companion assembly not found at '{companionPath}'.");

            Directory.CreateDirectory(corpus);
            var markdownPath = Path.Combine(corpus, "note.md");
            await File.WriteAllTextAsync(
                markdownPath,
                "# Loopback over a real socket\n\nEl contenido extraído — con acentos — cruza el socket.\n");

            using var extractor = new CompanionReflectionExtractor(companionPath);
            var extraction = await extractor.ExtractAsync(
                new ExtractionRequest(markdownPath, "txt"),
                CancellationToken.None);
            Assert.Equal(ExtractionOutcome.Completed, extraction.Outcome);

            var expectedText = extraction.NormalizedText!;
            var expectedBytes = Encoding.UTF8.GetByteCount(expectedText);
            var expectedHash = ComputeSha256(expectedText);

            // Real in-process HTTP/1.1 server on a real loopback TCP socket: the client below speaks HTTP
            // over a socket instead of through a stubbed HttpMessageHandler.
            server = LoopbackHttpApi.Start(expectedText, expectedHash);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var client = new HistoricalApiClient(
                http,
                new HistoricalApiClientOptions(
                    server.BaseUri,
                    LoopbackCollectionId,
                    _ => Task.FromResult(new RagServiceCredential("loopback-key", "loopback-rag-secret")),
                    _ => Task.FromResult(new CloudflareServiceToken("loopback-cf-id", "loopback-cf-secret")),
                    ContentResolver: (key, ct) => ResolveExtractedContent(extractor, key, ct)));

            var document = await harness.AddDocumentWithKeyAsync(markdownPath);
            var pipeline = new HistoricalPipeline(extractor, client, harness.RunStore, DefaultOptions());

            // No API-client state priming here: the unprimed real client must resolve its own remote ids
            // from the pipeline's stage operations, exactly as it does in production wiring.
            var result = await pipeline.RunAsync(harness.RunId);

            Assert.Null(server.LastError);
            var persisted = await harness.RunStore.GetRunDocumentAsync(document.Id);
            var audit = await harness.RunStore.GetAuditEventsAsync(harness.RunId);
            var contractCode = audit.LastOrDefault(static e => e.Action == "blocked_contract")?.OutcomeCode;
            // A real loopback run must drive the document to a durable Loaded receipt.
            Assert.True(
                result.LoadedCount == 1,
                $"Expected 1 loaded document, got {result.LoadedCount} (block: {result.BlockReason}, claimed: {result.ClaimedCount},"
                + $" blocked-operator-action: {result.BlockedOperatorActionCount}, contract: {contractCode ?? "none"},"
                + $" real HTTP requests: {server.RequestCount}).");
            Assert.Equal(1, result.CommittedCount);
            Assert.True(result.CommittedBytes > 0);

            Assert.Equal(DocumentState.Loaded, persisted!.State);
            // The document reached Loaded through the real commit response and the real operation poll.
            Assert.Equal(LoopbackOperationId.ToString("D"), persisted.RemoteOperationId);

            // Boundary proof: the whole protocol crossed a real loopback socket.
            Assert.True(server.RequestCount >= 5, $"Expected at least 5 real HTTP requests, got {server.RequestCount}.");
            Assert.True(server.SawRoute("POST", "/api/v1/auth/token"), "The token exchange did not cross the socket.");
            Assert.True(
                server.SawRoute("POST", $"/api/v1/historical/collections/{LoopbackCollectionId:D}/uploads"),
                "The reserve request did not cross the socket.");
            Assert.True(
                server.SawRoute("PUT", $"/api/v1/historical/uploads/{LoopbackUploadId:D}/content"),
                "The content upload did not cross the socket.");
            Assert.True(
                server.SawRoute("POST", $"/api/v1/historical/uploads/{LoopbackUploadId:D}:commit"),
                "The commit request did not cross the socket.");
            Assert.True(
                server.SawRoute("GET", $"/api/v1/historical/collections/{LoopbackCollectionId:D}/operations/{LoopbackOperationId:D}"),
                "The operation poll did not cross the socket.");

            // The bytes the socket actually carried are exactly what the real extractor produced.
            Assert.Equal(expectedBytes, server.UploadBodyBytes);
            Assert.True(server.UploadBodyBytesMatchExtractor, "The uploaded bytes were not the extractor's UTF-8 output.");
            Assert.Equal(expectedText, server.UploadBody);
            Assert.Equal(expectedBytes, server.ReserveDeclaredBytes);
            Assert.Equal(persisted.NormalizedTextHash, server.ReserveSha256);
        }
        finally
        {
            if (server is not null)
            {
                await server.DisposeAsync();
            }

            TryDeleteDirectory(corpus);
            harness.Dispose();
        }
    }

    [Fact]
    public async Task Loopback_RealServerKeepsReportingPending_NeverReportsLoaded()
    {
        var harness = new Harness();
        var corpus = Path.Combine(Path.GetTempPath(), $"rag-loopback-{Guid.NewGuid():N}");
        LoopbackHttpApi? server = null;
        try
        {
            await harness.InitializeAsync();

            var companionPath = LocateCompanionAssembly();
            Assert.True(File.Exists(companionPath), $"Rag.Companion assembly not found at '{companionPath}'.");

            Directory.CreateDirectory(corpus);
            var markdownPath = Path.Combine(corpus, "note.md");
            await File.WriteAllTextAsync(markdownPath, "# Still pending\n\nA real server response decides the outcome.\n");

            using var extractor = new CompanionReflectionExtractor(companionPath);
            var extraction = await extractor.ExtractAsync(
                new ExtractionRequest(markdownPath, "txt"),
                CancellationToken.None);
            Assert.Equal(ExtractionOutcome.Completed, extraction.Outcome);

            var expectedText = extraction.NormalizedText!;
            var expectedHash = ComputeSha256(expectedText);

            // The same real loopback socket, but the server keeps reporting the operation as still pending.
            server = LoopbackHttpApi.Start(expectedText, expectedHash, pollStatus: "pending");

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var client = new HistoricalApiClient(
                http,
                new HistoricalApiClientOptions(
                    server.BaseUri,
                    LoopbackCollectionId,
                    _ => Task.FromResult(new RagServiceCredential("loopback-key", "loopback-rag-secret")),
                    _ => Task.FromResult(new CloudflareServiceToken("loopback-cf-id", "loopback-cf-secret")),
                    ContentResolver: (key, ct) => ResolveExtractedContent(extractor, key, ct)));

            var document = await harness.AddDocumentWithKeyAsync(markdownPath);
            var pipeline = new HistoricalPipeline(extractor, client, harness.RunStore, DefaultOptions());

            var result = await pipeline.RunAsync(harness.RunId);

            Assert.Null(server.LastError);

            // The real pending response is honoured: nothing is reported loaded or committed.
            Assert.Equal(0, result.LoadedCount);
            Assert.Equal(0, result.CommittedCount);
            Assert.Equal(0L, result.CommittedBytes);

            var persisted = await harness.RunStore.GetRunDocumentAsync(document.Id);
            Assert.Equal(DocumentState.RemotePending, persisted!.State);
            Assert.NotEqual(DocumentState.Loaded, persisted.State);

            // The full protocol still crossed the real socket, ending with the real poll response.
            Assert.True(server.SawRoute("POST", "/api/v1/auth/token"), "The token exchange did not cross the socket.");
            Assert.True(
                server.SawRoute("PUT", $"/api/v1/historical/uploads/{LoopbackUploadId:D}/content"),
                "The content upload did not cross the socket.");
            Assert.True(
                server.SawRoute("POST", $"/api/v1/historical/uploads/{LoopbackUploadId:D}:commit"),
                "The commit request did not cross the socket.");
            Assert.True(
                server.SawRoute("GET", $"/api/v1/historical/collections/{LoopbackCollectionId:D}/operations/{LoopbackOperationId:D}"),
                "The operation poll did not cross the socket.");
        }
        finally
        {
            if (server is not null)
            {
                await server.DisposeAsync();
            }

            TryDeleteDirectory(corpus);
            harness.Dispose();
        }
    }

    // --- Bounded concurrency --------------------------------------------------

    [Fact]
    public async Task Concurrency_IsBoundedToOne_ProcessesDocumentsSerially()
    {
        var harness = new Harness();
        try
        {
            await harness.InitializeAsync();
            await harness.AddPendingDocumentAsync();
            await harness.AddPendingDocumentAsync();
            await harness.AddPendingDocumentAsync();

            var probe = new ConcurrencyProbeExtractor(new FakeTextExtractor());
            var pipeline = new HistoricalPipeline(
                probe,
                new ScriptedApiClient(),
                harness.RunStore,
                new PipelineOptions(
                    Concurrency: 1,
                    StagedByteWatermark: long.MaxValue,
                    StagedCountWatermark: int.MaxValue));

            var result = await pipeline.RunAsync(harness.RunId);

            Assert.Equal(3, result.LoadedCount);
            Assert.Equal(1, probe.MaxConcurrent);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [Fact]
    public async Task PipelineOptions_ConcurrencyBelowOne_IsRejected()
    {
        var harness = new Harness();
        try
        {
            await harness.InitializeAsync();

            var options = new PipelineOptions(
                Concurrency: 0,
                StagedByteWatermark: 1,
                StagedCountWatermark: 1);

            Assert.Throws<ArgumentOutOfRangeException>(() => new HistoricalPipeline(
                new FakeTextExtractor(),
                new ScriptedApiClient(),
                harness.RunStore,
                options));
        }
        finally
        {
            harness.Dispose();
        }
    }

    // --- Pause / resume ------------------------------------------------------

    [Fact]
    public async Task PauseRequested_StopsWithoutClaiming_AndResumeContinues()
    {
        var harness = new Harness();
        try
        {
            await harness.InitializeAsync();
            await harness.AddPendingDocumentAsync();
            await harness.AddPendingDocumentAsync();

            var pipeline = NewPipeline(harness.RunStore, new ScriptedApiClient());

            await pipeline.RequestPauseAsync(harness.RunId);
            var paused = await pipeline.RunAsync(harness.RunId);

            Assert.True(paused.Paused);
            Assert.Equal(0, paused.ClaimedCount);

            var run = await harness.RunStore.GetRunAsync(harness.RunId);
            Assert.Equal(RunObservedState.Paused, run!.ObservedState);

            await pipeline.ResumeAsync(harness.RunId);
            var resumed = await pipeline.RunAsync(harness.RunId);

            Assert.False(resumed.Paused);
            Assert.Equal(2, resumed.LoadedCount);
        }
        finally
        {
            harness.Dispose();
        }
    }

    // --- Kill / restart recovery --------------------------------------------

    [Fact]
    public async Task Restart_RecoversTransientAndPreservesLoaded()
    {
        var harness = new Harness();
        try
        {
            await harness.InitializeAsync();
            var inFlight = await harness.AddDocumentAsync(DocumentState.Committing, reserveAttempts: 1);
            var loaded = await harness.AddDocumentAsync(DocumentState.Loaded);

            // Restart with a fresh pipeline (no scripted faults).
            var reopened = await harness.ReopenAsync();
            var pipeline = NewPipeline(reopened, new ScriptedApiClient());
            var result = await pipeline.RunAsync(harness.RunId);

            Assert.Equal(DocumentState.Loaded, (await reopened.GetRunDocumentAsync(inFlight.Id))!.State);
            Assert.Equal(DocumentState.Loaded, (await reopened.GetRunDocumentAsync(loaded.Id))!.State);
            Assert.Equal(1, result.LoadedCount); // only the recovered in-flight document is re-processed
        }
        finally
        {
            harness.Dispose();
        }
    }

    // --- No unconfirmed loaded state ----------------------------------------

    [Fact]
    public async Task PendingPoll_NeverReportedLoaded()
    {
        var harness = new Harness();
        try
        {
            await harness.InitializeAsync();
            var document = await harness.AddPendingDocumentAsync();

            var api = new ScriptedApiClient { PollPending = true };
            var pipeline = NewPipeline(harness.RunStore, api);
            var result = await pipeline.RunAsync(harness.RunId);

            Assert.Equal(0, result.LoadedCount);

            var persisted = await harness.RunStore.GetRunDocumentAsync(document.Id);
            Assert.Equal(DocumentState.RemotePending, persisted!.State);
            Assert.NotEqual(DocumentState.Loaded, persisted.State);
        }
        finally
        {
            harness.Dispose();
        }
    }

    // --- Cancellation and auth block ----------------------------------------

    [Fact]
    public async Task CancellationBeforeRun_ConsumesNoAttempt()
    {
        var harness = new Harness();
        try
        {
            await harness.InitializeAsync();
            var document = await harness.AddPendingDocumentAsync();

            var pipeline = NewPipeline(harness.RunStore, new ScriptedApiClient());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                pipeline.RunAsync(harness.RunId, new CancellationToken(canceled: true)));

            var persisted = await harness.RunStore.GetRunDocumentAsync(document.Id);
            Assert.Equal(0, persisted!.ReserveAttempts);
            Assert.Equal(DocumentState.Pending, persisted.State);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [Fact]
    public async Task AuthFailure_BlocksGlobally_NoRetryLoop()
    {
        var harness = new Harness();
        try
        {
            await harness.InitializeAsync();
            await harness.AddPendingDocumentAsync();
            await harness.AddPendingDocumentAsync();

            var api = new ScriptedApiClient();
            api.Script(new ApiOperationResult(ApiOutcome.AuthFailure, ErrorCode: "blocked_auth"));

            var pipeline = NewPipeline(harness.RunStore, api);
            var result = await pipeline.RunAsync(harness.RunId);

            Assert.Equal(1, result.BlockedAuthCount);
            Assert.Equal(FailureClass.Authentication, result.BlockReason);
            Assert.Equal(1, api.OperationCalls); // no document-level retry loop

            var run = await harness.RunStore.GetRunAsync(harness.RunId);
            Assert.Equal(RunObservedState.BlockedAuth, run!.ObservedState);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [Fact]
    public async Task InfrastructureFailureDuringProcessing_CheckpointsAndBlocksRun()
    {
        var harness = new Harness();
        try
        {
            await harness.InitializeAsync();
            var alreadyLoaded = await harness.AddDocumentAsync(DocumentState.Loaded);
            await harness.AddPendingDocumentAsync();

            var pipeline = new HistoricalPipeline(
                new ThrowingExtractor(),
                new ScriptedApiClient(),
                harness.RunStore,
                DefaultOptions());

            var result = await pipeline.RunAsync(harness.RunId);

            Assert.Equal(FailureClass.LocalCapacity, result.BlockReason);
            Assert.Equal(1, result.BlockedOperatorActionCount);

            var run = await harness.RunStore.GetRunAsync(harness.RunId);
            Assert.Equal(RunObservedState.BlockedOperatorAction, run!.ObservedState);

            // Previously confirmed work is preserved (checkpoint), never silently skipped.
            Assert.Equal(DocumentState.Loaded, (await harness.RunStore.GetRunDocumentAsync(alreadyLoaded.Id))!.State);
        }
        finally
        {
            harness.Dispose();
        }
    }

    [Fact]
    public void ConfigurationSnapshot_ContainsBoundsAndNoSecrets()
    {
        var options = new PipelineOptions(
            Concurrency: 1,
            StagedByteWatermark: 1024,
            StagedCountWatermark: 8);

        var snapshot = options.BuildConfigurationSnapshot();

        Assert.Contains("1", snapshot, StringComparison.Ordinal);
        Assert.Contains("1024", snapshot, StringComparison.Ordinal);
        Assert.Contains("8", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", snapshot, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", snapshot, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", snapshot, StringComparison.OrdinalIgnoreCase);
    }

    // --- Helpers ------------------------------------------------------------

    private static readonly Guid LoopbackCollectionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid LoopbackUploadId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LoopbackDocumentId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid LoopbackVersionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid LoopbackOperationId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static PipelineOptions DefaultOptions()
        => new(Concurrency: 1, StagedByteWatermark: long.MaxValue, StagedCountWatermark: int.MaxValue);

    private static HistoricalPipeline NewPipeline(
        SqliteRunStore runStore,
        IHistoricalApiClient api,
        WatermarkScheduler? watermarks = null)
        => new(new FakeTextExtractor(), api, runStore, DefaultOptions(), watermarks);

    private static ApiOperationResult Transient(string errorCode)
        => new(ApiOutcome.TransientFailure, ErrorCode: errorCode);

    /// <summary>The staged byte count the pipeline must account for a source key's normalized text.</summary>
    private static async Task<long> StagedBytesForAsync(string sourceKey)
    {
        var extraction = await new FakeTextExtractor().ExtractAsync(new ExtractionRequest(sourceKey, "txt"));
        Assert.Equal(ExtractionOutcome.Completed, extraction.Outcome);
        return Encoding.UTF8.GetByteCount(extraction.NormalizedText!);
    }

    private static string LocateCompanionAssembly()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Rag.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName
            ?? throw new InvalidOperationException("Repository root (Rag.sln) not found above the test output directory.");
        foreach (var config in new[] { "Release", "Debug" })
        {
            var candidate = Path.Combine(root, "src", "Rag.Companion", "bin", config, "net10.0", "win-x64", "Rag.Companion.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(root, "src", "Rag.Companion", "bin", "Release", "net10.0", "win-x64", "Rag.Companion.dll");
    }

    /// <summary>
    /// Best-effort removal of a scratch directory. Cleanup never fails a test and never touches anything
    /// outside the uniquely created loopback directory passed in.
    /// </summary>
    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; the OS temp directory is reaped independently.
        }
    }

    private static string ComputeSha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>
    /// Resolves the content for a source key through the same real extractor the pipeline uses, so the
    /// HTTP request crosses the wire with exactly the pipeline's extracted content.
    /// </summary>
    private static async Task<HistoricalContent> ResolveExtractedContent(
        CompanionReflectionExtractor extractor,
        string key,
        CancellationToken cancellationToken)
    {
        var resolved = await extractor.ExtractAsync(new ExtractionRequest(key, "txt"), cancellationToken).ConfigureAwait(false);
        Assert.True(
        resolved.Outcome == ExtractionOutcome.Completed && resolved.NormalizedText is not null,
        $"Resolver extraction failed for '{key}': {resolved.ErrorCode ?? resolved.Outcome.ToString()}");
        return new HistoricalContent(resolved.NormalizedText!, Encoding.UTF8.GetByteCount(resolved.NormalizedText!));
    }

    private sealed class ThrowingExtractor : IExtractor
    {
        public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default)
            => throw new SqliteException("disk I/O error", 10);
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

    private sealed class ConcurrencyProbeExtractor : IExtractor
    {
        private readonly IExtractor _inner;
        private int _current;
        private int _max;

        public int MaxConcurrent => Volatile.Read(ref _max);

        public ConcurrencyProbeExtractor(IExtractor inner) => _inner = inner;

        public async Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default)
        {
            var current = Interlocked.Increment(ref _current);
            UpdateMax(current);
            try
            {
                return await _inner.ExtractAsync(request, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }

        private void UpdateMax(int candidate)
        {
            var observed = Volatile.Read(ref _max);
            while (candidate > observed)
            {
                var actual = Interlocked.CompareExchange(ref _max, candidate, observed);
                if (actual == observed)
                {
                    return;
                }

                observed = actual;
            }
        }
    }

    private sealed class ScriptedApiClient : IHistoricalApiClient
    {
        private readonly Queue<ApiOperationResult> _script = new();

        public int OperationCalls { get; private set; }
        public bool PollPending { get; init; }

        public void Script(params ApiOperationResult[] results)
        {
            foreach (var result in results)
            {
                _script.Enqueue(result);
            }
        }

        public Task<ApiOperationResult> ReserveAsync(ApiOperation operation, CancellationToken cancellationToken = default)
            => Next(cancellationToken);

        public Task<ApiOperationResult> UploadAsync(ApiOperation operation, CancellationToken cancellationToken = default)
            => Next(cancellationToken);

        public Task<ApiOperationResult> CommitAsync(ApiOperation operation, CancellationToken cancellationToken = default)
            => Next(cancellationToken);

        public Task<ApiOperationResult> PollAsync(ApiOperation operation, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ApiOperationResult(
                ApiOutcome.Success,
                RemoteId: "op-1",
                Pending: PollPending));
        }

        private Task<ApiOperationResult> Next(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OperationCalls++;
            return Task.FromResult(_script.Count > 0
                ? _script.Dequeue()
                : new ApiOperationResult(ApiOutcome.Success, RemoteId: "remote-1"));
        }
    }

    /// <summary>Simulates a process kill during the poll: the durable poll reservation stands, then it dies.</summary>
    private sealed class KillDuringPollApi : IHistoricalApiClient
    {
        private readonly CancellationTokenSource _kill;

        public KillDuringPollApi(CancellationTokenSource kill) => _kill = kill;

        public int PollCalls { get; private set; }

        public Task<ApiOperationResult> ReserveAsync(ApiOperation operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new ApiOperationResult(ApiOutcome.Success, RemoteId: "upload-1"));

        public Task<ApiOperationResult> UploadAsync(ApiOperation operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new ApiOperationResult(ApiOutcome.Success, RemoteId: "upload-1"));

        public Task<ApiOperationResult> CommitAsync(ApiOperation operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new ApiOperationResult(ApiOutcome.Success, RemoteId: "operation-1"));

        public Task<ApiOperationResult> PollAsync(ApiOperation operation, CancellationToken cancellationToken = default)
        {
            PollCalls++;
            _kill.Cancel();
            throw new OperationCanceledException();
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"rag-pipeline-{Guid.NewGuid():N}");

        public string DatabasePath => Path.Combine(_directory, "store.sqlite");
        public SqliteStore Store { get; private set; } = null!;
        public SqliteRunStore RunStore { get; private set; } = null!;
        public Guid RunId { get; private set; }
        public Guid SingleDocumentId { get; private set; }

        public async Task InitializeAsync()
        {
            Directory.CreateDirectory(_directory);
            Store = new SqliteStore(DatabasePath);
            await Store.InitializeAsync();
            RunStore = new SqliteRunStore(Store);
            RunId = Guid.NewGuid();
            await RunStore.CreateRunAsync(new Run(
                RunId,
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
            var document = await AddDocumentAsync(DocumentState.Pending);
            SingleDocumentId = document.Id;
            return document;
        }

        public async Task<RunDocument> AddDocumentAsync(DocumentState state, int reserveAttempts = 0)
        {
            var document = new RunDocument(
                Guid.NewGuid(),
                RunId,
                Guid.NewGuid(),
                $"source-{Guid.NewGuid():N}",
                state,
                ReserveAttempts: reserveAttempts,
                NormalizedTextHash: state == DocumentState.Committing ? "sha256" : null,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow);
            await RunStore.AddRunDocumentAsync(document);
            return document;
        }

        public async Task<RunDocument> AddDocumentWithKeyAsync(string sourceKey, DocumentState state = DocumentState.Pending)
        {
            var document = new RunDocument(
                Guid.NewGuid(),
                RunId,
                Guid.NewGuid(),
                sourceKey,
                state,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow);
            await RunStore.AddRunDocumentAsync(document);
            return document;
        }

        public async Task<SqliteRunStore> ReopenAsync()
        {
            await Store.DisposeAsync();
            Store = new SqliteStore(DatabasePath);
            await Store.InitializeAsync();
            RunStore = new SqliteRunStore(Store);
            return RunStore;
        }

        public void Dispose()
        {
            try
            {
                Store.DisposeAsync().AsTask().GetAwaiter().GetResult();
                Directory.Delete(_directory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _expectedSha256;
        private readonly string _expectedText;
        private readonly long _expectedBytes;
        private readonly Guid _uploadId = LoopbackUploadId;
        private readonly Guid _documentId = LoopbackDocumentId;
        private readonly Guid _versionId = LoopbackVersionId;
        private readonly Guid _operationId = LoopbackOperationId;

        public int RequestCount { get; private set; }
        public string? ReserveSha256 { get; private set; }
        public long ReserveDeclaredBytes { get; private set; }
        public string? UploadBody { get; private set; }

        public RecordingHandler(string expectedSha256, string expectedText, long expectedBytes)
        {
            _expectedSha256 = expectedSha256;
            _expectedText = expectedText;
            _expectedBytes = expectedBytes;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/api/v1/auth/token")
            {
                return Json(200, """{"access_token":"loopback-token","token_type":"Bearer","expires_in":900,"scope":"historical:uploads.write historical:operations.read"}""");
            }

            if (path.EndsWith("/uploads", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                ReserveSha256 = root.GetProperty("normalized_text_sha256").GetString();
                ReserveDeclaredBytes = root.GetProperty("declared_bytes").GetInt64();
                return Json(201, $$"""{"upload_id":"{{_uploadId:D}}","state":"reserved","correlation_id":"corr-1","created":true}""");
            }

            if (path.EndsWith("/content", StringComparison.Ordinal) && request.Method == HttpMethod.Put)
            {
                UploadBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                return Json(200, $$"""{"upload_id":"{{_uploadId:D}}","state":"published","declared_bytes":{{_expectedBytes}},"observed_bytes":{{_expectedBytes}},"normalized_text_sha256":"{{_expectedSha256}}"}""");
            }

            if (path.EndsWith(":commit", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                return Json(200, $$"""{"upload_id":"{{_uploadId:D}}","document_id":"{{_documentId:D}}","document_version_id":"{{_versionId:D}}","operation_id":"{{_operationId:D}}","state":"committed"}""");
            }

            if (path.Contains("/operations/", StringComparison.Ordinal) && request.Method == HttpMethod.Get)
            {
                return Json(200, """{"status":"committed"}""");
            }

            return Json(404, """{"title":"missing"}""");
        }

        private static HttpResponseMessage Json(int statusCode, string body)
            => new((HttpStatusCode)statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
    }

    /// <summary>
    /// Minimal in-process HTTP/1.1 server bound to a real loopback TCP socket. It implements only the
    /// frozen Unit 8 historical API contract that <see cref="HistoricalApiClient"/> drives (token
    /// exchange, reserve, content publish, commit and operation poll) and records what the socket
    /// actually carried, so the transport under test is a real HTTP connection over a real socket
    /// rather than a stubbed <see cref="HttpMessageHandler"/>.
    /// </summary>
    private sealed class LoopbackHttpApi : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly List<(string Method, string Path)> _requests = new();
        private readonly object _gate = new();
        private readonly byte[] _extractedBytes;
        private readonly string _extractedSha256;
        private readonly string _pollStatus;
        private volatile ReserveCapture? _reserve;
        private volatile UploadCapture? _upload;
        private volatile string? _lastError;
        private volatile bool _stopped;
        private Task _loop = Task.CompletedTask;

        private LoopbackHttpApi(HttpListener listener, Uri baseUri, string extractedText, string extractedSha256, string pollStatus)
        {
            _listener = listener;
            BaseUri = baseUri;
            _extractedBytes = Encoding.UTF8.GetBytes(extractedText);
            _extractedSha256 = extractedSha256;
            _pollStatus = pollStatus;
        }

        public Uri BaseUri { get; }

        public string? LastError => _lastError;

        public string? ReserveSha256 => _reserve?.Sha256;

        public long ReserveDeclaredBytes => _reserve?.DeclaredBytes ?? 0;

        public string? UploadBody => _upload?.Body;

        public long UploadBodyBytes => _upload?.Bytes ?? 0;

        public bool UploadBodyBytesMatchExtractor => _upload?.MatchesExtractor ?? false;

        public int RequestCount
        {
            get
            {
                lock (_gate)
                {
return _requests.Count;
                }
            }
        }

        public static LoopbackHttpApi Start(string extractedText, string extractedSha256, string pollStatus = "committed")
        {
            for (var attempt = 0; ; attempt++)
            {
                var listener = new HttpListener();
                var baseUri = new Uri($"http://127.0.0.1:{FindFreeLoopbackPort()}/");
                listener.Prefixes.Add(baseUri.ToString());
                try
                {
listener.Start();
                }
                catch (HttpListenerException) when (attempt < 3)
                {
listener.Close();
continue;
                }

                var server = new LoopbackHttpApi(listener, baseUri, extractedText, extractedSha256, pollStatus);
                server._loop = Task.Run(server.AcceptLoopAsync);
                return server;
            }
        }

        public bool SawRoute(string method, string path)
        {
            lock (_gate)
            {
                return _requests.Contains((method, path));
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stopped = true;
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (ObjectDisposedException)
            {
                // The listener was already closed.
            }

            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The accept loop is best effort during shutdown.
            }
        }

        private static int FindFreeLoopbackPort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            try
            {
                return ((IPEndPoint)probe.LocalEndpoint).Port;
            }
            finally
            {
                probe.Stop();
            }
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stopped)
            {
                HttpListenerContext context;
                try
                {
context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
return;
                }

                try
                {
await HandleAsync(context).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
_lastError = exception.Message;
await TryWriteAsync(context, 500, $"{{\"title\":\"{exception.GetType().Name}\"}}").ConfigureAwait(false);
                }
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var method = request.HttpMethod;
            var path = request.Url!.AbsolutePath;

            lock (_gate)
            {
                _requests.Add((method, path));
            }

            if (method == "POST" && path == "/api/v1/auth/token")
            {
                await ReadBodyAsync(request).ConfigureAwait(false);
                await WriteJsonAsync(
context.Response,
200,
"""{"access_token":"loopback-token","token_type":"Bearer","expires_in":900,"scope":"historical:uploads.write historical:operations.read"}""").ConfigureAwait(false);
                return;
            }

            if (method == "POST" && path.EndsWith("/uploads", StringComparison.Ordinal))
            {
                var body = Encoding.UTF8.GetString(await ReadBodyAsync(request).ConfigureAwait(false));
                using var document = JsonDocument.Parse(body);
                _reserve = new ReserveCapture(
document.RootElement.GetProperty("normalized_text_sha256").GetString(),
document.RootElement.GetProperty("declared_bytes").GetInt64());
                await WriteJsonAsync(
context.Response,
201,
$"{{\"upload_id\":\"{LoopbackUploadId:D}\",\"state\":\"reserved\",\"correlation_id\":\"corr-loopback\",\"created\":true}}").ConfigureAwait(false);
                return;
            }

            if (method == "PUT" && path.EndsWith("/content", StringComparison.Ordinal))
            {
                var payload = await ReadBodyAsync(request).ConfigureAwait(false);
                _upload = new UploadCapture(
Encoding.UTF8.GetString(payload),
payload.LongLength,
payload.AsSpan().SequenceEqual(_extractedBytes));
                await WriteJsonAsync(
context.Response,
200,
$"{{\"upload_id\":\"{LoopbackUploadId:D}\",\"state\":\"published\",\"declared_bytes\":{_extractedBytes.LongLength},\"observed_bytes\":{_extractedBytes.LongLength},\"normalized_text_sha256\":\"{_extractedSha256}\"}}").ConfigureAwait(false);
                return;
            }

            if (method == "POST" && path.EndsWith(":commit", StringComparison.Ordinal))
            {
                await ReadBodyAsync(request).ConfigureAwait(false);
                await WriteJsonAsync(
context.Response,
200,
$"{{\"upload_id\":\"{LoopbackUploadId:D}\",\"document_id\":\"{LoopbackDocumentId:D}\",\"document_version_id\":\"{LoopbackVersionId:D}\",\"operation_id\":\"{LoopbackOperationId:D}\",\"state\":\"committed\"}}").ConfigureAwait(false);
                return;
            }

            if (method == "GET" && path.Contains("/operations/", StringComparison.Ordinal))
            {
                await WriteJsonAsync(context.Response, 200, $"{{\"status\":\"{_pollStatus}\"}}").ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(context.Response, 404, """{"title":"not_found"}""").ConfigureAwait(false);
        }

        private static async Task<byte[]> ReadBodyAsync(HttpListenerRequest request)
        {
            using var buffer = new MemoryStream();
            await request.InputStream.CopyToAsync(buffer).ConfigureAwait(false);
            return buffer.ToArray();
        }

        private static async Task TryWriteAsync(HttpListenerContext context, int status, string body)
        {
            try
            {
                await WriteJsonAsync(context.Response, status, body).ConfigureAwait(false);
            }
            catch (Exception)
            {
                context.Response.Abort();
            }
        }

        private static async Task WriteJsonAsync(HttpListenerResponse response, int status, string body)
        {
            var payload = Encoding.UTF8.GetBytes(body);
            response.StatusCode = status;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = payload.Length;
            await response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
            response.Close();
        }

        private sealed record ReserveCapture(string? Sha256, long DeclaredBytes);

        private sealed record UploadCapture(string Body, long Bytes, bool MatchesExtractor);
    }
}
