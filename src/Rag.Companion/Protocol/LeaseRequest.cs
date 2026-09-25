namespace Rag.Companion.Protocol;

public sealed record LeaseRequest
{
    public const int CurrentProtocolVersion = 1;

    public int ProtocolVersion { get; init; } = CurrentProtocolVersion;
}
