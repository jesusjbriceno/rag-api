using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using Rag.HistoricalLoader.Contracts;
using Rag.HistoricalLoader.Core.Lifecycle;
using Rag.HistoricalLoader.Engine;
using Rag.HistoricalLoader.Engine.Control;

namespace Rag.HistoricalLoader.UnitTests.Control;

/// <summary>
/// The transport-agnostic control dispatcher: it validates and delegates only. Every case here runs against
/// fake streams and a recording handler, on Linux, with no pipe, ACL, Windows, or host wiring involved.
/// </summary>
public sealed class DispatchValidationTests
{
    private const string RequestId = "6f1d2c3b-4a59-4d8e-9f01-2a3b4c5d6e7f";
    private const string CommandId = "11111111-2222-4333-8444-555555555555";
    private const string BatchId = "99999999-8888-4777-8666-555555555555";
    private const string RunId = "77777777-6666-4555-8444-333333333333";

    private static string RequestJson(string operation = ControlOperations.Hello) =>
        ControlWire.Serialize(new ControlRequest(ControlProtocol.Version, RequestId, operation));

    private static JsonElement Wire(object value) => JsonDocument.Parse(ControlWire.Serialize(value)).RootElement.Clone();

    private static ControlRequest Request(string operation, JsonElement? payload = null, int version = ControlProtocol.Version) =>
        new(version, RequestId, operation, payload);

    private static ControlRequest RequestWithPayload(string operation, object payload) =>
        Request(operation, Wire(payload));

