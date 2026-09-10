using Rag.HistoricalLoader.Core.Benchmark;
using Rag.HistoricalLoader.Core.Data;
using Rag.HistoricalLoader.Core.Inventory;
using Rag.HistoricalLoader.Core.Sampling;

namespace Rag.HistoricalLoader.Engine;

public enum InventoryPhase
{
    Starting,
    Enumerating,
    Exporting,
    Completed,
    Failed,
}

public sealed record InventoryProgressEventArgs(InventoryPhase Phase, string Message, int? CandidateCount = null);

public sealed record InventoryRunRequest(
    string DatabasePath,
    string OutputDirectory,
    IReadOnlyList<SourceRoot> SourceRoots,
    long MaxCandidateBytes);

public sealed record InventoryRunResult(Guid ManifestId, InventoryScanResult Scan, ManifestExportResult Export);

public sealed record SelectSampleRequest(
    string DatabasePath,
    Guid ManifestId,
    int Budget,
    long Seed,
    int MinimumPerStratum,
    string ConfidenceCoverageRules,
    int SizeBandCount);

public sealed record SelectSampleResult(SampleSelectionResult Selection);

public sealed record BenchmarkExtractionRequest(
    string DatabasePath,
    Guid SampleSetId,
    ProbeOptions Options);

public sealed record BenchmarkExtractionResult(BenchmarkReport Report, int ObservationCount);

/// <summary>
/// Versioned local contract between the headless engine and a replaceable shell.
/// </summary>
public interface IHistoricalLoaderEngine
{
    event EventHandler<InventoryProgressEventArgs>? InventoryProgress;

    Task<InventoryRunResult> RunInventoryAsync(InventoryRunRequest request, CancellationToken cancellationToken = default);

    Task<SelectSampleResult> RunSelectSampleAsync(SelectSampleRequest request, CancellationToken cancellationToken = default);

    Task<BenchmarkExtractionResult> RunBenchmarkExtractionAsync(BenchmarkExtractionRequest request, CancellationToken cancellationToken = default);
}
