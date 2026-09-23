using Rag.HistoricalLoader.Core.Benchmark;
using Rag.HistoricalLoader.Core.Classification;
using Rag.HistoricalLoader.Core.Inventory;
using Rag.HistoricalLoader.Core.Persistence;
using Rag.HistoricalLoader.Core.Sampling;

namespace Rag.HistoricalLoader.Engine;

public sealed class HistoricalLoaderEngine : IHistoricalLoaderEngine
{
    public event EventHandler<InventoryProgressEventArgs>? InventoryProgress;

    public async Task<InventoryRunResult> RunInventoryAsync(InventoryRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Emit(InventoryPhase.Starting, "Starting inventory scan.");

        InventoryScanResult scan;
        await using (var store = new SqliteStore(request.DatabasePath))
        {
            await store.InitializeAsync(cancellationToken);
            var enumerator = new InventoryEnumerator(store, new CandidateClassifier(request.MaxCandidateBytes), new PhysicalFileSystemReader());
            scan = await enumerator.EnumerateAsync(request.SourceRoots, cancellationToken);
            Emit(InventoryPhase.Enumerating, $"Enumerated {scan.TotalCandidates} candidates.", scan.TotalCandidates);

            if (scan.EveryRootReachedTerminal)
            {
                await store.CompleteManifestAsync(scan.ManifestId, everyRootReachedTerminal: true, cancellationToken);
            }
        }

        Emit(InventoryPhase.Exporting, "Exporting inventory artifacts.");
        var snapshot = await new ManifestSnapshotReader().LoadAsync(request.DatabasePath, scan.ManifestId, cancellationToken);
        var export = await new ManifestExporter().ExportAsync(snapshot, request.OutputDirectory, cancellationToken);

        Emit(InventoryPhase.Completed, export.IsComplete ? "Inventory complete." : "Inventory exported with an incomplete scan.");
        return new InventoryRunResult(scan.ManifestId, scan, export);
    }

        public async Task<SelectSampleResult> RunSelectSampleAsync(SelectSampleRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var snapshot = await new ManifestSnapshotReader().LoadAsync(request.DatabasePath, request.ManifestId, cancellationToken);
            var selection = new StratifiedSampleSelector().Select(
                new SampleSelectionRequest(
                    request.ManifestId,
                    snapshot.State,
                    request.Budget,
                    request.Seed,
                    request.MinimumPerStratum,
                    StratifiedSampleSelector.DefaultPrngAlgorithm,
                    request.ConfidenceCoverageRules,
                    request.SizeBandCount),
                snapshot.Candidates);

            await using var store = new SampleSetStore(request.DatabasePath);
            await store.InitializeAsync(cancellationToken);
            await store.SaveAsync(selection.SampleSet, selection.Members, cancellationToken);

            return new SelectSampleResult(selection);
        }

        public async Task<BenchmarkExtractionResult> RunBenchmarkExtractionAsync(BenchmarkExtractionRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var options = request.Options with { SampleSetId = request.SampleSetId };
            var (sampleSet, members) = await LoadSampleSetAsync(request.DatabasePath, request.SampleSetId, cancellationToken);
            var snapshot = await new ManifestSnapshotReader().LoadAsync(request.DatabasePath, sampleSet.ManifestId, cancellationToken);
            var candidatesByManifest = snapshot.Candidates.ToDictionary(c => c.Id);

            var realExtraction = !string.IsNullOrWhiteSpace(request.CompanionAssemblyPath);
            List<BenchmarkCandidate> probeCandidates;
            if (realExtraction)
            {
                var resolved = await new BenchmarkSourcePathResolver().ResolveAsync(request.DatabasePath, request.SampleSetId, cancellationToken);
                probeCandidates = resolved
                    .Select(m => new BenchmarkCandidate(m.CandidateId, m.StratumKey, candidatesByManifest[m.CandidateId].ByteSize, m.LocalPath))
                    .ToList();
                options = options with
                {
                    AdapterName = "companion-reflection",
                    Configuration = "real local extraction via companion reflection; content and source paths are never persisted",
                };
            }
            else
            {
                probeCandidates = members
                    .Select(member => new BenchmarkCandidate(
                        member.CandidateId,
                        member.StratumKey,
                        candidatesByManifest[member.CandidateId].ByteSize))
                    .ToList();
            }

            using var companion = realExtraction ? new ReflectionCompanionExtractor(request.CompanionAssemblyPath!) : null;
            CandidatePhase extractPhase = realExtraction
                ? RealExtractionPhases.Extract(companion!)
                : ReferenceBenchmarkPhases.Extract;

            await using var store = new BenchmarkObservationStore(request.DatabasePath);
            await store.InitializeAsync(cancellationToken);

            var probe = new ExtractionProbe(options);
            var run = await probe.RunAsync(
                probeCandidates,
                ReferenceBenchmarkPhases.Discover,
                ReferenceBenchmarkPhases.Snapshot,
                extractPhase,
                ReferenceBenchmarkPhases.Stage,
                ReferenceBenchmarkPhases.SampleResource,
                store.SaveObservationAsync,
                cancellationToken);

            var report = BenchmarkReportBuilder.Build(run, options);
            await store.SaveReportAsync(report, cancellationToken);

            return new BenchmarkExtractionResult(report, run.Observations.Count);
        }

        private static async Task<(SampleSet Set, IReadOnlyList<SampleMember> Members)> LoadSampleSetAsync(
            string databasePath,
            Guid sampleSetId,
            CancellationToken cancellationToken)
        {
            await using var store = new SampleSetStore(databasePath);
            await store.InitializeAsync(cancellationToken);
            return await store.LoadAsync(sampleSetId, cancellationToken);
        }

        private void Emit(InventoryPhase phase, string message, int? candidateCount = null)
            => InventoryProgress?.Invoke(this, new InventoryProgressEventArgs(phase, message, candidateCount));
}

/// <summary>
/// Manifest-metadata reference phases for the Unit 3 smoke command: source bytes come
/// from the frozen manifest (no content is opened) and normalized text is the identity
/// of those bytes. Real text extraction is deferred to the Unit 5 Companion disposition.
/// </summary>
internal static class ReferenceBenchmarkPhases
{
public static async Task<PhaseResult> Discover(BenchmarkCandidate candidate, CancellationToken cancellationToken)
{
        await Task.Delay(1, cancellationToken);
        return new PhaseResult(0, "completed", null);
}

public static async Task<PhaseResult> Snapshot(BenchmarkCandidate candidate, CancellationToken cancellationToken)
{
        await Task.Delay(1, cancellationToken);
        return new PhaseResult(candidate.SourceBytes, "completed", null);
}

public static async Task<PhaseResult> Extract(BenchmarkCandidate candidate, CancellationToken cancellationToken)
{
        await Task.Delay(1, cancellationToken);
        return new PhaseResult(candidate.SourceBytes, "completed", null);
}

public static async Task<PhaseResult> Stage(BenchmarkCandidate candidate, CancellationToken cancellationToken)
{
        await Task.Delay(1, cancellationToken);
        return new PhaseResult(0, "completed", null);
}

public static ResourceSnapshot SampleResource(TimeSpan elapsed)
        => new(0, Environment.WorkingSet, 0, 0, elapsed);
}
