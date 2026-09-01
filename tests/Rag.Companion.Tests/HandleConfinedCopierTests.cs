using Rag.Companion.Snapshot;

namespace Rag.Companion.Tests;

public sealed class HandleConfinedCopierTests : IDisposable
{
    private readonly string _base;
    private readonly string _root;
    private readonly string _snapshot;
    public HandleConfinedCopierTests() { _base = Path.Combine(Path.GetTempPath(), "copier-" + Guid.NewGuid().ToString("N")); _root = Path.Combine(_base, "root"); _snapshot = Path.Combine(_base, "snapshot"); Directory.CreateDirectory(_root); Directory.CreateDirectory(_snapshot); }
    public void Dispose() => TryDelete(_base);

    [Fact]
    public async Task CopyAsync_retries_sharing_race_then_succeeds()
    {
        var source = Path.Combine(_root, "README.md"); await File.WriteAllTextAsync(source, "hello snapshot"); var attempts = 0;
        SourceOpener opener = path => { attempts++; if (attempts < HandleConfinedCopier.MaxRetries) throw new SharingViolationException(path); return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); };
        var snapshotPath = await new HandleConfinedCopier([_root], _snapshot, openSource: opener).CopyAsync(source);
        Assert.Equal(HandleConfinedCopier.MaxRetries, attempts); Assert.StartsWith(_snapshot, snapshotPath); Assert.Equal("hello snapshot", await File.ReadAllTextAsync(snapshotPath));
    }

    [Fact]
    public async Task CopyAsync_exhausts_sharing_retries_and_fails_closed()
    {
        var source = Path.Combine(_root, "README.md"); await File.WriteAllTextAsync(source, "hello");
        await Assert.ThrowsAsync<SharingViolationException>(() => new HandleConfinedCopier([_root], _snapshot, openSource: path => throw new SharingViolationException(path)).CopyAsync(source));
        Assert.Empty(Directory.GetFileSystemEntries(_snapshot));
    }

    [Fact]
    public async Task CopyAsync_rejects_source_escaping_root_via_symlink()
    {
        var outside = Path.Combine(_base, "outside"); Directory.CreateDirectory(outside); var outsideFile = Path.Combine(outside, "secret.txt"); await File.WriteAllTextAsync(outsideFile, "secret"); var link = Path.Combine(_root, "link.txt"); File.CreateSymbolicLink(link, outsideFile);
        await Assert.ThrowsAsync<IOException>(() => new HandleConfinedCopier([_root], _snapshot).CopyAsync(link));
        Assert.Empty(Directory.GetFileSystemEntries(_snapshot));
    }

    private static void TryDelete(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); else if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
