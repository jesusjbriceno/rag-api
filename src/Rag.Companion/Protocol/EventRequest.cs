namespace Rag.Companion.Protocol;

public sealed record EventRequest
{
    public int ProtocolVersion { get; init; } = LeaseRequest.CurrentProtocolVersion;
    public required string LeaseId { get; init; }
    public required string EventId { get; init; }
    public required long Sequence { get; init; }
    public required CompanionState State { get; init; }
    public required long Processed { get; init; }
    public required long Failed { get; init; }
    public string? ErrorCode { get; init; }
}
