namespace Rag.Companion.Protocol;

public sealed record EventResponse
{
    public required long AcceptedSequence { get; init; }
    public required CompanionState State { get; init; }
    public required bool Replayed { get; init; }
}
