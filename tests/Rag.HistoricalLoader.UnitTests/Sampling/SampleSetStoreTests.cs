using Microsoft.Data.Sqlite;
using Rag.HistoricalLoader.Core.Classification;
using Rag.HistoricalLoader.Core.Data;
using Rag.HistoricalLoader.Core.Persistence;
using Rag.HistoricalLoader.Core.Sampling;

namespace Rag.HistoricalLoader.UnitTests.Sampling;

public sealed class SampleSetStoreTests
{
    private static readonly Guid ManifestId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid RootId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public async Task SavePersistsFrozenFingerprintsAndEmitsAuditEvent()
    {
        var directory = NewDirectory();
        try
        {
            var databasePath = Path.Combine(directory, "store.sqlite");
            await using (var store = new SqliteStore(databasePath))
            {
                await store.InitializeAsync();
                await store.CreateManifestAsync(new Manifest(ManifestId, 1, ManifestState.Complete, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            }

            var candidates = new[]
            {
                Candidate(1, ".docx", 100), Candidate(2, ".docx", 200), Candidate(3, ".pdf", 300),
            };
            var result = new StratifiedSampleSelector().Select(
                new SampleSelectionRequest(ManifestId, ManifestState.Complete, 3, 9, 1, "splitmix64", "{}", 2),
                candidates);

            await using (var sampleStore = new SampleSetStore(databasePath))
            {
                await sampleStore.InitializeAsync();
                await sampleStore.SaveAsync(result.SampleSet, result.Members);

                var (loadedSet, loadedMembers) = await sampleStore.LoadAsync(result.SampleSet.Id);

                Assert.Equal(result.SampleSet.Id, loadedSet.Id);
                Assert.Equal(result.SampleSet.GatePassed, loadedSet.GatePassed);
                Assert.Equal(
                    result.Members.OrderBy(m => m.SelectedRank).Select(m => m.CandidateId),
                    loadedMembers.OrderBy(m => m.SelectedRank).Select(m => m.CandidateId));
                Assert.All(loadedMembers, m => Assert.False(string.IsNullOrWhiteSpace(m.MetadataFingerprint)));
            }

            Assert.Equal(1, await CountAuditRowsAsync(databasePath));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ChangedCandidateInvalidatesFrozenFingerprint()
    {
        var directory = NewDirectory();
        try
        {
            var databasePath = Path.Combine(directory, "store.sqlite");
            await using (var store = new SqliteStore(databasePath))
            {
                await store.InitializeAsync();
                await store.CreateManifestAsync(new Manifest(ManifestId, 1, ManifestState.Complete, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            }

            var candidates = new[]
            {
                Candidate(1, ".docx", 100), Candidate(2, ".docx", 200), Candidate(3, ".pdf", 300),
            };
            var result = new StratifiedSampleSelector().Select(
                new SampleSelectionRequest(ManifestId, ManifestState.Complete, 3, 9, 1, "splitmix64", "{}", 2),
                candidates);
            var byId = candidates.ToDictionary(c => c.Id);

            await using (var sampleStore = new SampleSetStore(databasePath))
            {
                await sampleStore.InitializeAsync();
                await sampleStore.SaveAsync(result.SampleSet, result.Members);
                var (_, members) = await sampleStore.LoadAsync(result.SampleSet.Id);

                var member = members.OrderBy(m => m.SelectedRank).First();
                var changed = byId[member.CandidateId] with { ByteSize = byId[member.CandidateId].ByteSize + 1 };

                Assert.NotEqual(
                    StratifiedSampleSelector.ComputeMetadataFingerprint(changed),
                    member.MetadataFingerprint);
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static Candidate Candidate(int seq, string ext, long size)
        => new(
            Guid.Parse($"00000000-0000-0000-0000-{seq:D12}"),
            ManifestId,
            RootId,
            $"f{seq}{ext}",
            ext,
            size,
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seq),
            EligibilityCodes.Eligible);

    private static async Task<int> CountAuditRowsAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM audit_event WHERE action = 'sample_select';";
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rag-sample-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
