using System.Text.Json;
using Rag.HistoricalLoader.Contracts;
using Rag.HistoricalLoader.Core.Data;
using Rag.HistoricalLoader.Core.Extraction;
using Rag.HistoricalLoader.Core.Lifecycle;
using Rag.HistoricalLoader.Core.Persistence;
using Rag.HistoricalLoader.Core.Sampling;
using Rag.HistoricalLoader.Engine.Control;
using Rag.HistoricalLoader.Engine.Pipeline;

namespace Rag.HistoricalLoader.UnitTests.Control;

/// <summary>
/// 11.prev-d RED — the privacy sentinel over every serialized success, failure, snapshot and event payload:
/// document content, absolute paths, source keys, configuration snapshots, credential material, tokens and
/// raw exception text have no field to travel in, and the only numeric measurement on the wire is the
/// allowlisted staged-byte count.
/// </summary>
public sealed class PrivacySentinelTests
{
    private const string PathSentinel = "/srv/private/corpus";
    private const string TokenSentinel = "sk-live-sentinel-0123456789abcdef";
    private const string SourceKeySentinel = "source-key-sentinel";
    private const string HashSentinel = "sha256-normalized-text-sentinel";
    private const string RemoteIdSentinel = "remote-document-id-sentinel";

    private static readonly DateTimeOffset SeedTime = new(2026, 9, 14, 10, 11, 12, TimeSpan.Zero);

    private static readonly HashSet<string> AllowedWireFields = new(StringComparer.Ordinal)
    {
        "protocol_version", "request_id", "operation", "status", "payload", "error_code",
        "supported_versions", "capabilities", "installation_id", "engine_instance_id", "limits",
        "max_frame_bytes", "max_json_depth", "max_page_size",
        "command_id", "run_id", "desired_state", "observed_state",
        "inventory", "manifest_id", "completeness", "candidate_count", "candidate_bytes",
        "document_counts", "checkpoint_at", "block_code", "event_high_water_mark",
        "documents", "next_cursor", "document_id", "candidate_id", "state", "attempts",
        "classification", "updated_at",
        "events", "high_water_mark", "event_id", "timestamp", "action", "state_transition",
        "outcome_code", "attempt", "measurements",

        // Three contract fields are maps, so their keys are JSON property names too: the per-operation
        // attempt counters, the document-count state names, and the allowlisted measurement name. Nothing
        // outside this vocabulary may ever appear as a map key either.
        "reserve", "upload", "commit", "poll",
        "pending", "snapshotting", "extracting", "staged", "reserving", "uploading", "committing",
        "remote_pending", "loaded", "skipped_document_error", "retry_wait", "retry_exhausted_network",
        "interrupted", "blocked_auth", "blocked_operator_action",
        "staged_bytes",
    };

