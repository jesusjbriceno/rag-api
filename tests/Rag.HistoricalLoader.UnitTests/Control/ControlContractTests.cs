using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Rag.HistoricalLoader.Contracts;

namespace Rag.HistoricalLoader.UnitTests.Control;

/// <summary>
/// Version-1 local control contract: envelope shape, fail-closed validation, stable wire values, and the
/// portability guard proving the Contracts assembly can only ever be a transport-neutral seam.
/// </summary>
public sealed class ControlContractTests
{
    private const string RequestId = "6f1d2c3b-4a59-4d8e-9f01-2a3b4c5d6e7f";
    private const string CommandId = "11111111-2222-4333-8444-555555555555";
    private const string BatchId = "99999999-8888-4777-8666-555555555555";
    private const string InstallationId = "installation-7f2a";
    private const string EngineInstanceId = "engine-instance-0c14";

    private static readonly string[] ForbiddenCoupling =
    [
        "Rag.HistoricalLoader.Core", "Rag.HistoricalLoader.Engine", "Sqlite", "Http", "WindowsBase",
        "PresentationFramework", "Credential", "System.Data", "System.IO", "System.Net", "System.Security",
        "System.Reflection", "System.Diagnostics",
    ];

    private static readonly DateTimeOffset Timestamp =
        DateTimeOffset.Parse("2026-09-14T10:11:12.1234567Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static ControlRequest Request(string operation, JsonElement? payload = null, int version = ControlProtocol.Version) =>
        new(version, RequestId, operation, payload);

    private static JsonElement Wire(object value) => JsonDocument.Parse(ControlWire.Serialize(value)).RootElement.Clone();

    private static ControlLimitsPayload Limits() =>
        new(ControlProtocol.Limits.MaxFrameBytes, ControlProtocol.Limits.MaxJsonDepth, ControlProtocol.Limits.MaxPageSize);

    private static HelloResult Hello() =>
        new(ControlProtocol.SupportedVersions, ControlCapabilities.All, InstallationId, EngineInstanceId, Limits());

    private static StateSnapshot Snapshot() =>
        new(RequestId, ControlRunStates.Running, ControlRunStates.Pausing,
            new InventoryTotals("manifest-1", "complete", 120, 4_096),
            new Dictionary<string, int> { [ControlDocumentStates.Loaded] = 3 },
            Timestamp, ControlErrorCodes.ResyncRequired, EngineInstanceId, "17");

    [Fact]
    public void Request_CarriesVersionRequestIdOperationAndTypedPayload()
    {
        var request = Request(ControlOperations.Start, Wire(new StartRequest(CommandId, BatchId, RequestId)));

        Assert.Equal(ControlProtocol.Version, request.ProtocolVersion);
        Assert.Equal(RequestId, request.RequestId);
        Assert.Null(ControlProtocol.Validate(request));

        var root = JsonDocument.Parse(ControlWire.Serialize(request)).RootElement;
        Assert.Equal(1, root.GetProperty("protocol_version").GetInt32());
        Assert.Equal(RequestId, root.GetProperty("request_id").GetString());
        Assert.Equal("start", root.GetProperty("operation").GetString());
        Assert.Equal(BatchId, root.GetProperty("payload").GetProperty("batch_id").GetString());

        // Identifiers stay opaque strings; the engine's path-bearing request types never reach the wire.
        Assert.Equal(
            [typeof(string), typeof(string), typeof(string), typeof(string)],
            new[]
            {
                typeof(ControlRequest).GetProperty(nameof(ControlRequest.RequestId))!.PropertyType,
                typeof(StartRequest).GetProperty(nameof(StartRequest.RunId))!.PropertyType,
                typeof(StartRequest).GetProperty(nameof(StartRequest.CommandId))!.PropertyType,
                typeof(CommandReceipt).GetProperty(nameof(CommandReceipt.ObservedState))!.PropertyType,
            });
    }

    [Fact]
    public void Response_EchoesVersionAndRequestId_WithTypedPayloadOrStableErrorCode()
    {
        var accepted = new ControlResponse<CommandReceipt>(ControlProtocol.Version, RequestId, ControlStatuses.Accepted,
            new CommandReceipt(CommandId, ControlOperations.Pause, RequestId, ControlRunStates.PauseRequested, ControlRunStates.Pausing));
        var rejected = new ControlResponse<StateSnapshot>(ControlProtocol.Version, RequestId, ControlStatuses.Rejected, ErrorCode: ControlErrorCodes.ResyncRequired);

        var acceptedRoot = JsonDocument.Parse(ControlWire.Serialize(accepted)).RootElement;
        Assert.Equal(1, acceptedRoot.GetProperty("protocol_version").GetInt32());
        Assert.Equal(RequestId, acceptedRoot.GetProperty("request_id").GetString());
        Assert.Equal("accepted", acceptedRoot.GetProperty("status").GetString());
        Assert.Equal("pausing", acceptedRoot.GetProperty("payload").GetProperty("observed_state").GetString());
        Assert.False(acceptedRoot.TryGetProperty("error_code", out _));

        var rejectedRoot = JsonDocument.Parse(ControlWire.Serialize(rejected)).RootElement;
        Assert.Equal("rejected", rejectedRoot.GetProperty("status").GetString());
        Assert.Equal("resync_required", rejectedRoot.GetProperty("error_code").GetString());
        Assert.Equal(
            ["error_code", "protocol_version", "request_id", "status"],
            rejectedRoot.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.DoesNotContain(
            rejectedRoot.EnumerateObject().Select(property => property.Name),
            name => name is "message" or "detail" or "exception" or "stack_trace");
        Assert.All(ControlStatuses.All, status => Assert.True(ControlStatuses.IsKnown(status)));
    }

    [Fact]
    public void UnsupportedOrFutureVersion_FailsClosedFirst_AndIsNeverDowngraded()
    {
        // Version 2 is a year from now; the request is otherwise deliberately malformed to prove ordering.
        Assert.Equal(ControlErrorCodes.UnsupportedVersion, ControlProtocol.Validate(new ControlRequest(2, string.Empty, "delete_everything")));
        Assert.Equal(ControlErrorCodes.UnsupportedVersion, ControlProtocol.Validate(new ControlRequest(0, RequestId, ControlOperations.Hello)));

        var hello = Hello();
        Assert.Equal([ControlProtocol.Version], hello.SupportedVersions.ToArray());
        Assert.Equal(ControlOperations.Hello, ControlOperations.All[0]);
    }

    [Theory]
    [InlineData("delete_everything")]
    [InlineData("START")]
    [InlineData("start ")]
    [InlineData("get__state")]
    [InlineData("get_state:")]
    [InlineData("hello\0")]
    [InlineData("")]
    public void UnknownOrNonCanonicalOperation_FailsClosed_WithUnknownOperation(string operation)
    {
        Assert.Equal(ControlErrorCodes.UnknownOperation, ControlProtocol.Validate(Request(operation, Wire(new StartRequest(CommandId, BatchId, RequestId)))));

        // Duplicates are impossible in the allowlist, so no operation can be dispatched twice by aliasing.
        Assert.Equal(ControlOperations.All.Count, ControlOperations.All.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("", ControlOperations.Start, true)]
    [InlineData("not-a-uuid", ControlOperations.Start, true)]
    [InlineData(RequestId, ControlOperations.Start, false)]
    [InlineData(RequestId, ControlOperations.GetEvents, false)]
    [InlineData(RequestId, ControlOperations.GetDocuments, false)]
    public void MalformedRequiredFields_FailClosed_WithMalformedRequest(string requestId, string operation, bool hasPayload)
    {
        var payload = hasPayload ? Wire(new StartRequest(CommandId, BatchId, RequestId)) : (JsonElement?)null;

        Assert.Equal(ControlErrorCodes.MalformedRequest, ControlProtocol.Validate(new ControlRequest(ControlProtocol.Version, requestId, operation, payload)));
    }

    [Fact]
    public void Hello_AdvertisesVersionsCapabilitiesIdentityAndLimits()
    {
        var hello = Hello();

        Assert.Null(ControlProtocol.Validate(Request(ControlOperations.Hello)));
        Assert.Single(hello.SupportedVersions);
        Assert.NotEmpty(hello.Capabilities);
        Assert.All(hello.Capabilities, capability => Assert.Contains(capability, ControlCapabilities.All));
        Assert.NotEqual(hello.InstallationId, hello.EngineInstanceId);
        Assert.Equal(1_048_576, hello.Limits.MaxFrameBytes);
        Assert.Equal(16, hello.Limits.MaxJsonDepth);
        Assert.Equal(100, hello.Limits.MaxPageSize);
    }

    [Fact]
    public void States_AreExplicitSnakeCaseStrings_NeverClrOrdinals()
    {
        Assert.Equal(15, ControlDocumentStates.All.Count);
        Assert.Equal("remote_pending", ControlDocumentStates.RemotePending);
        Assert.Equal("skipped_document_error", ControlDocumentStates.SkippedDocumentError);
        Assert.Equal(6, ControlRunStates.RunStates.Count);
        Assert.Equal("pausing", ControlRunStates.Pausing);
        Assert.Equal("pause_requested", ControlRunStates.PauseRequested);
        Assert.All(ControlDocumentStates.All.Concat(ControlRunStates.RunStates), state => Assert.Matches("^[a-z][a-z0-9_]*$", state));
        Assert.False(ControlDocumentStates.IsKnown("RemotePending"));
        Assert.False(ControlRunStates.IsKnown("7"));
    }

    [Fact]
    public void Timestamps_AreUtcIso8601_AndRoundTrip()
    {
        var json = ControlWire.Serialize(new EventSummary("17", Timestamp, RequestId, CommandId, "state_transition", "pending", "ok", 1));

        Assert.Contains("2026-09-14T10:11:12.1234567+00:00", json);
        var roundTripped = ControlWire.Deserialize<EventSummary>(json);
        Assert.NotNull(roundTripped);
        Assert.Equal(Timestamp, roundTripped!.Timestamp);
        Assert.Equal(TimeSpan.Zero, roundTripped.Timestamp.Offset);
    }

    [Fact]
    public void OptionalAdditiveFields_AreIgnorable()
    {
        var json = "{\"protocol_version\":1,\"request_id\":\"" + RequestId + "\",\"operation\":\"get_state\",\"future_field\":{\"nested\":[1,2]}}";

        var request = ControlWire.Deserialize<ControlRequest>(json);

        Assert.NotNull(request);
        Assert.Equal(ControlOperations.GetState, request!.Operation);
        Assert.Null(ControlProtocol.Validate(request));
    }

    [Theory]
    [InlineData(ControlErrorCodes.CommandConflict)]
    [InlineData(ControlErrorCodes.ResyncRequired)]
    [InlineData(ControlErrorCodes.PlatformNotSupported)]
    [InlineData(ControlErrorCodes.UnsupportedVersion)]
    public void StableErrorCodes_AreAllowlisted(string code)
    {
        Assert.True(ControlErrorCodes.IsKnown(code));
        Assert.Contains(code, ControlErrorCodes.All);
    }

    [Theory]
    [InlineData("Exception: access to the path 'C:\\Users\\operator' was denied")]
    [InlineData("leaked-client-secret")]
    [InlineData(null)]
    public void FreeFormDiagnostics_AreNeverStableErrorCodes(string? code)
    {
        Assert.False(ControlErrorCodes.IsKnown(code));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public void PageLimits_AboveOrBelowTheContractMaximum_AreRejectedBeforeDispatch(int limit)
    {
        Assert.Equal(ControlErrorCodes.MalformedRequest, ControlProtocol.ValidatePageLimit(limit));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void PageLimits_AtOrInsideTheContractMaximum_AreAccepted(int limit)
    {
        Assert.Null(ControlProtocol.ValidatePageLimit(limit));
    }

    [Fact]
    public void NoDtoFieldCanCarryContentPathsSecretsOrRawDiagnostics()
    {
        string[] forbiddenFragments =
        [
            "content", "path", "directory", "folder", "secret", "password", "token", "credential",
            "sourcekey", "source_key", "exception", "stack", "message", "text", "config",
        ];

        var offenders = typeof(ILoaderControlClient).Assembly.GetExportedTypes()
            .SelectMany(type => type.GetProperties().Select(property => $"{type.Name}.{property.Name}"))
            .Where(member => forbiddenFragments.Any(fragment => member.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        Assert.Empty(offenders);

        // A fully populated snapshot still serializes to allowlisted fields only.
        var json = ControlWire.Serialize(Snapshot());
        Assert.Contains("\"candidate_count\":120", json);
        Assert.Contains("\"completeness\":\"complete\"", json);
        Assert.DoesNotContain("C:\\\\", json);
        Assert.DoesNotContain("/home/", json);
    }

    [Fact]
    public void ContractsAssembly_HasNoForbiddenCoupling_AndOnlyOneTransportNeutralSeam()
    {
        var assembly = typeof(ILoaderControlClient).Assembly;

        var referenced = assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(referenced, reference => (reference.Name ?? string.Empty).StartsWith("Rag.", StringComparison.Ordinal));
        Assert.DoesNotContain(referenced, reference => ForbiddenCoupling.Any(coupling => (reference.Name ?? string.Empty).Contains(coupling, StringComparison.Ordinal)));

        var exported = assembly.GetExportedTypes();
        Assert.All(exported, type => Assert.StartsWith("Rag.HistoricalLoader.Contracts", type.Namespace, StringComparison.Ordinal));
        Assert.DoesNotContain(exported, type => type.IsEnum);
        Assert.Equal(["ILoaderControlClient"], exported.Where(type => type.IsInterface).Select(type => type.Name));
        Assert.Equal(
            ["GetDocumentsAsync", "GetEventsAsync", "GetStateAsync", "HelloAsync", "PauseAsync", "ResumeAsync", "StartAsync"],
            typeof(ILoaderControlClient).GetMethods().Select(method => method.Name).OrderBy(name => name, StringComparer.Ordinal));

        var surface = exported
            .SelectMany(type => type.GetProperties().Select(property => property.PropertyType)
                .Concat(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType).Prepend(method.ReturnType))))
            .Select(type => type.Assembly.GetName().Name ?? string.Empty)
            .Distinct()
            .ToArray();

        Assert.Contains("Rag.HistoricalLoader.Contracts", surface);
        Assert.DoesNotContain(surface, name => ForbiddenCoupling.Any(coupling => name.Contains(coupling, StringComparison.Ordinal)));
    }
}
