namespace Rag.HistoricalLoader.Core.Benchmark;

public enum BenchmarkOutcome
{
    Completed,
    Error,
    Timeout,
    Hang,
}

public sealed record BenchmarkCandidate(Guid CandidateId, string StratumKey, long SourceBytes, string? SourcePath = null);

public sealed record PhaseResult(long Bytes, string Outcome, string? ErrorCode);

public sealed record ResourceSnapshot(
    double CpuPercent,
    long WorkingSetBytes,
    long DiskIoBytes,
    int QueueGrowth,
    TimeSpan Elapsed);

public sealed record EnvironmentFingerprint(
    string MeasurementMethod,
    TimeSpan SamplingInterval,
    string Machine,
    string OperatingSystem,
    string RuntimeVersion,
    string AdapterName,
    string AdapterVersion,
    string Configuration);

public sealed record BenchmarkObservation(
    Guid Id,
    Guid SampleSetId,
    Guid CandidateId,
    string StratumKey,
    DateTimeOffset StartedAt,
    TimeSpan DiscoveryDuration,
    TimeSpan SnapshotDuration,
    TimeSpan ExtractionDuration,
    TimeSpan StagingDuration,
    long SourceBytes,
    long NormalizedTextBytes,
    BenchmarkOutcome Outcome,
    string? ErrorCode,
    ResourceSnapshot Resource);

public sealed record WeightedFullCorpusRange(TimeSpan LowerBound, TimeSpan UpperBound, IReadOnlyList<string> Assumptions);

public sealed record BenchmarkReport(
    Guid SampleSetId,
    EnvironmentFingerprint Environment,
    int Count,
    TimeSpan MedianExtraction,
    TimeSpan? P95Extraction,
    TimeSpan MinExtraction,
    TimeSpan MaxExtraction,
    double DocumentsPerHour,
    double SourceGbPerHour,
    double NormalizedGbPerHour,
    int Errors,
    int Timeouts,
    int Hangs,
    double ErrorRate,
    double TimeoutHangRate,
    ResourceSnapshot PeakResource,
    int PeakQueueGrowth,
    bool SustainedReliabilitySatisfied,
    string? ReliabilityBlockReason,
    WeightedFullCorpusRange? FullCorpusRange,
    IReadOnlyList<string> Limitations);

public sealed record ProbeRun(IReadOnlyList<BenchmarkObservation> Observations, TimeSpan Elapsed);
