using Rag.HistoricalLoader.Core.Classification;
using Rag.HistoricalLoader.Core.Data;
using Rag.HistoricalLoader.Core.Persistence;

namespace Rag.HistoricalLoader.Core.Inventory;

public sealed record InventoryScanResult(
    Guid ManifestId,
    IReadOnlyDictionary<string, int> CandidateCountByEligibility,
    IReadOnlyList<RootScanOutcome> RootOutcomes,
    IReadOnlyList<EnumerationError> Errors)
{
    public int TotalCandidates => CandidateCountByEligibility.Values.Sum();

    public bool EveryRootReachedTerminal => RootOutcomes.Count > 0 && RootOutcomes.All(outcome => outcome.ReachedTerminal);
}

public sealed class InventoryEnumerator
{
    private readonly SqliteStore _store;
    private readonly CandidateClassifier _classifier;
    private readonly RootConfinedWalker _walker;

    public InventoryEnumerator(SqliteStore store, CandidateClassifier classifier, IFileSystemReader fileSystem)
    {
        _store = store;
        _classifier = classifier;
        _walker = new RootConfinedWalker(fileSystem);
    }

    public async Task<InventoryScanResult> EnumerateAsync(IReadOnlyList<SourceRoot> roots, CancellationToken cancellationToken = default)
    {
        var manifest = new Manifest(Guid.NewGuid(), 1, ManifestState.Scanning, DateTimeOffset.UtcNow);
        await _store.CreateManifestAsync(manifest, cancellationToken);
        foreach (var root in roots)
        {
            await _store.AddSourceRootAsync(root, cancellationToken);
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var outcomes = new List<RootScanOutcome>();
        var errors = new List<EnumerationError>();

        foreach (var root in roots)
        {
            var (reachedTerminal, rootErrors) = await _walker.WalkAsync(root.CanonicalPath, async (discovery, lastWriteTime) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var classification = _classifier.Classify(discovery);
                var candidate = new Candidate(
                    Guid.NewGuid(),
                    manifest.Id,
                    root.Id,
                    discovery.RelativePath,
                    Path.GetExtension(discovery.RelativePath),
                    discovery.ByteSize,
                    lastWriteTime,
                    classification.EligibilityCode,
                    classification.DiscoveryErrorCode);
                await _store.AddCandidateAsync(candidate, cancellationToken);
                counts[classification.EligibilityCode] = counts.TryGetValue(classification.EligibilityCode, out var current) ? current + 1 : 1;
            }).ConfigureAwait(false);

            outcomes.Add(new RootScanOutcome(root.Id, root.CanonicalPath, reachedTerminal));
            errors.AddRange(rootErrors);

            foreach (var error in rootErrors)
            {
                var relativePath = Path.GetRelativePath(root.CanonicalPath, error.Path);
                await _store.AddEnumerationErrorAsync(manifest.Id, root.Id, relativePath, error.ErrorCode, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            }
        }

        return new InventoryScanResult(manifest.Id, counts, outcomes, errors);
    }
}
