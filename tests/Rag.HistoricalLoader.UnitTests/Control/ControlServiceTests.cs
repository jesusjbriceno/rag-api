using System.Globalization;
using System.Text;
using System.Text.Json;
using Rag.HistoricalLoader.Contracts;
using Rag.HistoricalLoader.Core.Data;
using Rag.HistoricalLoader.Core.Extraction;
using Rag.HistoricalLoader.Core.Lifecycle;
using Rag.HistoricalLoader.Core.Persistence;
using Rag.HistoricalLoader.Core.Sampling;
using Rag.HistoricalLoader.Engine.Control;
using Rag.HistoricalLoader.Engine.Pipeline;
using DocumentPage = Rag.HistoricalLoader.Contracts.DocumentPage;
using EventPage = Rag.HistoricalLoader.Contracts.EventPage;

namespace Rag.HistoricalLoader.UnitTests.Control;

/// <summary>
/// 11.prev-d RED — the control service behind the unchanged <see cref="IControlCommandHandler"/> seam.
/// Everything here runs on Linux against a fake transport, the fake extractor and the fake API: the
/// approved-batch start, one active run per installation, durable receipt replay, a pause that never
/// blocks on the drain, durable-count projections, and the opaque allowlisted page cursors.
/// </summary>
public sealed class ControlServiceTests
{
    private static readonly DateTimeOffset SeedTime = new(2026, 9, 14, 10, 11, 12, TimeSpan.Zero);

    // --- hello-before-operations --------------------------------------------