    [Fact]
    public async Task EverySerializedPayload_IsFreeOfContentPathsSourceKeysSecretsAndRawExceptionText()
    {
        var harness = await Harness.CreateAsync();
        try
        {
            var dispatcher = new ControlDispatcher(harness.Service);
            var payloads = new List<string>();

            payloads.Add(Frame(await dispatcher.DispatchAsync(Request(ControlOperations.Hello))));

            // A failure envelope must carry a stable code, never the rejected request or a diagnostic.
            payloads.Add(Frame(await dispatcher.DispatchAsync(Request(
                ControlOperations.Start,
                new StartRequest(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString())))));
            payloads.Add(Frame(await dispatcher.DispatchAsync(new ControlRequest(
                ControlProtocol.Version + 1, Guid.NewGuid().ToString(), ControlOperations.Hello))));

            // An accepted mutation over a batch whose durable rows carry every hostile sentinel.
            var runId = Guid.NewGuid();
            var accepted = await dispatcher.DispatchAsync(Request(
                ControlOperations.Start,
                new StartRequest(Guid.NewGuid().ToString(), harness.BatchId.ToString(), runId.ToString())));
            Assert.Equal(ControlDispatchStatus.Handled, accepted.Status);
            payloads.Add(Frame(accepted));

            // A second run whose persisted configuration snapshot, source key, hash and remote identity are hostile.
            var hostileRunId = Guid.NewGuid();
            var hostileDocument = new StartDocument(Guid.NewGuid(), Guid.NewGuid(), SourceKeySentinel);
            await harness.Control.CommitStartAsync(new StartCommandPlan(
                Guid.NewGuid(),
                new Run(
                    hostileRunId, null, "legacy", RunDesiredState.Running, RunObservedState.Running, "0.1.0",
                    $"{{\"root\":\"{PathSentinel}\",\"token\":\"{TokenSentinel}\"}}", SeedTime),
                [hostileDocument]));
            await harness.RunStore.SaveAsync(
                new RunDocument(
                    hostileDocument.Id, hostileRunId, hostileDocument.CandidateId, SourceKeySentinel,
                    DocumentState.Staged, ReserveAttempts: 1, ExtractionHash: HashSentinel,
                    NormalizedTextHash: HashSentinel, RemoteUploadId: RemoteIdSentinel,
                    RemoteDocumentId: RemoteIdSentinel, RemoteVersionId: RemoteIdSentinel,
                    CreatedAt: SeedTime, UpdatedAt: SeedTime),
                "staged", null, 0, "128");
            await harness.RunStore.RecordStagedBytesAsync(hostileRunId, SourceKeySentinel, 256);

            foreach (var run in new[] { hostileRunId, runId })
            {
                payloads.Add(Frame(await dispatcher.DispatchAsync(Request(
                    ControlOperations.GetState, new GetStateRequest(run.ToString())))));
                payloads.Add(Frame(await dispatcher.DispatchAsync(Request(
                    ControlOperations.GetDocuments, new GetDocumentsRequest(run.ToString(), Limit: 10)))));
                payloads.Add(Frame(await dispatcher.DispatchAsync(Request(
                    ControlOperations.GetEvents, new GetEventsRequest(Limit: 10, RunId: run.ToString())))));
            }

            var joined = string.Join("\n", payloads);

            Assert.DoesNotContain(PathSentinel, joined, StringComparison.Ordinal);
            Assert.DoesNotContain(TokenSentinel, joined, StringComparison.Ordinal);
            Assert.DoesNotContain(SourceKeySentinel, joined, StringComparison.Ordinal);
            Assert.DoesNotContain(HashSentinel, joined, StringComparison.Ordinal);
            Assert.DoesNotContain(RemoteIdSentinel, joined, StringComparison.Ordinal);
            Assert.DoesNotContain("configuration_snapshot", joined, StringComparison.Ordinal);
            Assert.DoesNotContain("source_document_key", joined, StringComparison.Ordinal);
            Assert.DoesNotContain("normalized_text_hash", joined, StringComparison.Ordinal);
            Assert.DoesNotContain("extraction_hash", joined, StringComparison.Ordinal);
            Assert.DoesNotContain("Exception", joined, StringComparison.Ordinal);
            Assert.DoesNotContain(" at Rag.", joined, StringComparison.Ordinal);

            // Every serialized payload is an independent JSON document; each one is walked on its own.
            foreach (var field in payloads.SelectMany(PropertyNames))
            {
                Assert.Contains(field, AllowedWireFields);
            }
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task SerializedProjections_ExposeOnlyTheAllowlistedFieldsAndTheStagedBytesMeasurement()
    {
        var harness = await Harness.CreateAsync();
        try
        {
            var dispatcher = new ControlDispatcher(harness.Service);
            var runId = Guid.NewGuid();
            await harness.Control.CommitStartAsync(new StartCommandPlan(
                Guid.NewGuid(),
                new Run(runId, null, "legacy", RunDesiredState.Running, RunObservedState.Running, "0.1.0", "{}", SeedTime),
                [new StartDocument(Guid.NewGuid(), Guid.NewGuid(), SourceKeySentinel)]));
            await harness.RunStore.RecordStagedBytesAsync(runId, SourceKeySentinel, 512);

            var state = await dispatcher.DispatchAsync(Request(ControlOperations.GetState, new GetStateRequest(runId.ToString())));
            var documents = await dispatcher.DispatchAsync(Request(
                ControlOperations.GetDocuments, new GetDocumentsRequest(runId.ToString(), Limit: 10)));
            var events = await dispatcher.DispatchAsync(Request(
                ControlOperations.GetEvents, new GetEventsRequest(Limit: 10, RunId: runId.ToString())));

            Assert.Equal(ControlDispatchStatus.Handled, state.Status);
            Assert.Equal(ControlDispatchStatus.Handled, documents.Status);
            Assert.Equal(ControlDispatchStatus.Handled, events.Status);

            var eventRoot = Payload(events.ResponseJson!);
            // The start receipt is the first durable event; the staged-byte row is the allowlisted last one.
            var single = eventRoot.GetProperty("events").EnumerateArray().Last();
            Assert.Equal("staged_bytes", single.GetProperty("action").GetString());
            Assert.Equal(
                ["staged_bytes"],
                single.GetProperty("measurements").EnumerateObject().Select(property => property.Name));

            var documentRoot = Payload(documents.ResponseJson!);
            var row = documentRoot.GetProperty("documents")[0];
            Assert.Equal(
                ["commit", "poll", "reserve", "upload"],
                row.GetProperty("attempts").EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
            Assert.Equal("pending", row.GetProperty("state").GetString());
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    // --- helpers ------------------------------------------------------------

    private static string Frame(ControlDispatchResult result) => result.ResponseJson ?? string.Empty;

    private static ControlRequest Request(string operation, object? payload = null) =>
        new(ControlProtocol.Version, Guid.NewGuid().ToString(), operation,
            payload is null ? null : JsonDocument.Parse(ControlWire.Serialize(payload)).RootElement.Clone());

    private static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement.GetProperty("payload");

    private static IEnumerable<string> PropertyNames(string json)
    {
        var names = new List<string>();
        Walk(JsonDocument.Parse(json).RootElement, names);
        return names;

        static void Walk(JsonElement element, List<string> names)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        names.Add(property.Name);
                        Walk(property.Value, names);
                    }

                    break;

                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        Walk(item, names);
                    }

                    break;
            }
        }
    }

    /// <summary>A seeded database whose durable rows carry the hostile sentinels.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private Harness(string directory, string databasePath, Guid batchId, SqliteStore store,
            SqliteRunStore runStore, SqliteControlStore control, ControlService service)
        {
            Directory = directory;
            DatabasePath = databasePath;
            BatchId = batchId;
            Store = store;
            RunStore = runStore;
            Control = control;
            Service = service;
        }

