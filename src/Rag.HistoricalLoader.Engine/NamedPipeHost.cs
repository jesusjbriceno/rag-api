namespace Rag.HistoricalLoader.Engine;

/// <summary>
/// Current-user-ACL named-pipe host stub. The named-pipe transport is declared here
/// so the local contract stays versioned, but Unit 1c intentionally never opens it.
/// </summary>
public sealed class NamedPipeHost
{
    public const string PipeName = "rag-historical-loader";

    public bool IsListening => false;
}
