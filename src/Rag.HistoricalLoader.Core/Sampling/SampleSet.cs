using Rag.HistoricalLoader.Core.Data;

namespace Rag.HistoricalLoader.Core.Sampling;

public sealed record SampleSelectionRequest(
    Guid ManifestId,
    ManifestState ManifestState,
    int Budget,
    long Seed,
    int MinimumPerStratum,
    string PrngAlgorithm,
    string ConfidenceCoverageRules,
    int SizeBandCount = 4);

public sealed record SizeBandRange(int Index, long MinBytes, long MaxBytes, int Count);

public sealed record SampleAllocation(string StratumKey, string Format, string SizeBand, int Population, int Allocation, double Weight);

public sealed record StratumCoverage(
    string StratumKey, string Format, string SizeBand, int Population,
    int Allocation, double Weight, bool Covered, string? UncoveredReason);

public sealed record SampleSet(
    Guid Id, Guid ManifestId, string AlgorithmVersion, string PrngAlgorithm, long Seed,
    int Budget, int MinimumPerStratum, string ConfidenceCoverageRules,
    bool GatePassed, bool Representative, DateTimeOffset CreatedAt, string CoverageJson);

public sealed record SampleMember(
    Guid SampleSetId, Guid CandidateId, string StratumKey, string MetadataFingerprint, int SelectedRank);

public sealed record SampleSelectionResult(
    bool GatePassed, string? GateFailureReason, bool Representative,
    SampleSet SampleSet, IReadOnlyList<SampleMember> Members,
    IReadOnlyList<StratumCoverage> Coverage, IReadOnlyList<SizeBandRange> SizeBands,
    IReadOnlyList<string> Limitations);
