using Rag.HistoricalLoader.Core.Benchmark;
using Rag.HistoricalLoader.Core.Data;
using Rag.HistoricalLoader.Core.Persistence;
using Rag.HistoricalLoader.Core.Sampling;

namespace Rag.HistoricalLoader.UnitTests.Benchmark;

public sealed class BenchmarkSourcePathResolverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"resolver-{Guid.NewGuid():N}");

    public BenchmarkSourcePathResolverTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task Resolve_ReturnsMembersWithAbsolutePathsOrderedByRank()
    {
        var (databasePath, sampleSetId, rootPath, candidates) = await BuildFixtureAsync();

        var resolved = await new BenchmarkSourcePathResolver().ResolveAsync(databasePath, sampleSetId);

        // Members are stored with non-monotonic ranks; resolution must order by selected_rank.
        var ranked = candidates.Reverse().ToList();
        Assert.Equal(ranked.Count, resolved.Count);
        for (var i = 0; i < ranked.Count; i++)
        {
            Assert.Equal(ranked[i].Id, resolved[i].CandidateId);
            Assert.Equal(ranked[i].Extension + "|1", resolved[i].StratumKey);
            Assert.Equal(Path.GetFullPath(Path.Combine(rootPath, ranked[i].RelativePath)), resolved[i].LocalPath);
        }
    }

    [Fact]
    public async Task Resolve_UnknownSampleSet_ReturnsEmpty()
    {
        var (databasePath, _, _, _) = await BuildFixtureAsync();

        var resolved = await new BenchmarkSourcePathResolver().ResolveAsync(databasePath, Guid.NewGuid());

        Assert.Empty(resolved);
    }

    [Fact]
    public async Task Resolve_IsReadOnly_DoesNotMutateDatabase()
    {
        var (databasePath, sampleSetId, _, _) = await BuildFixtureAsync();
        var before = await File.ReadAllBytesAsync(databasePath);

        _ = await new BenchmarkSourcePathResolver().ResolveAsync(databasePath, sampleSetId);

        Assert.Equal(before, await File.ReadAllBytesAsync(databasePath));
    }

    private async Task<(string DatabasePath, Guid SampleSetId, string RootPath, IReadOnlyList<Candidate> Candidates)> BuildFixtureAsync()
    {
        var databasePath = Path.Combine(_dir, "store.sqlite");
        var rootPath = Path.Combine(_dir, "corpus");
        Directory.CreateDirectory(rootPath);

        var rootId = Guid.NewGuid();
        var manifestId = Guid.NewGuid();
        var sampleSetId = Guid.NewGuid();
        var candidates = new[]
        {
            new Candidate(Guid.NewGuid(), manifestId, rootId, "a.md", ".md", 100, DateTimeOffset.UtcNow, "eligible"),
            new Candidate(Guid.NewGuid(), manifestId, rootId, Path.Combine("sub", "b.pdf"), ".pdf", 200, DateTimeOffset.UtcNow, "eligible"),
            new Candidate(Guid.NewGuid(), manifestId, rootId, "c.docx", ".docx", 300, DateTimeOffset.UtcNow, "eligible"),
        };

        await using (var store = new SqliteStore(databasePath))
        {
            await store.InitializeAsync();
            await store.AddSourceRootAsync(new SourceRoot(rootId, "docs", rootPath, DateTimeOffset.UtcNow));
            await store.CreateManifestAsync(new Manifest(manifestId, 1, ManifestState.Complete, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            foreach (var candidate in candidates)
            {
                await store.AddCandidateAsync(candidate);
            }
        }

        await using (var store = new SampleSetStore(databasePath))
        {
            await store.InitializeAsync();
            var members = candidates.Select((candidate, index) => new SampleMember(
                sampleSetId, candidate.Id, candidate.Extension + "|1", $"fp-{index}", candidates.Length - 1 - index)).ToList();
            await store.SaveAsync(new SampleSet(
                sampleSetId, manifestId, "v1", "splitmix64", 42, 3, 1, "95%|10%", true, true,
                DateTimeOffset.UtcNow, "{}"), members);
        }

        return (databasePath, sampleSetId, rootPath, candidates);
    }
}
