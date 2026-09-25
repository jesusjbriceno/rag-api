using Rag.Companion.Snapshot;

namespace Rag.Companion.Tests;

public sealed class HandleConfinedCopierTests : IDisposable
{
    private readonly string _base;
    private readonly string _root;
    private readonly string _snapshot;

    public HandleConfinedCopierTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "copier-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_base, "root");
        _snapshot = Path.Combine(_base, "snapshot");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_snapshot);
    }

    public void Dispose() => TryDelete(_base);

    [Fact]
    public async Task CopyAsync_retries_sharing_race_then_succeeds()
    {
        var source = Path.Combine(_root, "README.md");
        await File.WriteAllTextAsync(source, "hello snapshot");

        var attempts = 0;
        SourceOpener opener = path =>
        {
            attempts++;
            if (attempts < HandleConfinedCopier.MaxRetries)
            {
                throw new SharingViolationException(path);
            }
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        };

        var copier = new HandleConfinedCopier([_root], _snapshot, openSource: opener);

        var snapshotPath = await copier.CopyAsync(source);

        Assert.Equal(HandleConfinedCopier.MaxRetries, attempts);
        Assert.StartsWith(_snapshot, snapshotPath);
        Assert.Equal("hello snapshot", await File.ReadAllTextAsync(snapshotPath));
    }

    [Fact]
    public async Task CopyAsync_exhausts_sharing_retries_and_fails_closed()
    {
        var source = Path.Combine(_root, "README.md");
        await File.WriteAllTextAsync(source, "hello");

        SourceOpener opener = path => throw new SharingViolationException(path);
        var copier = new HandleConfinedCopier([_root], _snapshot, openSource: opener);

        await Assert.ThrowsAsync<SharingViolationException>(() => copier.CopyAsync(source));

        Assert.Empty(Directory.GetFileSystemEntries(_snapshot));
    }

    [Fact]
    public async Task CopyAsync_detects_drift_and_cleans_up_partial_copy()
    {
        var source = Path.Combine(_root, "README.md");
        await File.WriteAllTextAsync(source, "original");

        var copier = new HandleConfinedCopier([_root], _snapshot, openSource: path => new DriftingStream(path));

        await Assert.ThrowsAsync<IOException>(() => copier.CopyAsync(source));

        Assert.Empty(Directory.GetFileSystemEntries(_snapshot));
    }

    [Fact]
    public async Task CopyAsync_rejects_source_escaping_root_via_symlink()
    {
        var outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(outside);
        var outsideFile = Path.Combine(outside, "secret.txt");
        await File.WriteAllTextAsync(outsideFile, "secret");
        var link = Path.Combine(_root, "link.txt");
        File.CreateSymbolicLink(link, outsideFile);

        var copier = new HandleConfinedCopier([_root], _snapshot);

        await Assert.ThrowsAsync<IOException>(() => copier.CopyAsync(link));

        Assert.Empty(Directory.GetFileSystemEntries(_snapshot));
    }

    /// <summary>
    /// A source stream that appends a byte to the on-disk file the moment the copy reaches EOF,
    /// deterministically simulating a concurrent writer changing the file mid-snapshot.
    /// </summary>
    private sealed class DriftingStream : Stream
    {
        private readonly FileStream _inner;
        private readonly string _path;
        private bool _drifted;

        public DriftingStream(string path)
        {
            _path = path;
            _inner = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0 && !_drifted)
            {
                _drifted = true;
                await File.AppendAllTextAsync(_path, "mutated", cancellationToken).ConfigureAwait(false);
            }
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
