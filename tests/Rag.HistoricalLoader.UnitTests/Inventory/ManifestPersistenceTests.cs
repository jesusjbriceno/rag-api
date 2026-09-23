using Microsoft.Data.Sqlite;
using Rag.HistoricalLoader.Core.Classification;
using Rag.HistoricalLoader.Core.Data;
using Rag.HistoricalLoader.Core.Inventory;
using Rag.HistoricalLoader.Core.Persistence;

namespace Rag.HistoricalLoader.UnitTests.Inventory;

public sealed class ManifestPersistenceTests
{
    [Fact]
    public async Task Initialize_SetsDurabilityConfiguration()
    {
        var directory = NewDirectory();
        try
        {
            await using var store = OpenStore(directory);
            await store.InitializeAsync();

            var config = await store.GetDurabilityConfigurationAsync();

            Assert.Equal("wal", config.JournalMode);
            Assert.True(config.ForeignKeysEnabled);
            Assert.Equal(2, config.SynchronousMode);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task Initialize_EnforcesForeignKeys()
    {
        var directory = NewDirectory();
        try
        {
            await using var store = OpenStore(directory);
            await store.InitializeAsync();

            var root = new SourceRoot(Guid.NewGuid(), "docs", "/srv/docs", DateTimeOffset.UtcNow);
            var manifest = new Manifest(Guid.NewGuid(), 1, ManifestState.Scanning, DateTimeOffset.UtcNow);
            await store.AddSourceRootAsync(root);
            await store.CreateManifestAsync(manifest);

            await store.AddCandidateAsync(NewCandidate(manifest.Id, root.Id, "a.txt"));

            await Assert.ThrowsAsync<SqliteException>(() =>
                store.AddCandidateAsync(NewCandidate(Guid.NewGuid(), root.Id, "b.txt")));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task Initialize_SerializesConcurrentWritesThroughSingleWriter()
    {
        var directory = NewDirectory();
        try
        {
            await using var store = OpenStore(directory);
            await store.InitializeAsync();

            var root = new SourceRoot(Guid.NewGuid(), "docs", "/srv/docs", DateTimeOffset.UtcNow);
            var manifest = new Manifest(Guid.NewGuid(), 1, ManifestState.Scanning, DateTimeOffset.UtcNow);
            await store.AddSourceRootAsync(root);
            await store.CreateManifestAsync(manifest);

            var writes = Enumerable.Range(0, 50)
                .Select(i => store.AddCandidateAsync(NewCandidate(manifest.Id, root.Id, $"file-{i}.txt")))
                .ToArray();
            await Task.WhenAll(writes);

            Assert.Equal(50, await store.CountCandidatesAsync(manifest.Id));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task Initialize_MigratesAndBacksUpBeforeMigration()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            SeedVersionZeroDatabase(path);

            await using var store = new SqliteStore(path);
            await store.InitializeAsync();

            Assert.Equal(SqliteStore.CurrentSchemaVersion, await store.GetSchemaVersionAsync());
            Assert.True(File.Exists($"{path}.pre-migration-v0.bak"));
            await store.CreateManifestAsync(new Manifest(Guid.NewGuid(), 1, ManifestState.Scanning, DateTimeOffset.UtcNow));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task Initialize_BlocksOnIntegrityFailureWithoutRebuild()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            await File.WriteAllTextAsync(path, "not a sqlite database");

            var store = new SqliteStore(path);
            var exception = await Assert.ThrowsAsync<SqliteIntegrityException>(() => store.InitializeAsync());
            await store.DisposeAsync();

            Assert.Contains("restore", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("export", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("not a sqlite database", await File.ReadAllTextAsync(path));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task CompleteManifest_RequiresEveryRootTerminal()
    {
        var directory = NewDirectory();
        try
        {
            await using var store = OpenStore(directory);
            await store.InitializeAsync();

            var manifest = new Manifest(Guid.NewGuid(), 1, ManifestState.Scanning, DateTimeOffset.UtcNow);
            await store.CreateManifestAsync(manifest);

            var completed = await store.CompleteManifestAsync(manifest.Id, everyRootReachedTerminal: false);

            Assert.False(completed);
            Assert.Equal(ManifestState.Scanning, await store.GetManifestStateAsync(manifest.Id));

            Assert.True(await store.CompleteManifestAsync(manifest.Id, everyRootReachedTerminal: true));
            Assert.Equal(ManifestState.Complete, await store.GetManifestStateAsync(manifest.Id));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task InstallationId_IsStableAcrossReopens()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);

            Guid first;
            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                first = await store.GetInstallationIdAsync();
                Assert.NotEqual(Guid.Empty, first);
            }

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                Assert.Equal(first, await store.GetInstallationIdAsync());
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task EnumerationErrors_AreDurableAcrossReopen()
    {
        var directory = NewDirectory();
        try
        {
            var path = DatabasePath(directory);
            var manifestId = Guid.NewGuid();
            var rootId = Guid.NewGuid();

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();
                await store.CreateManifestAsync(new Manifest(manifestId, 1, ManifestState.Scanning, DateTimeOffset.UtcNow));
                await store.AddSourceRootAsync(new SourceRoot(rootId, "docs", "/srv/docs", DateTimeOffset.UtcNow));
                await store.AddEnumerationErrorAsync(manifestId, rootId, "locked", EnumerationErrorCodes.AccessDenied, DateTimeOffset.UtcNow);
                await store.AddEnumerationErrorAsync(manifestId, rootId, "../outside.txt", EnumerationErrorCodes.PathEscape, DateTimeOffset.UtcNow);
            }

            await using (var store = new SqliteStore(path))
            {
                await store.InitializeAsync();

                var errors = await store.GetEnumerationErrorsAsync(manifestId);

                Assert.Equal(2, errors.Count);
                Assert.Contains(errors, e => e.ErrorCode == EnumerationErrorCodes.AccessDenied && e.RelativePath == "locked");
                Assert.Contains(errors, e => e.ErrorCode == EnumerationErrorCodes.PathEscape && e.RelativePath == "../outside.txt");
                Assert.All(errors, e => Assert.Equal(manifestId, e.ManifestId));
                Assert.All(errors, e => Assert.Equal(rootId, e.RootId));
            }
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rag-historical-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string DatabasePath(string directory) => Path.Combine(directory, "store.sqlite");

    private static SqliteStore OpenStore(string directory) => new(DatabasePath(directory));

    private static void DeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; the OS temp directory will reclaim leftovers.
        }
    }

    private static Candidate NewCandidate(Guid manifestId, Guid rootId, string relativePath)
        => new(Guid.NewGuid(), manifestId, rootId, relativePath, ".txt", 100, DateTimeOffset.UtcNow, EligibilityCodes.Eligible);

    private static void SeedVersionZeroDatabase(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE legacy_marker (id INTEGER NOT NULL);";
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO legacy_marker VALUES (1);";
            command.ExecuteNonQuery();
        }
    }
}
