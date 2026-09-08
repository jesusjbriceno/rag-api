namespace Rag.Companion.Protocol;

public sealed record LeaseResponse
{
    public required string JobId { get; init; }
    public required Guid CollectionId { get; init; }
    public required string LeaseId { get; init; }

    /// <summary>Unix UTC seconds at which the lease expires.</summary>
    public required long LeaseExpiresAt { get; init; }
    public required long NextSequence { get; init; }
    public required bool Snapshot { get; init; }
}
