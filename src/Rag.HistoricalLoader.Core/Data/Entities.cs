namespace Rag.HistoricalLoader.Core.Data;

public enum ManifestState
{
    Scanning = 0,
    Complete = 1,
}

public sealed record LoaderInstallation(Guid Id, int SchemaVersion, DateTimeOffset CreatedAt);

public sealed record SourceRoot(
    Guid Id,
    string Label,
    string CanonicalPath,
    DateTimeOffset CreatedAt,
    bool ReparseTraversalDisabled = true);

public sealed record Manifest(
    Guid Id,
    int ScanVersion,
    ManifestState State,
    DateTimeOffset StartTime,
    DateTimeOffset? EndTime = null);

public sealed record Candidate(
    Guid Id,
    Guid ManifestId,
    Guid RootId,
    string RelativePath,
    string Extension,
    long ByteSize,
    DateTimeOffset LastWriteTime,
    string EligibilityCode,
    string? DiscoveryErrorCode = null,
    string? MetadataFingerprint = null);

public sealed record AuditEvent(
    long Id,
    DateTimeOffset UtcTimestamp,
    Guid? RunId,
    Guid? CandidateId,
    string Action,
    string? StateTransition,
    string? OutcomeCode,
    int AttemptNumber,
    string? Measurements);
