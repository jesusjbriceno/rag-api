using System.IO;

namespace Rag.Companion.Snapshot;

/// <summary>Thrown when another process holds the source file against write/delete sharing.</summary>
public sealed class SharingViolationException : IOException
{
    public SharingViolationException(string path)
        : base($"Another process is using '{path}' (sharing violation).")
    {
        HResult = ErrorSharingViolation;
    }

    private const int ErrorSharingViolation = unchecked((int)0x80070020);
}

/// <summary>Opens a source file for snapshotting. Injectable for sharing-race tests.</summary>
public delegate Stream SourceOpener(string path);

/// <summary>
/// Opens a source file once with read-only sharing (denying concurrent write/delete), verifies
/// the handle path stays confined to a non-reparse root, and copies it to a private snapshot
/// directory. Sharing races are retried at most three times; identity drift, root escape, and
/// residual partial files all fail closed.
/// </summary>
public sealed class HandleConfinedCopier
{
    public const int MaxRetries = 3;
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private readonly IReadOnlyList<string> _roots;
    private readonly string _snapshotDirectory;
    private readonly TimeProvider _clock;
    private readonly SourceOpener _openSource;

    public HandleConfinedCopier(IEnumerable<string> roots, string snapshotDirectory, TimeProvider? clock = null, SourceOpener? openSource = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotDirectory);
        var normalizedRoots = new List<string>();
        foreach (var root in roots)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(root);
            normalizedRoots.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
        }
        if (normalizedRoots.Count == 0) throw new ArgumentException("At least one root is required.", nameof(roots));
        _roots = normalizedRoots;
        _snapshotDirectory = Path.GetFullPath(snapshotDirectory);
        _clock = clock ?? TimeProvider.System;
        _openSource = openSource ?? OpenShared;
    }

    /// <summary>Copies <paramref name="sourcePath"/> into the snapshot directory and returns the snapshot path.</summary>
    public async Task<string> CopyAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullPath = Path.GetFullPath(sourcePath);
        IOException? sharingViolation = null;
        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try { return await CopyOnceAsync(fullPath, cancellationToken).ConfigureAwait(false); }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                sharingViolation = ex;
                if (attempt < MaxRetries) await Task.Delay(Backoff(attempt), _clock, cancellationToken).ConfigureAwait(false);
            }
        }
        throw sharingViolation ?? new IOException($"Snapshot failed for '{fullPath}' after {MaxRetries} sharing retries.");
    }

    private async Task<string> CopyOnceAsync(string sourcePath, CancellationToken cancellationToken)
    {
        EnsureSnapshotDirectory();
        using Stream source = _openSource(sourcePath);
        EnsureConfined(sourcePath);
        var identity = CaptureIdentity(sourcePath);
        var destinationPath = Path.Combine(_snapshotDirectory, Guid.NewGuid().ToString("N") + Path.GetExtension(sourcePath));
        var tempPath = destinationPath + ".part";
        try
        {
            await using (var destination = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, 81920, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            EnsureUnchanged(sourcePath, identity);
            EnsureConfined(sourcePath);
            File.Move(tempPath, destinationPath);
            return destinationPath;
        }
        catch { TryDelete(tempPath); TryDelete(destinationPath); throw; }
    }

    private void EnsureSnapshotDirectory()
    {
        if (!Directory.Exists(_snapshotDirectory)) Directory.CreateDirectory(_snapshotDirectory);
        if (ConfinedSnapshotService.IsLink(new DirectoryInfo(_snapshotDirectory))) throw new IOException($"Snapshot directory is a reparse point: '{_snapshotDirectory}'.");
    }

    private void EnsureConfined(string path)
    {
        if (!IsUnderAnyRoot(path)) throw new IOException($"Source escapes the configured roots: '{path}'.");
        if (ConfinedSnapshotService.IsLink(new FileInfo(path)))
        {
            var target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            if (target is null || !IsUnderAnyRoot(target)) throw new IOException($"Source is a reparse point escaping the configured roots: '{path}'.");
        }
    }

    private static (long Length, DateTime LastWriteTimeUtc) CaptureIdentity(string path) { var info = new FileInfo(path); return (info.Length, info.LastWriteTimeUtc); }
    private static void EnsureUnchanged(string path, (long Length, DateTime LastWriteTimeUtc) identity) { var current = CaptureIdentity(path); if (current.Length != identity.Length || current.LastWriteTimeUtc != identity.LastWriteTimeUtc) throw new IOException($"Source changed during snapshot (drift detected): '{path}'."); }
    private bool IsUnderAnyRoot(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return _roots.Any(root => string.Equals(full, root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }
    private static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(25 * attempt);
    private static bool IsSharingViolation(IOException ex) => ex.HResult == ErrorSharingViolation;
    private static Stream OpenShared(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