        public string Directory { get; }

        public string DatabasePath { get; }

        public Guid BatchId { get; }

        public SqliteStore Store { get; }

        public SqliteRunStore RunStore { get; }

        public SqliteControlStore Control { get; }

        public ControlService Service { get; }

        public static async Task<Harness> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"rag-control-privacy-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(directory);
            var databasePath = Path.Combine(directory, "store.sqlite");
            var manifestId = Guid.NewGuid();
            var rootId = Guid.NewGuid();
            var batchId = Guid.NewGuid();

            var store = new SqliteStore(databasePath);
            await store.InitializeAsync();
            await store.AddSourceRootAsync(new SourceRoot(rootId, "seed", PathSentinel, SeedTime));
            await store.CreateManifestAsync(new Manifest(manifestId, 1, ManifestState.Complete, SeedTime, SeedTime));
            var members = new List<SampleMember>();
            for (var index = 0; index < 2; index++)
            {
                var candidateId = Guid.NewGuid();
                await store.AddCandidateAsync(new Candidate(
                    candidateId, manifestId, rootId, $"{PathSentinel}/doc-{index}.txt", ".txt", 1024, SeedTime,
                    "eligible", MetadataFingerprint: $"fingerprint-{candidateId:N}"));
                members.Add(new SampleMember(batchId, candidateId, "stratum-a", $"fingerprint-{candidateId:N}", index));
            }

            await using (var samples = new SampleSetStore(databasePath))
            {
                await samples.InitializeAsync();
                await samples.SaveAsync(
                    new SampleSet(batchId, manifestId, "v1", "pcg64", 42, 2, 1, "confidence-rules", true, true, SeedTime, "0.98"),
                    members);
            }

            var runStore = new SqliteRunStore(store);
            var control = new SqliteControlStore(store);
            var options = new ControlServiceOptions
            {
                EngineInstanceId = $"engine-instance-{Guid.NewGuid():N}",
                EngineVersion = "0.1.0",
                CollectionId = "legacy",
                DatabasePath = databasePath,
            };
            var pipeline = new HistoricalPipeline(
                new FakeTextExtractor(), new FakeHistoricalApiClient(), runStore,
                new PipelineOptions(1, 64L * 1024 * 1024, 1_000));
            var service = new ControlService(store, control, runStore, pipeline, options);
            await service.StartAsync();

            return new Harness(directory, databasePath, batchId, store, runStore, control, service);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Service.DisposeAsync();
                await Store.DisposeAsync();
            }
            finally
            {
                try
                {
                    System.IO.Directory.Delete(Directory, recursive: true);
                }
                catch (IOException)
                {
                    // Best-effort cleanup.
                }
            }
        }
    }
}
