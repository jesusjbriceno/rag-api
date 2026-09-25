namespace Rag.HistoricalLoader.Engine.Control;

/// <summary>
/// Raised when another engine instance already holds the installation lock. It is thrown before the store or
/// the transport is opened, so a second instance fails closed instead of taking the installation over.
/// </summary>
public sealed class ControlInstallationLockHeldException : Exception
{
    public ControlInstallationLockHeldException(string lockFilePath, Exception? innerException = null)
        : base($"Another engine instance holds the installation lock '{lockFilePath}'.", innerException)
        => LockFilePath = lockFilePath;

    /// <summary>The installation lock path that is already held.</summary>
    public string LockFilePath { get; }
}

/// <summary>
/// The OS-backed exclusive installation lock. It is a single <see cref="FileStream"/> opened with
/// <see cref="FileShare.None"/> on the installation lock file, so the exclusion is enforced by the operating
/// system rather than by engine-side convention: every other handle — even a permissive read — is refused
/// while it is held, and the lock is released when the owning handle is disposed or the process exits.
/// </summary>
public sealed class ControlInstallationLock : IDisposable
{
    private FileStream? _stream;

    private ControlInstallationLock(string lockFilePath, FileStream stream)
    {
        LockFilePath = lockFilePath;
        _stream = stream;
    }

    /// <summary>The installation lock path.</summary>
    public string LockFilePath { get; }

    /// <summary>
    /// Takes the installation lock, or throws <see cref="ControlInstallationLockHeldException"/> when another
    /// instance already holds it. This is the engine's fail-closed entry point.
    /// </summary>
    /// <exception cref="ControlInstallationLockHeldException">The lock is already held.</exception>
    public static ControlInstallationLock Acquire(string lockFilePath)
        => TryAcquire(lockFilePath, out var held)
            ? held!
            : throw new ControlInstallationLockHeldException(lockFilePath);

    /// <summary>
    /// Attempts to take the installation lock. A held lock, an unreadable path, or a denied handle reports
    /// <see langword="false"/> instead of throwing, so a probe can never take the installation over.
    /// </summary>
    public static bool TryAcquire(string lockFilePath, out ControlInstallationLock? held)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockFilePath);
        held = null;
        try
        {
            var fullPath = Path.GetFullPath(lockFilePath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var stream = new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            held = new ControlInstallationLock(fullPath, stream);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Releases the lock. Disposing twice is a no-op.</summary>
    public void Dispose()
        => Interlocked.Exchange(ref _stream, null)?.Dispose();
}
