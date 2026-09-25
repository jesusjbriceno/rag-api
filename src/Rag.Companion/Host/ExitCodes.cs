namespace Rag.Companion.Host;

/// <summary>Process exit codes surfaced by the companion CLI.</summary>
public static class ExitCodes
{
    /// <summary>The snapshot completed with no final per-file failures (or no work was leased).</summary>
    public const int Success = 0;

    /// <summary>The snapshot completed with at least one final per-file failure, or failed before completion.</summary>
    public const int TerminalFailure = 1;

    /// <summary>The command line was malformed.</summary>
    public const int Usage = 2;

    /// <summary>The configuration was missing, malformed, or invalid.</summary>
    public const int ConfigError = 3;
}
