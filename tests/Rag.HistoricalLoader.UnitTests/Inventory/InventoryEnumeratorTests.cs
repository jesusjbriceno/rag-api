using System.Text;
using Rag.HistoricalLoader.Core.Classification;
using Rag.HistoricalLoader.Core.Data;
using Rag.HistoricalLoader.Core.Inventory;
using Rag.HistoricalLoader.Core.Persistence;

namespace Rag.HistoricalLoader.UnitTests.Inventory;

public sealed class InventoryEnumeratorTests : IAsyncLifetime
{
    private string _directory = null!;
    private SqliteStore _store = null!;

    public async Task InitializeAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"rag-historical-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _store = new SqliteStore(Path.Combine(_directory, "store.sqlite"));
        await _store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; the OS temp directory will reclaim leftovers.
        }
    }

    [Fact]
    public async Task Enumerate_PersistsEveryOutcomeInSmallTransactions()
    {
        var root = NewRootPath();
        var reader = new FakeFileSystemReader();
        reader.AddDirectory(root,
            FileEntry(root, "eligible.docx", 5),
            FileEntry(root, "unsupported.xlsx", 5),
            InaccessibleFileEntry(root, "locked.pdf"),
            ReparseEntry(root, "junction"),
            DirectoryEntry(root, "partial"));
        reader.ThrowOn(Path.Combine(root, "partial"));

        var enumerator = new InventoryEnumerator(_store, new CandidateClassifier(10), reader);
        var roots = new[] { new SourceRoot(Guid.NewGuid(), "docs", root, DateTimeOffset.UtcNow) };

        var result = await enumerator.EnumerateAsync(roots);

        Assert.Equal(4, result.TotalCandidates);
        Assert.Equal(4, await _store.CountCandidatesAsync(result.ManifestId));
        Assert.Equal(1, result.CandidateCountByEligibility[EligibilityCodes.Eligible]);
        Assert.Equal(1, result.CandidateCountByEligibility[EligibilityCodes.UnsupportedFormat]);
        Assert.Equal(1, result.CandidateCountByEligibility[EligibilityCodes.AccessDenied]);
        Assert.Equal(1, result.CandidateCountByEligibility[EligibilityCodes.ReparsePoint]);
        Assert.Equal(ManifestState.Scanning, await _store.GetManifestStateAsync(result.ManifestId));
        Assert.Contains(result.Errors, e => e.ErrorCode == EnumerationErrorCodes.DirectoryEnumerationFailed);
        Assert.False(result.EveryRootReachedTerminal);
    }

    [Fact]
    public async Task Enumerate_DoesNotFollowReparsePoints()
    {
        var root = NewRootPath();
        var reader = new FakeFileSystemReader();
        reader.AddDirectory(root,
            DirectoryEntry(root, "real"),
            ReparseEntry(root, "junction"));
        reader.AddDirectory(Path.Combine(root, "real"), FileEntry(Path.Combine(root, "real"), "inside.txt", 5));
        reader.AddDirectory(Path.Combine(root, "junction"), FileEntry(Path.Combine(root, "junction"), "escaped.txt", 5));

        var enumerator = new InventoryEnumerator(_store, new CandidateClassifier(10), reader);
        var roots = new[] { new SourceRoot(Guid.NewGuid(), "docs", root, DateTimeOffset.UtcNow) };

        var result = await enumerator.EnumerateAsync(roots);

        Assert.Equal(2, result.TotalCandidates);
        Assert.Equal(1, result.CandidateCountByEligibility[EligibilityCodes.Eligible]);
        Assert.Equal(1, result.CandidateCountByEligibility[EligibilityCodes.ReparsePoint]);
        Assert.Empty(result.Errors);
        Assert.True(result.EveryRootReachedTerminal);
    }

    [Fact]
    public async Task Enumerate_RejectsPathsEscapingRoot()
    {
        var root = NewRootPath();
        var escaping = Path.Combine(root, "..", "outside.txt");
        var reader = new FakeFileSystemReader();
        reader.AddDirectory(root, new FileSystemEntry(escaping, false, false, 5, DateTimeOffset.UtcNow));

        var enumerator = new InventoryEnumerator(_store, new CandidateClassifier(10), reader);
        var roots = new[] { new SourceRoot(Guid.NewGuid(), "docs", root, DateTimeOffset.UtcNow) };

        var result = await enumerator.EnumerateAsync(roots);

        Assert.Equal(0, result.TotalCandidates);
        Assert.Contains(result.Errors, e => e.ErrorCode == EnumerationErrorCodes.PathEscape);
        Assert.False(result.EveryRootReachedTerminal);
    }

    [Fact]
    public async Task Enumerate_ReadsMetadataOnlyAndNeverOpensContent()
    {
        var root = NewRootPath();
        Directory.CreateDirectory(root);
        var sentinel = "UNIQUE_SENTINEL_CONTENT_9f4b2c";
        var path = Path.Combine(root, "doc.txt");
        await File.WriteAllTextAsync(path, sentinel);
        try
        {
            var reader = new PhysicalFileSystemReader();

            var entry = Assert.Single(reader.Enumerate(root));
            Assert.False(entry.IsDirectory);
            Assert.False(entry.IsReparsePoint);
            Assert.Equal(Encoding.UTF8.GetByteCount(sentinel), entry.ByteSize);

            var enumerator = new InventoryEnumerator(_store, new CandidateClassifier(10), reader);
            var roots = new[] { new SourceRoot(Guid.NewGuid(), "docs", root, DateTimeOffset.UtcNow) };

            var result = await enumerator.EnumerateAsync(roots);

            Assert.Equal(1, result.TotalCandidates);
            Assert.Equal(1, await _store.CountCandidatesAsync(result.ManifestId));
            Assert.Empty(result.Errors);
            Assert.DoesNotContain(result.Errors, e => e.Path.Contains(sentinel, StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact]
    public async Task PhysicalFileSystemReader_DetectsSymlinkAsReparsePointAndDoesNotFollow()
    {
        var root = NewRootPath();
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "real"));
        await File.WriteAllTextAsync(Path.Combine(root, "real", "inside.txt"), "content");
        Directory.CreateSymbolicLink(Path.Combine(root, "junction"), Path.Combine(root, "real"));
        try
        {
            var enumerator = new InventoryEnumerator(_store, new CandidateClassifier(100), new PhysicalFileSystemReader());
            var roots = new[] { new SourceRoot(Guid.NewGuid(), "docs", root, DateTimeOffset.UtcNow) };

            var result = await enumerator.EnumerateAsync(roots);

            Assert.Equal(2, result.TotalCandidates);
            Assert.Equal(1, result.CandidateCountByEligibility[EligibilityCodes.Eligible]);
            Assert.Equal(1, result.CandidateCountByEligibility[EligibilityCodes.ReparsePoint]);
            Assert.True(result.EveryRootReachedTerminal);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact]
    public async Task Walker_NormalizesLongAndUnicodeRelativePaths()
    {
        var root = NewRootPath();
        var longName = new string('a', 120) + ".pdf";
        var reader = new FakeFileSystemReader();
        reader.AddDirectory(root, FileEntry(root, longName, 5), DirectoryEntry(root, "café_文档"));
        reader.AddDirectory(Path.Combine(root, "café_文档"), FileEntry(Path.Combine(root, "café_文档"), "nested_ß.txt", 5));

        var walker = new RootConfinedWalker(reader);
        var discovered = new List<CandidateDiscovery>();
        var (reachedTerminal, errors) = await walker.WalkAsync(root, (d, _) =>
        {
            discovered.Add(d);
            return Task.CompletedTask;
        });

        Assert.True(reachedTerminal);
        Assert.Empty(errors);
        Assert.Equal(2, discovered.Count);
        Assert.Contains(discovered, d => d.RelativePath == longName);
        Assert.Contains(discovered, d => d.RelativePath == Path.Combine("café_文档", "nested_ß.txt"));
    }

    [Fact]
    public async Task Enumerate_PermissionDeniedDirectoryRecordsErrorWithoutThrowing()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // POSIX permission semantics are not available on Windows; junction/reparse is covered elsewhere.
        }

        var root = NewRootPath();
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "open"));
        await File.WriteAllTextAsync(Path.Combine(root, "open", "ok.txt"), "x");
        var locked = Path.Combine(root, "locked");
        Directory.CreateDirectory(locked);
        await File.WriteAllTextAsync(Path.Combine(locked, "hidden.txt"), "x");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var enumerator = new InventoryEnumerator(_store, new CandidateClassifier(100), new PhysicalFileSystemReader());
            var roots = new[] { new SourceRoot(Guid.NewGuid(), "docs", root, DateTimeOffset.UtcNow) };

            var result = await enumerator.EnumerateAsync(roots);

            Assert.Equal(1, result.TotalCandidates);
            Assert.Equal(1, await _store.CountCandidatesAsync(result.ManifestId));
            Assert.Contains(result.Errors, e => e.ErrorCode == EnumerationErrorCodes.DirectoryEnumerationFailed);
            Assert.False(result.EveryRootReachedTerminal);
            Assert.Equal(ManifestState.Scanning, await _store.GetManifestStateAsync(result.ManifestId));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact]
    public async Task Enumerate_PersistsEnumerationErrorsDurablyAcrossReopen()
    {
        var root = NewRootPath();
        var escaping = Path.Combine(root, "..", "outside.txt");
        var reader = new FakeFileSystemReader();
        reader.AddDirectory(root,
            DirectoryEntry(root, "partial"),
            new FileSystemEntry(escaping, false, false, 5, DateTimeOffset.UtcNow));
        reader.ThrowOn(Path.Combine(root, "partial"));

        var enumerator = new InventoryEnumerator(_store, new CandidateClassifier(10), reader);
        var roots = new[] { new SourceRoot(Guid.NewGuid(), "docs", root, DateTimeOffset.UtcNow) };

        var result = await enumerator.EnumerateAsync(roots);

        Assert.Equal(2, result.Errors.Count);
        Assert.Contains(result.Errors, e => e.ErrorCode == EnumerationErrorCodes.DirectoryEnumerationFailed);
        Assert.Contains(result.Errors, e => e.ErrorCode == EnumerationErrorCodes.PathEscape);

        // Dispose and reopen a fresh store instance on the same database file to prove
        // enumeration/path-escape errors are durable, not only held in InventoryScanResult.Errors.
        await _store.DisposeAsync();
        _store = new SqliteStore(Path.Combine(_directory, "store.sqlite"));
        await _store.InitializeAsync();

        var persisted = await _store.GetEnumerationErrorsAsync(result.ManifestId);

        Assert.Equal(2, persisted.Count);
        Assert.Contains(persisted, e => e.ErrorCode == EnumerationErrorCodes.DirectoryEnumerationFailed);
        Assert.Contains(persisted, e => e.ErrorCode == EnumerationErrorCodes.PathEscape);
        Assert.All(persisted, e => Assert.Equal(result.ManifestId, e.ManifestId));
    }

    [Fact]
    public async Task Enumerate_PersistsAllErrorKindsAcrossMultipleRoots()
    {
        var rootA = NewRootPath();
        var rootB = NewRootPath();
        var reader = new FakeFileSystemReader();

        // Root A: one directory-enumeration failure + one path escape.
        reader.AddDirectory(rootA,
            DirectoryEntry(rootA, "partial"),
            new FileSystemEntry(Path.Combine(rootA, "..", "outside-a.txt"), false, false, 5, DateTimeOffset.UtcNow));
        reader.ThrowOn(Path.Combine(rootA, "partial"));

        // Root B: one inaccessible directory (access-denied) + one eligible file.
        reader.AddDirectory(rootB,
            InaccessibleDirectoryEntry(rootB, "locked"),
            FileEntry(rootB, "ok.txt", 5));

        var enumerator = new InventoryEnumerator(_store, new CandidateClassifier(10), reader);
        var roots = new[]
        {
            new SourceRoot(Guid.NewGuid(), "docs-a", rootA, DateTimeOffset.UtcNow),
            new SourceRoot(Guid.NewGuid(), "docs-b", rootB, DateTimeOffset.UtcNow),
        };

        var result = await enumerator.EnumerateAsync(roots);

        Assert.Equal(3, result.Errors.Count);
        Assert.Contains(result.Errors, e => e.ErrorCode == EnumerationErrorCodes.DirectoryEnumerationFailed);
        Assert.Contains(result.Errors, e => e.ErrorCode == EnumerationErrorCodes.PathEscape);
        Assert.Contains(result.Errors, e => e.ErrorCode == EnumerationErrorCodes.AccessDenied);
        Assert.Equal(1, result.TotalCandidates);

        await _store.DisposeAsync();
        _store = new SqliteStore(Path.Combine(_directory, "store.sqlite"));
        await _store.InitializeAsync();

        var persisted = await _store.GetEnumerationErrorsAsync(result.ManifestId);

        Assert.Equal(3, persisted.Count);
        Assert.Contains(persisted, e => e.ErrorCode == EnumerationErrorCodes.DirectoryEnumerationFailed);
        Assert.Contains(persisted, e => e.ErrorCode == EnumerationErrorCodes.PathEscape);
        Assert.Contains(persisted, e => e.ErrorCode == EnumerationErrorCodes.AccessDenied);

        var rootIds = roots.Select(r => r.Id).ToHashSet();
        Assert.All(persisted, e => Assert.Equal(result.ManifestId, e.ManifestId));
        Assert.All(persisted, e => Assert.Contains(e.RootId, rootIds));
    }

    private static string NewRootPath() => Path.Combine(Path.GetTempPath(), $"rag-fixture-{Guid.NewGuid():N}");

    private static FileSystemEntry FileEntry(string root, string name, long size)
        => new(Path.Combine(root, name), false, false, size, DateTimeOffset.UtcNow);

    private static FileSystemEntry InaccessibleFileEntry(string root, string name)
        => new(Path.Combine(root, name), false, false, 0, DateTimeOffset.MinValue, EnumerationErrorCodes.AccessDenied);

    private static FileSystemEntry InaccessibleDirectoryEntry(string root, string name)
        => new(Path.Combine(root, name), true, false, 0, DateTimeOffset.MinValue, EnumerationErrorCodes.AccessDenied);

    private static FileSystemEntry ReparseEntry(string root, string name)
        => new(Path.Combine(root, name), true, true, 0, DateTimeOffset.MinValue);

    private static FileSystemEntry DirectoryEntry(string root, string name)
        => new(Path.Combine(root, name), true, false, 0, DateTimeOffset.UtcNow);

    private sealed class FakeFileSystemReader : IFileSystemReader
    {
        private readonly Dictionary<string, IReadOnlyList<FileSystemEntry>> _entries = new(StringComparer.Ordinal);
        private readonly HashSet<string> _throwOn = new(StringComparer.Ordinal);

        public void AddDirectory(string path, params FileSystemEntry[] entries) => _entries[path] = entries;

        public void ThrowOn(string path) => _throwOn.Add(path);

        public IReadOnlyList<FileSystemEntry> Enumerate(string directoryPath)
        {
            if (_throwOn.Contains(directoryPath))
            {
                throw new UnauthorizedAccessException("Simulated access denial.");
            }

            return _entries.TryGetValue(directoryPath, out var entries) ? entries : Array.Empty<FileSystemEntry>();
        }
    }
}
