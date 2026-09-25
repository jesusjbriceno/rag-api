using System.Text.Json;
using Rag.Companion.Protocol;

namespace Rag.Companion.Tests;

public sealed class ProtocolDtoTests
{
    [Fact]
    public void Lease_request_serializes_to_fixed_shape()
    {
        Assert.Equal("{\"protocolVersion\":1}", JsonSerializer.Serialize(new LeaseRequest(), ProtocolJson.Options));
    }

    [Fact]
    public void Lease_request_rejects_unknown_field()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<LeaseRequest>("{\"protocolVersion\":1,\"sourcePath\":\"C:\\\\secret\"}", ProtocolJson.Options));
    }

    [Fact]
    public void Lease_response_deserializes_fixed_shape()
    {
        const string json = "{\"jobId\":\"job-1\",\"collectionId\":\"00000000-0000-0000-0000-000000000001\",\"leaseId\":\"lease-1\",\"leaseExpiresAt\":1735689660,\"nextSequence\":1,\"snapshot\":true}";

        var lease = JsonSerializer.Deserialize<LeaseResponse>(json, ProtocolJson.Options);

        Assert.NotNull(lease);
        Assert.Equal("job-1", lease.JobId);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000001"), lease.CollectionId);
        Assert.Equal(1735689660, lease.LeaseExpiresAt);
        Assert.Equal(1, lease.NextSequence);
        Assert.True(lease.Snapshot);
    }

    [Fact]
    public void Lease_response_rejects_unknown_field()
    {
        const string json = "{\"jobId\":\"job-1\",\"collectionId\":\"00000000-0000-0000-0000-000000000001\",\"leaseId\":\"lease-1\",\"leaseExpiresAt\":1735689660,\"nextSequence\":1,\"snapshot\":true,\"command\":\"ls\"}";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LeaseResponse>(json, ProtocolJson.Options));
    }

    [Fact]
    public void Event_request_serializes_running_state_lowercase()
    {
        var request = new EventRequest { LeaseId = "lease-1", EventId = "evt-1", Sequence = 1, State = CompanionState.Running, Processed = 0, Failed = 0 };

        Assert.Equal(
            "{\"protocolVersion\":1,\"leaseId\":\"lease-1\",\"eventId\":\"evt-1\",\"sequence\":1,\"state\":\"running\",\"processed\":0,\"failed\":0}",
            JsonSerializer.Serialize(request, ProtocolJson.Options));
    }

    [Fact]
    public void Event_request_omits_null_error_code()
    {
        var request = new EventRequest { LeaseId = "l", EventId = "e", Sequence = 2, State = CompanionState.Succeeded, Processed = 1, Failed = 0 };

        Assert.DoesNotContain("errorCode", JsonSerializer.Serialize(request, ProtocolJson.Options));
    }

    [Fact]
    public void Event_request_rejects_unknown_field()
    {
        const string json = "{\"protocolVersion\":1,\"leaseId\":\"l\",\"eventId\":\"e\",\"sequence\":1,\"state\":\"running\",\"processed\":0,\"failed\":0,\"content\":\"leak\"}";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EventRequest>(json, ProtocolJson.Options));
    }

    [Fact]
    public void Event_response_deserializes_replayed_flag()
    {
        var response = JsonSerializer.Deserialize<EventResponse>("{\"acceptedSequence\":1,\"state\":\"running\",\"replayed\":true}", ProtocolJson.Options);

        Assert.NotNull(response);
        Assert.True(response.Replayed);
        Assert.Equal(CompanionState.Running, response.State);
        Assert.Equal(1, response.AcceptedSequence);
    }
}