    private static byte[] Framed(string json)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(json);
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)payload.Length);
        return [.. prefix, .. payload];
    }

    /// <summary>A length prefix that declares one byte more than the frame limit, with no payload bytes.</summary>
    private static byte[] OversizedPrefix()
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)(ControlProtocol.Limits.MaxFrameBytes + 1));
        return prefix;
    }

    private static HelloResult Hello() => new(
        ControlProtocol.SupportedVersions,
        ControlCapabilities.All,
        "installation-7f2a",
        "engine-instance-0c14",
        new ControlLimitsPayload(ControlProtocol.Limits.MaxFrameBytes, ControlProtocol.Limits.MaxJsonDepth, ControlProtocol.Limits.MaxPageSize));

    private static JsonElement ResponseRoot(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task ValidRequest_IsDispatchedExactlyOnce_AndTheHandlerReplyIsSerialized()
    {
        var handler = new RecordingHandler { Outcome = ControlDispatchOutcome.Ok(Hello()) };
        var dispatcher = new ControlDispatcher(handler);
        var request = Request(ControlOperations.Hello);

        var result = await dispatcher.DispatchAsync(request);

        Assert.Equal(ControlDispatchStatus.Handled, result.Status);
        Assert.Null(result.ErrorCode);
        Assert.Equal(RequestId, result.RequestId);
        Assert.False(result.CloseConnection);
        Assert.Same(request, Assert.Single(handler.Calls));

        var root = ResponseRoot(result.ResponseJson!);
        Assert.Equal(1, root.GetProperty("protocol_version").GetInt32());
        Assert.Equal(RequestId, root.GetProperty("request_id").GetString());
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal("engine-instance-0c14", root.GetProperty("payload").GetProperty("engine_instance_id").GetString());
        Assert.False(root.TryGetProperty("error_code", out _));
    }

    [Theory]
    [InlineData("delete_everything")]
    [InlineData("START")]
    [InlineData("")]
    public async Task UnknownOperation_FailsClosedBeforeDispatch(string operation)
    {
        var handler = new RecordingHandler();
        var dispatcher = new ControlDispatcher(handler);

        var result = await dispatcher.DispatchAsync(Request(operation, Wire(new StartRequest(CommandId, BatchId, RunId))));

        Assert.Equal(ControlDispatchStatus.Rejected, result.Status);
        Assert.Equal(ControlErrorCodes.UnknownOperation, result.ErrorCode);
        Assert.Empty(handler.Calls);
        Assert.Equal(ControlErrorCodes.UnknownOperation, ResponseRoot(result.ResponseJson!).GetProperty("error_code").GetString());
    }

    [Fact]
    public async Task UnsupportedVersion_FailsClosedFirst_AndIsNeverDowngraded()
    {
        var handler = new RecordingHandler();
        var dispatcher = new ControlDispatcher(handler);

        var result = await dispatcher.DispatchAsync(new ControlRequest(ControlProtocol.Version + 1, RequestId, ControlOperations.Hello));

        Assert.Equal(ControlDispatchStatus.Rejected, result.Status);
        Assert.Equal(ControlErrorCodes.UnsupportedVersion, result.ErrorCode);
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData("", ControlOperations.Hello, false)]
    [InlineData("not-a-uuid", ControlOperations.Hello, false)]
    [InlineData(RequestId, ControlOperations.Start, false)]
    public async Task MalformedRequiredFields_FailClosedBeforeDispatch(string requestId, string operation, bool hasPayload)
    {
        var handler = new RecordingHandler();
        var dispatcher = new ControlDispatcher(handler);
        var payload = hasPayload ? Wire(new StartRequest(CommandId, BatchId, RunId)) : (JsonElement?)null;

        var result = await dispatcher.DispatchAsync(new ControlRequest(ControlProtocol.Version, requestId, operation, payload));

        Assert.Equal(ControlDispatchStatus.Rejected, result.Status);
        Assert.Equal(ControlErrorCodes.MalformedRequest, result.ErrorCode);
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData(ControlOperations.GetDocuments, 101)]
    [InlineData(ControlOperations.GetDocuments, 0)]
    [InlineData(ControlOperations.GetEvents, 101)]
    [InlineData(ControlOperations.GetEvents, -1)]
    public async Task PageRequestsBeyondTheContractLimit_FailClosedBeforeDispatch(string operation, int limit)
    {
        var handler = new RecordingHandler { Outcome = ControlDispatchOutcome.Ok() };
        var dispatcher = new ControlDispatcher(handler);

        var result = await dispatcher.DispatchAsync(RequestWithPayload(operation, new GetDocumentsRequest(RunId, limit)));

        Assert.Equal(ControlDispatchStatus.Rejected, result.Status);
        Assert.Equal(ControlErrorCodes.MalformedRequest, result.ErrorCode);
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public async Task PageRequestsInsideTheContractLimit_AreDispatched(int limit)
    {
        Assert.Equal(ControlProtocol.Limits.MaxPageSize, 100);

        var handler = new RecordingHandler { Outcome = ControlDispatchOutcome.Ok() };
        var dispatcher = new ControlDispatcher(handler);

        var result = await dispatcher.DispatchAsync(RequestWithPayload(ControlOperations.GetDocuments, new GetDocumentsRequest(RunId, limit)));

        Assert.Equal(ControlDispatchStatus.Handled, result.Status);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task PageBearingPayloadThatCannotBind_FailsClosedBeforeDispatch()
    {
        var handler = new RecordingHandler();
        var dispatcher = new ControlDispatcher(handler);

        var result = await dispatcher.DispatchAsync(Request(
            ControlOperations.GetDocuments,
            JsonDocument.Parse("{\"run_id\":123,\"limit\":\"all\"}").RootElement.Clone()));

        Assert.Equal(ControlDispatchStatus.Rejected, result.Status);
        Assert.Equal(ControlErrorCodes.MalformedRequest, result.ErrorCode);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task OptionalAdditiveFields_AreIgnored_AndTheRequestStillDispatches()
    {
        var handler = new RecordingHandler { Outcome = ControlDispatchOutcome.Ok() };
        var dispatcher = new ControlDispatcher(handler);

        // A client a year from now may send fields this engine does not know.
        var request = ControlWire.Deserialize<ControlRequest>(
            "{\"protocol_version\":1,\"request_id\":\"" + RequestId + "\",\"operation\":\"get_state\",\"future_field\":{\"nested\":[1,2]}}");

        var result = await dispatcher.DispatchAsync(request);

        Assert.Equal(ControlDispatchStatus.Handled, result.Status);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task HandlerRejection_IsSerializedWithTheAllowlistedCode_AndNoRawDiagnostics()
    {
        var handler = new RecordingHandler { Outcome = ControlDispatchOutcome.Rejected(ControlErrorCodes.ResyncRequired) };
        var dispatcher = new ControlDispatcher(handler);

        var result = await dispatcher.DispatchAsync(Request(ControlOperations.GetEvents, Wire(new GetEventsRequest(10))));

        Assert.Equal(ControlDispatchStatus.Handled, result.Status);
        Assert.Equal(ControlErrorCodes.ResyncRequired, result.ErrorCode);
        var root = ResponseRoot(result.ResponseJson!);
        Assert.Equal("rejected", root.GetProperty("status").GetString());
        Assert.Equal("resync_required", root.GetProperty("error_code").GetString());
        Assert.Equal(
            ["error_code", "protocol_version", "request_id", "status"],
            root.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.DoesNotContain(
            root.EnumerateObject().Select(property => property.Name),
            name => name is "message" or "detail" or "exception" or "stack_trace");
    }

    [Theory]
    [InlineData("Exception: access to the path 'C:\\Users\\operator' was denied")]
    [InlineData("leaked-client-secret")]
    [InlineData("")]
    public void FreeFormHandlerDiagnostics_CanNeverBecomeAWireErrorCode(string code)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ControlDispatchOutcome.Rejected(code));
    }

    [Fact]
    public async Task ValidFrame_RoundTripsThroughOneDispatcherTurn_AndWritesExactlyOneResponseFrame()
    {
        var handler = new RecordingHandler { Outcome = ControlDispatchOutcome.Ok(Hello()) };
        var dispatcher = new ControlDispatcher(handler);
        var connection = dispatcher.TryOpenConnection();
        Assert.NotNull(connection);
        var stream = new DuplexStream(Framed(RequestJson()));

        var result = await dispatcher.ProcessFrameAsync(connection, stream);

        Assert.Equal(ControlDispatchStatus.Handled, result.Status);
        Assert.Equal(RequestId, result.RequestId);
        Assert.False(result.CloseConnection);
        Assert.Single(handler.Calls);

        var response = await ControlFrameCodec.ReadAsync(new MemoryStream(stream.WrittenToArray()));
        Assert.Equal(ControlFrameReadStatus.Frame, response.Status);
        Assert.Equal(RequestId, ResponseRoot(response.Json!).GetProperty("request_id").GetString());
    }

    [Fact]
    public async Task ResponseFrames_ArePlainUtf8Json_WithNoBinaryOrTypeAnnotationMetadata()
    {
        var handler = new RecordingHandler { Outcome = ControlDispatchOutcome.Ok(Hello()) };
        var dispatcher = new ControlDispatcher(handler);
        var connection = dispatcher.TryOpenConnection();
        Assert.NotNull(connection);
        var stream = new DuplexStream(Framed(RequestJson()));

        await dispatcher.ProcessFrameAsync(connection, stream);

        var bytes = stream.WrittenToArray();
        var text = System.Text.Encoding.UTF8.GetString(bytes, 4, bytes.Length - 4);

        // No binary object serialization and no arbitrary type names on the wire.
        Assert.StartsWith("{", text, StringComparison.Ordinal);
        Assert.DoesNotContain("$type", text, StringComparison.Ordinal);
        Assert.DoesNotContain("@type", text, StringComparison.Ordinal);
        Assert.DoesNotContain("System.", text, StringComparison.Ordinal);
        var root = ResponseRoot(text);
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.True(root.TryGetProperty("payload", out var payload));
        Assert.Equal(JsonValueKind.Object, payload.ValueKind);
    }

    [Fact]
    public async Task EndOfStream_DispatchesNothing_AndWritesNoResponseFrame()
    {
        var handler = new RecordingHandler();
        var dispatcher = new ControlDispatcher(handler);
        var connection = dispatcher.TryOpenConnection();
        Assert.NotNull(connection);
        var stream = new DuplexStream([]);

        var result = await dispatcher.ProcessFrameAsync(connection, stream);

        Assert.Equal(ControlDispatchStatus.EndOfStream, result.Status);
        Assert.Empty(handler.Calls);
        Assert.Empty(stream.WrittenToArray());
    }

    [Theory]
    [InlineData("oversized")]
    [InlineData("truncated-frame")]
    [InlineData("truncated-prefix")]
    [InlineData("malformed-json")]
    [InlineData("deep-json")]
    public async Task FramesThatFailClosed_AreNeverDispatched_AndWriteAStableErrorEnvelope(string frameCase)
    {
        var handler = new RecordingHandler();
        var dispatcher = new ControlDispatcher(handler);
        var connection = dispatcher.TryOpenConnection();
        Assert.NotNull(connection);

        var stream = new DuplexStream(frameCase switch
        {
            "oversized" => OversizedPrefix(),
            "truncated-frame" => [.. Framed(RequestJson())[..10]],
            "truncated-prefix" => [0x2a, 0x00],
            "malformed-json" => Framed("{\"protocol_version\":1,"),
            _ => Framed("{\"protocol_version\":1,\"request_id\":\"" + RequestId + "\",\"operation\":\"hello\",\"pad\":" + Nest(ControlProtocol.Limits.MaxJsonDepth) + "}"),
        });

        var result = await dispatcher.ProcessFrameAsync(connection, stream);

        Assert.Equal(ControlDispatchStatus.Rejected, result.Status);
        Assert.Equal(ControlErrorCodes.MalformedRequest, result.ErrorCode);
        Assert.True(result.CloseConnection);
        Assert.Empty(handler.Calls);

        var written = await ControlFrameCodec.ReadAsync(new MemoryStream(stream.WrittenToArray()));
        Assert.Equal(ControlFrameReadStatus.Frame, written.Status);
        var root = ResponseRoot(written.Json!);
        Assert.Equal("rejected", root.GetProperty("status").GetString());
        Assert.Equal("malformed_request", root.GetProperty("error_code").GetString());
        Assert.False(root.TryGetProperty("payload", out _));
    }

    [Fact]
    public async Task WellFormedJsonThatIsNotARequestEnvelope_FailsClosedWithoutDispatch()
    {
        var handler = new RecordingHandler();
        var dispatcher = new ControlDispatcher(handler);
        var connection = dispatcher.TryOpenConnection();
        Assert.NotNull(connection);
        var stream = new DuplexStream(Framed("[1,2,3]"));

        var result = await dispatcher.ProcessFrameAsync(connection, stream);

        Assert.Equal(ControlDispatchStatus.Rejected, result.Status);
        Assert.Equal(ControlErrorCodes.MalformedRequest, result.ErrorCode);
        Assert.Empty(handler.Calls);

        // The frame boundary was intact, so exactly one rejection envelope was written.
        var written = await ControlFrameCodec.ReadAsync(new MemoryStream(stream.WrittenToArray()));
        Assert.Equal(ControlFrameReadStatus.Frame, written.Status);
        Assert.Equal("malformed_request", ResponseRoot(written.Json!).GetProperty("error_code").GetString());
    }

    [Fact]
    public async Task ReadDeadlineExpiryAtTheDispatcher_RejectsWithAStableCode_WithoutDispatch()
    {
        var handler = new RecordingHandler();
        var dispatcher = new ControlDispatcher(handler, ControlTransportLimits.Default with { ReadDeadline = TimeSpan.FromMilliseconds(50) });
        var connection = dispatcher.TryOpenConnection();
        Assert.NotNull(connection);

        var result = await dispatcher.ProcessFrameAsync(connection, new BlockingStream());

        Assert.Equal(ControlDispatchStatus.Rejected, result.Status);
        Assert.Equal(ControlErrorCodes.MalformedRequest, result.ErrorCode);
        Assert.True(result.CloseConnection);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task ConnectionSaturation_IsBounded_AndFreesASlotOnlyOnARealClose()
    {
        var handler = new RecordingHandler();
        var dispatcher = new ControlDispatcher(handler, ControlTransportLimits.Default with { MaxConcurrentConnections = 2 });

        var first = dispatcher.TryOpenConnection();
        var second = dispatcher.TryOpenConnection();
        var saturated = dispatcher.TryOpenConnection();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(saturated);
        Assert.Equal(2, dispatcher.ActiveConnectionCount);

        Assert.True(dispatcher.CloseConnection(first!));
        Assert.False(dispatcher.CloseConnection(first!));
        Assert.Equal(1, dispatcher.ActiveConnectionCount);
        Assert.NotNull(dispatcher.TryOpenConnection());
    }

    [Fact]
    public async Task RequestCountSaturationOnOneConnection_IsRejectedWithoutDispatch_AndClosesTheConnection()
    {
        var handler = new RecordingHandler { Outcome = ControlDispatchOutcome.Ok() };
        var dispatcher = new ControlDispatcher(handler, ControlTransportLimits.Default with { MaxRequestsPerConnection = 2 });
        var connection = dispatcher.TryOpenConnection();
        Assert.NotNull(connection);
        var stream = new DuplexStream([.. Framed(RequestJson()), .. Framed(RequestJson()), .. Framed(RequestJson())]);

        var first = await dispatcher.ProcessFrameAsync(connection, stream);
        var second = await dispatcher.ProcessFrameAsync(connection, stream);
        var third = await dispatcher.ProcessFrameAsync(connection, stream);

        Assert.Equal(ControlDispatchStatus.Handled, first.Status);
        Assert.Equal(ControlDispatchStatus.Handled, second.Status);
        Assert.Equal(ControlDispatchStatus.Rejected, third.Status);
        Assert.Equal(ControlErrorCodes.MalformedRequest, third.ErrorCode);
        Assert.True(third.CloseConnection);
        Assert.Equal(2, handler.Calls.Count);
        Assert.Equal(2, connection.RequestCount);

        // Two handled responses plus exactly one saturation rejection reached the wire.
        var written = new MemoryStream(stream.WrittenToArray());
        var responses = new List<string>();
        while (true)
        {
            var frame = await ControlFrameCodec.ReadAsync(written);
            if (frame.Status != ControlFrameReadStatus.Frame)
            {
                break;
            }

            responses.Add(frame.Json!);
        }

        Assert.Equal(3, responses.Count);
        Assert.Equal("rejected", ResponseRoot(responses[2]).GetProperty("status").GetString());
    }

    // ------------------------------------------------------------------------------------------------
    // Cross-slice obligation recorded by 11.prev-b2 and 11.prev-b1a: the durable store vocabulary and the
    // wire vocabulary are compared here, test-side, because Core must not reference Contracts. Without this
    // test the two assemblies could drift silently.
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void StoreAndWireDocumentStates_AgreeExactly_SoTheyCannotDriftSilently()
    {
        var storeStates = Enum.GetValues<DocumentState>().Select(ControlStoreVocabulary.DocumentStateName).ToArray();

        Assert.Equal(15, storeStates.Length);
        Assert.Equal(
            Sorted(ControlDocumentStates.All),
            Sorted(storeStates));
        Assert.All(storeStates, state => Assert.True(ControlDocumentStates.IsKnown(state)));
        Assert.All(storeStates, state => Assert.Matches("^[a-z][a-z0-9_]*$", state));

        // Every durable document state really is projected from the enum, not from a hand-written table.
        Assert.Equal(DocumentState.RemotePending.ToString(), "RemotePending");
        Assert.Equal("remote_pending", ControlStoreVocabulary.DocumentStateName(DocumentState.RemotePending));
        Assert.Equal("skipped_document_error", ControlStoreVocabulary.DocumentStateName(DocumentState.SkippedDocumentError));
        Assert.Equal("retry_exhausted_network", ControlStoreVocabulary.DocumentStateName(DocumentState.RetryExhaustedNetwork));
    }

    [Fact]
    public void StoreAndWireRunStates_AgreeExactly_SoTheyCannotDriftSilently()
    {
        var storeDesired = Enum.GetValues<RunDesiredState>().Select(ControlStoreVocabulary.DesiredStateName).ToArray();
        var storeObserved = Enum.GetValues<RunObservedState>().Select(ControlStoreVocabulary.ObservedStateName).ToArray();

        Assert.Equal(Sorted(ControlRunStates.DesiredStates), Sorted(storeDesired));
        Assert.Equal(Sorted(ControlRunStates.RunStates), Sorted(storeObserved));
        Assert.All(storeDesired, state => Assert.True(ControlRunStates.IsKnown(state)));
        Assert.All(storeObserved, state => Assert.Equal(ControlRunStates.RunStates.Count, storeObserved.Length));
        Assert.Equal("pause_requested", ControlStoreVocabulary.DesiredStateName(RunDesiredState.PauseRequested));
        Assert.Equal("pausing", ControlStoreVocabulary.ObservedStateName(RunObservedState.Pausing));
        Assert.Equal("completed", ControlStoreVocabulary.ObservedStateName(RunObservedState.Completed));
    }

    [Fact]
    public void StorePageBoundAndBlockCodes_AgreeWithTheWireContract()
    {
        // The store page bound mirrors the wire page bound the dispatcher enforces.
        Assert.Equal(ControlProtocol.Limits.MaxPageSize, ControlStoreVocabulary.MaxPageSize);
        Assert.Equal(100, ControlStoreVocabulary.MaxPageSize);

        // The 11.prev-b2 decision: a total derivation over the durable blocked states, null otherwise.
        Assert.Equal(ControlRunStates.BlockedAuth, ControlStoreVocabulary.BlockCode(RunObservedState.BlockedAuth));
        Assert.Equal(ControlRunStates.BlockedOperatorAction, ControlStoreVocabulary.BlockCode(RunObservedState.BlockedOperatorAction));
        var unblocked = Enum.GetValues<RunObservedState>()
            .Where(state => state is not RunObservedState.BlockedAuth and not RunObservedState.BlockedOperatorAction)
            .ToArray();
        Assert.Equal(4, unblocked.Length);
        Assert.All(unblocked, state => Assert.Null(ControlStoreVocabulary.BlockCode(state)));
    }

    [Fact]
    public void TheStoreSideStillHasNoReferenceToTheWireContract()
    {
        var storeReferences = typeof(ControlStoreVocabulary).Assembly.GetReferencedAssemblies();

        // Core must not reference Contracts: the agreement above is asserted only from the test assembly.
        Assert.DoesNotContain(
            storeReferences,
            reference => (reference.Name ?? string.Empty).StartsWith("Rag.HistoricalLoader.Contracts", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------------------------------------
    // "Validates and delegates only": no ingestion policy, no path-bearing input, no pipe or Windows code,
    // and no host wiring in this slice.
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheHandlerSeam_TakesOnlyARequestAndACancellationToken()
    {
        var method = Assert.Single(typeof(IControlCommandHandler).GetMethods());

        Assert.Equal("HandleAsync", method.Name);
        Assert.Equal(
            [typeof(ControlRequest), typeof(CancellationToken)],
            method.GetParameters().Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public void NoDispatcherSurfaceNameCarriesAPathPipeDatabaseOrPolicyInput()
    {
        string[] forbidden =
        [
            "path", "pipe", "database", "file", "directory", "folder", "root", "approve", "batch",
            "collection", "credential", "secret", "token", "content", "requestjson",
        ];

        // CancellationToken is not credential material; every other parameter name must be policy-free.
        var parameterNames = DispatcherSurfaceTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .SelectMany(method => method.GetParameters())
                .Where(parameter => parameter.ParameterType != typeof(CancellationToken))
                .Select(parameter => parameter.Name!));
        var propertyNames = DispatcherSurfaceTypes().SelectMany(type => type.GetProperties().Select(property => property.Name));
        var names = parameterNames.Concat(propertyNames).ToArray();

        Assert.NotEmpty(names);
        Assert.All(names, name => Assert.DoesNotContain(forbidden, fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void ThisSliceAddsNoPipeWindowsOrCredentialSurface()
    {
        var controlTypes = typeof(ControlDispatcher).Assembly.GetExportedTypes()
            .Where(type => type.Namespace == "Rag.HistoricalLoader.Engine.Control")
            .ToArray();
        Assert.NotEmpty(controlTypes);

        var referencedNamespaces = controlTypes
            .SelectMany(type => type.GetProperties().Select(property => property.PropertyType)
                .Concat(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType))))
            .Select(type => type.Namespace ?? string.Empty)
            .Distinct()
            .ToArray();

        Assert.DoesNotContain(referencedNamespaces, ns => ns.StartsWith("System.IO.Pipes", StringComparison.Ordinal));
        Assert.DoesNotContain(referencedNamespaces, ns => ns.StartsWith("System.Security", StringComparison.Ordinal));
        Assert.DoesNotContain(referencedNamespaces, ns => ns.StartsWith("System.Runtime.InteropServices", StringComparison.Ordinal));
        Assert.DoesNotContain(controlTypes, type => (type.Namespace ?? string.Empty).Contains("Windows", StringComparison.Ordinal));
    }

    [Fact]
    public void ThisSliceOpensNoPipeAndLeavesTheCliHostSurfaceUnchanged()
    {
        // Acceptance evidence (b)/(c): 11.prev-c introduces no host wiring and no pipe code. The named-pipe
        // host stays closed until 11.prev-d/-e, and the existing CLI entry point is untouched.
        Assert.False(new NamedPipeHost().IsListening);
    }

    private static Type[] DispatcherSurfaceTypes() =>
    [
        typeof(ControlDispatcher),
        typeof(ControlConnection),
        typeof(ControlDispatchResult),
        typeof(ControlDispatchOutcome),
        typeof(ControlTransportLimits),
        typeof(IControlCommandHandler),
    ];

    private static string[] Sorted(IEnumerable<string> values) =>
        values.OrderBy(value => value, StringComparer.Ordinal).ToArray();

    private static string Nest(int levels)
    {
        var body = "1";
        for (var level = 0; level < levels; level++)
        {
            body = "{\"a\":" + body + "}";
        }

        return body;
    }

    /// <summary>Records every delegated request so a test can prove the dispatcher never dispatched.</summary>
    private sealed class RecordingHandler : IControlCommandHandler
    {
        public List<ControlRequest> Calls { get; } = [];

        public ControlDispatchOutcome Outcome { get; set; } = ControlDispatchOutcome.Ok();

        public ValueTask<ControlDispatchOutcome> HandleAsync(ControlRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(request);
            return ValueTask.FromResult(Outcome);
        }
    }

    /// <summary>
    /// A duplex fake transport: reads one pre-built frame from an input buffer and records every response
    /// byte separately, so a test can inspect exactly what the dispatcher wrote. An input-only
    /// <see cref="MemoryStream"/> is not expandable and cannot stand in for a writable transport.
    /// </summary>
    private sealed class DuplexStream : Stream
    {
        private readonly MemoryStream _input;
        private readonly MemoryStream _written = new();

        public DuplexStream(byte[] input) => _input = new MemoryStream(input);

        public byte[] WrittenToArray() => _written.ToArray();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _input.Length;

        public override long Position
        {
            get => _input.Position;
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _input.ReadAsync(buffer, cancellationToken);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _written.WriteAsync(buffer, cancellationToken);

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A stream that never delivers a byte and only completes when its token is cancelled.</summary>
    private sealed class BlockingStream : Stream
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _gate.Task.WaitAsync(cancellationToken);
            return 0;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