    [Fact]
    public async Task Hello_IsRequiredBeforeEveryOtherOperation_AndAdvertisesIdentityAndLimits()
    {
        var harness = await Harness.CreateAsync();
        try
        {
            var session = new ControlSession(harness.Service);

            var early = await session.HandleAsync(Request(ControlOperations.GetState, new GetStateRequest()));

            Assert.Equal(ControlStatuses.Rejected, early.Status);
            Assert.Equal(ControlErrorCodes.MalformedRequest, early.ErrorCode);
            Assert.False(session.HelloSucceeded);

            var hello = await session.HandleAsync(Request(ControlOperations.Hello));

            Assert.Equal(ControlStatuses.Ok, hello.Status);
            Assert.True(session.HelloSucceeded);
            var payload = Assert.IsType<HelloResult>(hello.Payload);
            Assert.Equal(ControlProtocol.SupportedVersions, payload.SupportedVersions);
            Assert.Equal(ControlCapabilities.All, payload.Capabilities);
            Assert.Equal((await harness.Store.GetInstallationIdAsync()).ToString(), payload.InstallationId);
            Assert.Equal(harness.Options.EngineInstanceId, payload.EngineInstanceId);
            Assert.Equal(ControlProtocol.Limits.MaxFrameBytes, payload.Limits.MaxFrameBytes);
            Assert.Equal(ControlProtocol.Limits.MaxJsonDepth, payload.Limits.MaxJsonDepth);
            Assert.Equal(ControlProtocol.Limits.MaxPageSize, payload.Limits.MaxPageSize);

            var after = await session.HandleAsync(Request(ControlOperations.GetState, new GetStateRequest()));

            Assert.Equal(ControlStatuses.Ok, after.Status);
            Assert.IsType<StateSnapshot>(after.Payload);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    // --- start: approved batch, durable atomic commit -----------------------

    [Fact]
    public async Task Start_ResolvesTheGatePassedBatchAndCommitsRunDocumentsAuditAndReceiptTogether()
    {
        var harness = await Harness.CreateAsync();
        try
        {
            var batch = await harness.SeedApprovedBatchAsync(memberCount: 3);
            var commandId = Guid.NewGuid();
            var runId = Guid.NewGuid();

            var outcome = await harness.Service.HandleAsync(StartRequest(commandId, batch.BatchId, runId));

            Assert.Equal(ControlStatuses.Accepted, outcome.Status);
            var receipt = Assert.IsType<CommandReceipt>(outcome.Payload);
            Assert.Equal(commandId.ToString(), receipt.CommandId);
            Assert.Equal(runId.ToString(), receipt.RunId);
            Assert.Equal(ControlRunStates.Running, receipt.DesiredState);
            Assert.Equal(ControlRunStates.Running, receipt.ObservedState);

            // The engine resolves the batch itself: the wire carried only the opaque batch id, and the durable
            // run records the persisted sample-set id as its sample/batch.
            await harness.Api.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            var run = await harness.RunStore.GetRunAsync(runId);
            Assert.NotNull(run);
            Assert.Equal(batch.BatchId, run!.SampleId);
            Assert.Equal(harness.Options.CollectionId, run.CollectionId);

            var documents = await harness.RunStore.GetRunDocumentsAsync(runId);
            Assert.Equal(
                batch.CandidateIds.OrderBy(id => id),
                documents.Select(document => document.CandidateId).OrderBy(id => id));
            Assert.Equal(3, documents.Select(document => document.Id).Distinct().Count());
            Assert.Equal(3, documents.Select(document => document.SourceDocumentKey).Distinct().Count());
            Assert.All(documents, document => Assert.False(string.IsNullOrWhiteSpace(document.SourceDocumentKey)));

            var audit = await harness.RunStore.GetAuditEventsAsync(runId);
            Assert.Single(audit.Where(entry => entry.Action == "start_requested"));

            var durable = await harness.Control.GetCommandReceiptAsync(commandId);
            Assert.NotNull(durable);
            Assert.Equal(runId, durable!.RunId);
            Assert.Equal(RunDesiredState.Running, run.DesiredState);

            // No response byte carries a path-bearing or configuration-bearing field.
            var json = ControlWire.Serialize(outcome.Payload);
            Assert.DoesNotContain("source_document_key", json);
            Assert.DoesNotContain("configuration_snapshot", json);
            Assert.DoesNotContain(batch.CandidateIds[0].ToString(), json);
        }
        finally
        {
            harness.Api.Release();
            await harness.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("ungated")]
    [InlineData("over_capacity")]
    public async Task Start_RejectsAnUnknownUngatedOrOverCapacityBatch_WithoutWritingAnything(string batchKind)
    {
        var harness = await Harness.CreateAsync(maxStartDocumentCount: 2);
        try
        {
            var batchId = batchKind switch
            {
                "ungated" => (await harness.SeedApprovedBatchAsync(memberCount: 1, gatePassed: false)).BatchId,
                "over_capacity" => (await harness.SeedApprovedBatchAsync(memberCount: 3)).BatchId,
                _ => Guid.NewGuid(),
            };
            var commandId = Guid.NewGuid();
            var runId = Guid.NewGuid();

            var outcome = await harness.Service.HandleAsync(StartRequest(commandId, batchId, runId));

            Assert.Equal(ControlStatuses.Rejected, outcome.Status);
            Assert.Equal(ControlErrorCodes.CommandConflict, outcome.ErrorCode);
            Assert.Null(await harness.Control.GetCommandReceiptAsync(commandId));
            Assert.Null(await harness.RunStore.GetRunAsync(runId));
            Assert.Empty(await harness.RunStore.GetRunDocumentsAsync(runId));
            Assert.Empty(await harness.RunStore.GetAuditEventsAsync(runId));
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task Start_RejectsASecondActiveRunOrAConflictingBatchForAnExistingRun()
    {
        var harness = await Harness.CreateAsync();
        try
        {
            var batch = await harness.SeedApprovedBatchAsync(memberCount: 2);
            var otherBatch = await harness.SeedApprovedBatchAsync(memberCount: 2);
            var runId = Guid.NewGuid();

            Assert.Equal(
                ControlStatuses.Accepted,
                (await harness.Service.HandleAsync(StartRequest(Guid.NewGuid(), batch.BatchId, runId))).Status);
            await harness.Api.Entered.WaitAsync(TimeSpan.FromSeconds(10));

            // One active run per installation: a second run is rejected while the first is active.
            var second = await harness.Service.HandleAsync(StartRequest(Guid.NewGuid(), otherBatch.BatchId, Guid.NewGuid()));
            Assert.Equal(ControlStatuses.Rejected, second.Status);
            Assert.Equal(ControlErrorCodes.CommandConflict, second.ErrorCode);

            // An already-existing run with a conflicting batch is rejected, never merged or duplicated.
            var conflicting = await harness.Service.HandleAsync(StartRequest(Guid.NewGuid(), otherBatch.BatchId, runId));
            Assert.Equal(ControlStatuses.Rejected, conflicting.Status);
            Assert.Equal(ControlErrorCodes.CommandConflict, conflicting.ErrorCode);

            Assert.Equal(2, (await harness.RunStore.GetRunDocumentsAsync(runId)).Count);
        }
        finally
        {
            harness.Api.Release();
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task RepeatedStart_ReplaysTheDurableReceipt_WithoutASecondRunAuditOrPipeline()
    {
        var harness = await Harness.CreateAsync();
        try
        {
            var batch = await harness.SeedApprovedBatchAsync(memberCount: 2);
            var commandId = Guid.NewGuid();
            var runId = Guid.NewGuid();

            var first = await harness.Service.HandleAsync(StartRequest(commandId, batch.BatchId, runId));
            var receipt = Assert.IsType<CommandReceipt>(first.Payload);
            await harness.Api.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            var dispatched = harness.Api.OperationCalls;

            var replay = await harness.Service.HandleAsync(StartRequest(commandId, batch.BatchId, runId));

            Assert.Equal(ControlStatuses.Accepted, replay.Status);
            Assert.Equal(receipt, Assert.IsType<CommandReceipt>(replay.Payload));
            Assert.Equal(2, (await harness.RunStore.GetRunDocumentsAsync(runId)).Count);
            Assert.Single((await harness.RunStore.GetAuditEventsAsync(runId)).Where(entry => entry.Action == "start_requested"));
            Assert.Equal(dispatched, harness.Api.OperationCalls);
        }
        finally
        {
            harness.Api.Release();
            await harness.DisposeAsync();
        }
    }

    // --- pause: acknowledgement is not a checkpoint -------------------------

    [Fact]
    public async Task Pause_IsAcknowledgedWhileThePipelineStillDrains_AndPausedFollowsTheDurableBoundary()
    {
        var harness = await Harness.CreateAsync(drainTimeout: TimeSpan.FromSeconds(10));
        try
        {
            var batch = await harness.SeedApprovedBatchAsync(memberCount: 2);
            var runId = Guid.NewGuid();
            await harness.Service.HandleAsync(StartRequest(Guid.NewGuid(), batch.BatchId, runId));
            await harness.Api.Entered.WaitAsync(TimeSpan.FromSeconds(10));

            var pauseTask = harness.Service
                .HandleAsync(Request(ControlOperations.Pause, new PauseRequest(Guid.NewGuid().ToString(), runId.ToString())))
                .AsTask();

            // The acknowledgement must not wait for the drain: the request thread never becomes the pipeline.
            Assert.Same(pauseTask, await Task.WhenAny(pauseTask, Task.Delay(TimeSpan.FromSeconds(3))));
            var acknowledged = await pauseTask;

            Assert.Equal(ControlStatuses.Accepted, acknowledged.Status);
            var receipt = Assert.IsType<CommandReceipt>(acknowledged.Payload);
            Assert.Equal(ControlRunStates.PauseRequested, receipt.DesiredState);
            Assert.Equal(ControlRunStates.Pausing, receipt.ObservedState);

            var auditing = await harness.RunStore.GetAuditEventsAsync(runId);
            Assert.Equal("pause_requested", auditing[^1].Action);

            var draining = await StateAsync(harness, runId);
            Assert.Equal(ControlRunStates.Pausing, draining.ObservedState);
            Assert.Equal(0, draining.DocumentCounts.GetValueOrDefault(ControlDocumentStates.Loaded));

            harness.Api.Release();
            var paused = await WaitForStateAsync(harness, runId, ControlRunStates.Paused, TimeSpan.FromSeconds(20));

            // Paused only after the in-flight document has its durable receipt, and the drain never claims more.
            Assert.Equal(ControlRunStates.PauseRequested, paused.DesiredState);
            Assert.Equal(1, paused.DocumentCounts.GetValueOrDefault(ControlDocumentStates.Loaded));
            Assert.Equal(1, paused.DocumentCounts.GetValueOrDefault(ControlDocumentStates.Pending));
        }
        finally
        {
            harness.Api.Release();
            await harness.DisposeAsync();
        }
    }

    // --- resume: eligible work only -----------------------------------------

    [Fact]
    public async Task Resume_ContinuesPausedWorkAndPreservesAttemptsAndTerminalRows()
    {
        var harness = await Harness.CreateAsync(drainTimeout: TimeSpan.FromSeconds(10));
        try
        {
            var batch = await harness.SeedApprovedBatchAsync(memberCount: 2);
            var runId = Guid.NewGuid();
            await harness.Service.HandleAsync(StartRequest(Guid.NewGuid(), batch.BatchId, runId));
            await harness.Api.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            await harness.Service.HandleAsync(Request(ControlOperations.Pause, new PauseRequest(Guid.NewGuid().ToString(), runId.ToString())));
            harness.Api.Release();
            await WaitForStateAsync(harness, runId, ControlRunStates.Paused, TimeSpan.FromSeconds(20));

            // A terminal outcome and its exhausted attempt counter are durable facts, not resumable work.
            var exhausted = new RunDocument(
                Guid.NewGuid(), runId, Guid.NewGuid(), "source-exhausted", DocumentState.RetryExhaustedNetwork,
                ReserveAttempts: 3, CreatedAt: SeedTime, UpdatedAt: SeedTime);
            await harness.RunStore.SaveAsync(exhausted, "retry_exhausted", "transient_failure", 3, null);

            var resume = await harness.Service.HandleAsync(ResumeRequest(Guid.NewGuid(), runId));

            Assert.Equal(ControlStatuses.Accepted, resume.Status);
            Assert.Equal(ControlRunStates.Running, Assert.IsType<CommandReceipt>(resume.Payload).DesiredState);

            var completed = await WaitForStateAsync(harness, runId, ControlRunStates.Completed, TimeSpan.FromSeconds(20));
            Assert.Equal(2, completed.DocumentCounts.GetValueOrDefault(ControlDocumentStates.Loaded));

            var documents = await harness.RunStore.GetRunDocumentsAsync(runId);
            Assert.All(
                documents.Where(document => document.Id != exhausted.Id),
                document => Assert.Equal(DocumentState.Loaded, document.State));
            Assert.All(
                documents.Where(document => document.Id != exhausted.Id),
                document => Assert.Equal(1, document.ReserveAttempts));

            var preserved = Assert.Single(documents, document => document.Id == exhausted.Id);
            Assert.Equal(DocumentState.RetryExhaustedNetwork, preserved.State);
            Assert.Equal(3, preserved.ReserveAttempts);
        }
        finally
        {
            harness.Api.Release();
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task Resume_IsRejectedForABlockedRun_AndTheContractCarriesNoBypassFlag()
    {
        var harness = await Harness.CreateAsync();
        try
        {
            var batch = await harness.SeedApprovedBatchAsync(memberCount: 1);
            var runId = Guid.NewGuid();
            await harness.Service.HandleAsync(StartRequest(Guid.NewGuid(), batch.BatchId, runId));
            await harness.Api.Entered.WaitAsync(TimeSpan.FromSeconds(10));
            await harness.RunStore.SetRunObservedStateAsync(runId, RunObservedState.BlockedAuth);

            var outcome = await harness.Service.HandleAsync(ResumeRequest(Guid.NewGuid(), runId));

            Assert.Equal(ControlStatuses.Rejected, outcome.Status);
            Assert.Equal(ControlErrorCodes.CommandConflict, outcome.ErrorCode);
            Assert.Equal(RunObservedState.BlockedAuth, (await harness.RunStore.GetRunAsync(runId))!.ObservedState);

            // Blocked conditions need engine-side validation of the correction: the wire has no bypass flag.
            Assert.Equal(
                ["CommandId", "RunId"],
                typeof(ResumeRequest).GetProperties().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        }
        finally
        {
            harness.Api.Release();
            await harness.DisposeAsync();
        }
    }

    // --- durable projections ------------------------------------------------

    [Fact]
    public async Task GetState_ProjectsDurableRowsOnly_WithAnExplicitEmptyResultAndNoFabricatedLoaded()
    {
        var harness = await Harness.CreateAsync();
        try
        {
            var empty = await harness.Service.HandleAsync(Request(ControlOperations.GetState, new GetStateRequest()));

            Assert.Equal(ControlStatuses.Ok, empty.Status);
            var none = Assert.IsType<StateSnapshot>(empty.Payload);
            Assert.Null(none.RunId);
            Assert.Empty(none.DocumentCounts);
            Assert.Equal(harness.ManifestId.ToString(), none.Inventory.ManifestId);
            Assert.Equal(3, none.Inventory.CandidateCount);
            Assert.Equal("complete", none.Inventory.Completeness);
            Assert.Equal(harness.Options.EngineInstanceId, none.EngineInstanceId);
            Assert.Equal("0", none.EventHighWaterMark);

            // A durably "completed" run with outstanding remote_pending work is never projected as loaded.
            var runId = Guid.NewGuid();
            await harness.RunStore.CreateRunAsync(new Run(
                runId, null, harness.Options.CollectionId, RunDesiredState.Running, RunObservedState.Running,
                "0.1.0", "{}", SeedTime));
            await harness.RunStore.AddRunDocumentAsync(new RunDocument(
                Guid.NewGuid(), runId, Guid.NewGuid(), "source-remote-pending", DocumentState.RemotePending,
                PollAttempts: 1, CreatedAt: SeedTime, UpdatedAt: SeedTime));
            await harness.RunStore.SaveAsync(
                new RunDocument(Guid.NewGuid(), runId, Guid.NewGuid(), "source-loaded", DocumentState.Loaded,
                    ReserveAttempts: 1, UploadAttempts: 1, CommitAttempts: 1, PollAttempts: 1,
                    CreatedAt: SeedTime, UpdatedAt: SeedTime),
                "loaded", null, 0, null);
            await harness.RunStore.SetRunObservedStateAsync(runId, RunObservedState.Completed);

            var projected = await StateAsync(harness, runId);
            var expectedHighWater = (await harness.Control.GetControlSnapshotAsync(runId)).EventHighWaterMark;

            Assert.Equal(runId.ToString(), projected.RunId);
            Assert.Equal(ControlRunStates.Completed, projected.ObservedState);
            Assert.Equal(1, projected.DocumentCounts.GetValueOrDefault(ControlDocumentStates.RemotePending));
            Assert.Equal(1, projected.DocumentCounts.GetValueOrDefault(ControlDocumentStates.Loaded));
            Assert.Equal(2, projected.DocumentCounts.Values.Sum());
            Assert.Null(projected.BlockCode);
            Assert.Null(projected.CheckpointAt);
            Assert.Equal(
                expectedHighWater.ToString(CultureInfo.InvariantCulture),
                projected.EventHighWaterMark);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetDocuments_PagesThroughAnOpaqueUrlSafeCursor_AndRefusesATamperedOne()
    {
        var harness = await Harness.CreateAsync();
        try
        {
            var runId = Guid.NewGuid();
            await harness.RunStore.CreateRunAsync(new Run(
                runId, null, harness.Options.CollectionId, RunDesiredState.Running, RunObservedState.Running,
                "0.1.0", "{}", SeedTime));
            var documents = new[]
            {
                await SeedDocumentAsync(harness, runId, SeedTime.AddSeconds(1), "source-sentinel-one"),
                await SeedDocumentAsync(harness, runId, SeedTime.AddSeconds(2), "source-sentinel-two"),
                await SeedDocumentAsync(harness, runId, SeedTime.AddSeconds(3), "source-sentinel-three"),
            };

            var first = await harness.Service.HandleAsync(
                Request(ControlOperations.GetDocuments, new GetDocumentsRequest(runId.ToString(), Limit: 2)));

            Assert.Equal(ControlStatuses.Ok, first.Status);
            var page = Assert.IsType<DocumentPage>(first.Payload);
            Assert.Equal(2, page.Documents.Count);
            Assert.NotNull(page.NextCursor);

            // The cursor is an opaque, URL-safe, versioned encoding of the keyset position and nothing else.
            Assert.True(ControlDocumentCursor.TryDecode(page.NextCursor, out var cursor));
            Assert.Equal(documents[1].Id, cursor.RunDocumentId);
            Assert.Equal(documents[1].CreatedAt.UtcTicks, cursor.CreatedAt.UtcTicks);
            Assert.Equal($"v1.{documents[1].CreatedAt.UtcTicks}.{documents[1].Id}", DecodeCursor(page.NextCursor!));
            Assert.DoesNotContain('+', page.NextCursor!);
            Assert.DoesNotContain('/', page.NextCursor!);
            Assert.DoesNotContain('=', page.NextCursor!);
            Assert.False(ControlDocumentCursor.TryDecode(EncodeCursor("v2", documents[1].CreatedAt.UtcTicks, documents[1].Id), out _));
            Assert.False(ControlDocumentCursor.TryDecode("not-a-cursor", out _));

            var second = await harness.Service.HandleAsync(
                Request(ControlOperations.GetDocuments, new GetDocumentsRequest(runId.ToString(), Limit: 2, AfterDocumentId: page.NextCursor)));

            var tail = Assert.IsType<DocumentPage>(second.Payload);
            Assert.Equal(documents[2].Id.ToString(), Assert.Single(tail.Documents).DocumentId);
            Assert.Null(tail.NextCursor);

            // The bounded page carries identity, state, attempts, classification and timestamps only.
            var json = ControlWire.Serialize(page);
            Assert.DoesNotContain("source-sentinel", json);
            Assert.DoesNotContain("source_document_key", json);
            Assert.DoesNotContain("normalized_text_hash", json);
            Assert.DoesNotContain("remote_", json, StringComparison.Ordinal);

            var tampered = await harness.Service.HandleAsync(
                Request(ControlOperations.GetDocuments, new GetDocumentsRequest(runId.ToString(), Limit: 2, AfterDocumentId: "!!tampered!!")));
            Assert.Equal(ControlStatuses.Rejected, tampered.Status);
            Assert.Equal(ControlErrorCodes.ResyncRequired, tampered.ErrorCode);

            var unparsableRun = await harness.Service.HandleAsync(
                Request(ControlOperations.GetDocuments, new GetDocumentsRequest("not-a-run", Limit: 2)));
            Assert.Equal(ControlStatuses.Rejected, unparsableRun.Status);
            Assert.Equal(ControlErrorCodes.MalformedRequest, unparsableRun.ErrorCode);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task GetEvents_ReadsBoundedExclusivePagesWithTheHighWaterMark_AndOnlyTheStagedBytesMeasurement()
    {
        var harness = await Harness.CreateAsync();
        try
        {
            var runId = Guid.NewGuid();
            var document = new RunDocument(
                Guid.NewGuid(), runId, Guid.NewGuid(), "source-events", DocumentState.Pending,
                CreatedAt: SeedTime, UpdatedAt: SeedTime);
            await harness.RunStore.CreateRunAsync(new Run(
                runId, null, harness.Options.CollectionId, RunDesiredState.Running, RunObservedState.Running,
                "0.1.0", "{}", SeedTime));
            await harness.RunStore.AddRunDocumentAsync(document);
            await harness.RunStore.RecordStagedBytesAsync(runId, document.SourceDocumentKey, 128);
            await harness.RunStore.SaveAsync(document with { State = DocumentState.Loaded }, "loaded", null, 0, "512");

            var first = await harness.Service.HandleAsync(
                Request(ControlOperations.GetEvents, new GetEventsRequest(Limit: 1, RunId: runId.ToString())));

            Assert.Equal(ControlStatuses.Ok, first.Status);
            var page = Assert.IsType<EventPage>(first.Payload);
            var head = Assert.Single(page.Events);
            Assert.Equal("staged_bytes", head.Action);
            Assert.Equal(128d, head.Measurements!["staged_bytes"]);
            Assert.NotNull(page.NextCursor);
            Assert.Equal("2", page.HighWaterMark);
            Assert.Equal("1", page.NextCursor);

            var second = await harness.Service.HandleAsync(
                Request(ControlOperations.GetEvents, new GetEventsRequest(Limit: 1, RunId: runId.ToString(), AfterEventId: page.NextCursor)));

            var tail = Assert.IsType<EventPage>(second.Payload);
            var next = Assert.Single(tail.Events);
            Assert.Equal("loaded", next.Action);
            Assert.True(
                long.Parse(next.EventId, CultureInfo.InvariantCulture)
                > long.Parse(head.EventId, CultureInfo.InvariantCulture));

            // Only `staged_bytes` is an allowlisted measurement: another action's numeric payload is dropped.
            Assert.Null(next.Measurements);
            Assert.Null(tail.NextCursor);

            var bounded = await harness.Service.HandleAsync(
                Request(ControlOperations.GetEvents,
                    new GetEventsRequest(Limit: 100, RunId: runId.ToString(), ThroughEventId: head.EventId.ToString(CultureInfo.InvariantCulture))));
            Assert.Equal(head.EventId, Assert.Single(Assert.IsType<EventPage>(bounded.Payload).Events).EventId);

            var beyondHighWater = await harness.Service.HandleAsync(
                Request(ControlOperations.GetEvents, new GetEventsRequest(Limit: 100, RunId: runId.ToString(), AfterEventId: "9999")));
            Assert.Equal(ControlStatuses.Rejected, beyondHighWater.Status);
            Assert.Equal(ControlErrorCodes.ResyncRequired, beyondHighWater.ErrorCode);

            var unparsableCursor = await harness.Service.HandleAsync(
                Request(ControlOperations.GetEvents, new GetEventsRequest(Limit: 100, RunId: runId.ToString(), AfterEventId: "not-a-cursor")));
            Assert.Equal(ControlStatuses.Rejected, unparsableCursor.Status);
            Assert.Equal(ControlErrorCodes.ResyncRequired, unparsableCursor.ErrorCode);

            var throughBeyondHighWater = await harness.Service.HandleAsync(
                Request(ControlOperations.GetEvents, new GetEventsRequest(Limit: 100, RunId: runId.ToString(), ThroughEventId: "9999")));
            Assert.Equal(ControlStatuses.Rejected, throughBeyondHighWater.Status);
            Assert.Equal(ControlErrorCodes.MalformedRequest, throughBeyondHighWater.ErrorCode);
        }
        finally
        {
            await harness.DisposeAsync();
        }
    }

    // --- helpers ------------------------------------------------------------

    private static ControlRequest Request(string operation, object? payload = null) =>
        new(ControlProtocol.Version, Guid.NewGuid().ToString(), operation,
            payload is null ? null : JsonDocument.Parse(ControlWire.Serialize(payload)).RootElement.Clone());

    private static ControlRequest StartRequest(Guid commandId, Guid batchId, Guid runId) =>
        Request(ControlOperations.Start, new StartRequest(commandId.ToString(), batchId.ToString(), runId.ToString()));

    private static ControlRequest ResumeRequest(Guid commandId, Guid runId) =>
        Request(ControlOperations.Resume, new ResumeRequest(commandId.ToString(), runId.ToString()));

    private static async Task<RunDocument> SeedDocumentAsync(Harness harness, Guid runId, DateTimeOffset createdAt, string sourceKey)
    {
        var document = new RunDocument(
            Guid.NewGuid(), runId, Guid.NewGuid(), sourceKey, DocumentState.Pending,
            NormalizedTextHash: "normalized-text-hash-sentinel",
            RemoteDocumentId: "remote-document-id-sentinel",
            CreatedAt: createdAt, UpdatedAt: createdAt);
        await harness.RunStore.AddRunDocumentAsync(document);
        return document;
    }

    private static async Task<StateSnapshot> StateAsync(Harness harness, Guid runId)
    {
        var outcome = await harness.Service.HandleAsync(
            Request(ControlOperations.GetState, new GetStateRequest(runId.ToString())));
        Assert.Equal(ControlStatuses.Ok, outcome.Status);
        return Assert.IsType<StateSnapshot>(outcome.Payload);
    }

    private static async Task<StateSnapshot> WaitForStateAsync(Harness harness, Guid runId, string expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        StateSnapshot? snapshot = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            snapshot = await StateAsync(harness, runId);
            if (snapshot.ObservedState == expected)
            {
                return snapshot;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"The run never reported '{expected}'; last durable projection was '{snapshot?.ObservedState}'.");
        return snapshot!;
    }

    private static string DecodeCursor(string cursor)
    {
        var padded = cursor.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            _ => padded,
        };
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    private static string EncodeCursor(string version, long utcTicks, Guid documentId) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{version}.{utcTicks}.{documentId}"))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed record SeededBatch(Guid BatchId, IReadOnlyList<Guid> CandidateIds);

    /// <summary>
    /// Fixture store, fake extractor/API, real pipeline and the control service under test. Every seam that
    /// would touch Windows, a real pipe or a real credential is replaced by a fake.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private Harness(
            string directory, string databasePath, Guid manifestId, Guid rootId, SqliteStore store,
            SqliteRunStore runStore, SqliteControlStore control, SampleSetStore samples,
            BlockingApiClient api, ControlService service, ControlServiceOptions options)
        {
            Directory = directory;
            DatabasePath = databasePath;
            ManifestId = manifestId;
            RootId = rootId;
            Store = store;
            RunStore = runStore;
            Control = control;
            Samples = samples;
            Api = api;
            Service = service;
            Options = options;
        }

        public string Directory { get; }

        public string DatabasePath { get; }

        public Guid ManifestId { get; }

        public Guid RootId { get; }

        public SqliteStore Store { get; }

        public SqliteRunStore RunStore { get; }

        public SqliteControlStore Control { get; }

        public SampleSetStore Samples { get; }

        public BlockingApiClient Api { get; }

        public ControlService Service { get; }

        public ControlServiceOptions Options { get; }

        public static async Task<Harness> CreateAsync(
            int maxStartDocumentCount = 10_000,
            TimeSpan? drainTimeout = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"rag-control-service-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(directory);
            var databasePath = Path.Combine(directory, "store.sqlite");
            var manifestId = Guid.NewGuid();
            var rootId = Guid.NewGuid();

            var store = new SqliteStore(databasePath);
            await store.InitializeAsync();
            await store.AddSourceRootAsync(new SourceRoot(rootId, "seed", "/srv/seed", SeedTime));
            await store.CreateManifestAsync(new Manifest(manifestId, 1, ManifestState.Complete, SeedTime, SeedTime));
            for (var index = 0; index < 3; index++)
            {
                var candidateId = Guid.NewGuid();
                await store.AddCandidateAsync(new Candidate(
                    candidateId, manifestId, rootId, $"folder/doc-{candidateId:N}.txt", ".txt", 1024, SeedTime,
                    "eligible", MetadataFingerprint: $"fingerprint-{candidateId:N}"));
            }

            var samples = new SampleSetStore(databasePath);
            await samples.InitializeAsync();

            var runStore = new SqliteRunStore(store);
            var control = new SqliteControlStore(store);
            var api = new BlockingApiClient(new FakeHistoricalApiClient());
            var options = new ControlServiceOptions
            {
                EngineInstanceId = $"engine-instance-{Guid.NewGuid():N}",
                EngineVersion = "0.1.0",
                CollectionId = "legacy",
                DrainTimeout = drainTimeout ?? TimeSpan.FromSeconds(30),
                MaxStartDocumentCount = maxStartDocumentCount,
                DatabasePath = databasePath,
            };
            var pipeline = new HistoricalPipeline(
                new FakeTextExtractor(), api, runStore, new PipelineOptions(1, 64L * 1024 * 1024, 1_000));
            var service = new ControlService(store, control, runStore, pipeline, options);
            await service.StartAsync();

            return new Harness(directory, databasePath, manifestId, rootId, store, runStore, control, samples, api, service, options);
        }

        public async Task<SeededBatch> SeedApprovedBatchAsync(int memberCount, bool gatePassed = true)
        {
            var batchId = Guid.NewGuid();
            var members = new List<SampleMember>();
            for (var index = 0; index < memberCount; index++)
            {
                var candidateId = Guid.NewGuid();
                await Store.AddCandidateAsync(new Candidate(
                    candidateId, ManifestId, RootId, $"folder/batch-doc-{candidateId:N}.txt", ".txt", 2048, SeedTime,
                    "eligible", MetadataFingerprint: $"fingerprint-{candidateId:N}"));
                members.Add(new SampleMember(batchId, candidateId, "stratum-a", $"fingerprint-{candidateId:N}", index));
            }

            var set = new SampleSet(
                batchId, ManifestId, "v1", "pcg64", 42, memberCount, 1, "confidence-rules", gatePassed, true,
                SeedTime, "0.98");
            await Samples.SaveAsync(set, members);
            return new SeededBatch(batchId, members.Select(member => member.CandidateId).ToArray());
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Service.DisposeAsync();
                await Samples.DisposeAsync();
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

    /// <summary>The fake API with a gate on the first reserve, so a pipeline can be held mid-document.</summary>
    private sealed class BlockingApiClient : IHistoricalApiClient
    {
        private readonly IHistoricalApiClient _inner;
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blocked;

        public BlockingApiClient(IHistoricalApiClient inner) => _inner = inner;

        public Task Entered => _entered.Task;

        public int OperationCalls => ((FakeHistoricalApiClient)_inner).OperationCalls;

        public void Release() => _release.TrySetResult();

        public Task<ApiOperationResult> ReserveAsync(ApiOperation operation, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                _entered.TrySetResult();
                return AwaitAsync();

                async Task<ApiOperationResult> AwaitAsync()
                {
                    await _release.Task.WaitAsync(cancellationToken);
                    return await _inner.ReserveAsync(operation, cancellationToken);
                }
            }

            return _inner.ReserveAsync(operation, cancellationToken);
        }

        public Task<ApiOperationResult> UploadAsync(ApiOperation operation, CancellationToken cancellationToken = default)
            => _inner.UploadAsync(operation, cancellationToken);

        public Task<ApiOperationResult> CommitAsync(ApiOperation operation, CancellationToken cancellationToken = default)
            => _inner.CommitAsync(operation, cancellationToken);

        public Task<ApiOperationResult> PollAsync(ApiOperation operation, CancellationToken cancellationToken = default)
            => _inner.PollAsync(operation, cancellationToken);
    }
}
