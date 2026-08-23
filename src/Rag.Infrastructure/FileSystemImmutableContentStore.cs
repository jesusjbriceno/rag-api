using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Rag.Application;
using Rag.Domain;

namespace Rag.Infrastructure;

/// <summary>
/// Stores immutable content beneath a service-owned Linux directory.
/// Descriptor-relative operations bind every operation to the opened root and versions directory inodes, so a
/// concurrent path-name swap cannot redirect writes or deletes outside that root. Other platforms fail closed.
/// </summary>
public sealed class FileSystemImmutableContentStore(string rootPath) : IImmutableContentStore
{
    private static readonly Regex ExpectedReference = new(
        "^versions/[0-9a-f]{32}\\.txt$",
        RegexOptions.CultureInvariant);

    private readonly string _rootPath = Path.GetFullPath(rootPath);

    public async Task StoreAsync(ContentReference reference, ContentHash contentHash, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        if (ContentHash.FromBytes(content.Span) != contentHash)
        {
            throw new InvalidOperationException("The supplied content does not match its SHA-256 hash.");
        }

        var fileName = ResolveFileName(reference);
        EnsureLinux();
        using var rootDirectory = OpenRootDirectory();
        using var versionsDirectory = OpenOrCreateVersionsDirectory(rootDirectory);
        string? temporaryFileName = $".{fileName}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = CreateNewFile(versionsDirectory, temporaryFileName))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (LinuxNative.LinkAt(FileDescriptor(versionsDirectory), temporaryFileName, FileDescriptor(versionsDirectory), fileName, 0) == 0)
            {
                DeleteFileBestEffort(versionsDirectory, temporaryFileName);
                temporaryFileName = null;
                await VerifyHashAsync(versionsDirectory, fileName, contentHash, cancellationToken);
                return;
            }

            var error = Marshal.GetLastPInvokeError();
            if (error != LinuxNative.EExist)
            {
                ThrowForLinuxError("link content file", error);
            }

            await VerifyHashAsync(versionsDirectory, fileName, contentHash, cancellationToken);
        }
        finally
        {
            if (temporaryFileName is not null)
            {
                DeleteFileBestEffort(versionsDirectory, temporaryFileName);
            }
        }
    }

    public Task DeleteAsync(ContentReference reference, CancellationToken cancellationToken)
    {
        var fileName = ResolveFileName(reference);
        EnsureLinux();
        using var rootDirectory = OpenRootDirectory();
        using var versionsDirectory = OpenOrCreateVersionsDirectory(rootDirectory);

        try
        {
            using var existingFile = OpenExistingFile(versionsDirectory, fileName);
        }
        catch (FileNotFoundException)
        {
            return Task.CompletedTask;
        }

        if (LinuxNative.UnlinkAt(FileDescriptor(versionsDirectory), fileName, 0) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != LinuxNative.ENoEnt)
            {
                ThrowForLinuxError("delete content file", error);
            }
        }

        return Task.CompletedTask;
    }

    public async Task<byte[]> ReadAsync(ContentReference reference, ContentHash contentHash, CancellationToken cancellationToken)
    {
        var fileName = ResolveFileName(reference);
        EnsureLinux();
        using var rootDirectory = OpenRootDirectory();
        using var versionsDirectory = OpenOrCreateVersionsDirectory(rootDirectory);
        await using var stream = OpenExistingFile(versionsDirectory, fileName);
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        var content = memory.ToArray();
        if (ContentHash.FromBytes(content) != contentHash)
        {
            throw new IOException("The immutable content does not match its expected hash.");
        }

        return content;
    }

    private static async Task VerifyHashAsync(SafeFileHandle directory, string fileName, ContentHash expectedHash, CancellationToken cancellationToken)
    {
        await using var stream = OpenExistingFile(directory, fileName);
        var actualHash = new ContentHash(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)));
        if (actualHash != expectedHash)
        {
            throw new IOException("An existing content file does not match its expected hash.");
        }
    }

    private string ResolveFileName(ContentReference reference)
    {
        if (!ExpectedReference.IsMatch(reference.Value))
        {
            throw new InvalidOperationException("The content reference is not a recognized immutable-version reference.");
        }

        return Path.GetFileName(reference.Value);
    }

    private SafeFileHandle OpenRootDirectory()
    {
        Directory.CreateDirectory(_rootPath);
        return OpenDirectory(_rootPath);
    }

    private static SafeFileHandle OpenOrCreateVersionsDirectory(SafeFileHandle rootDirectory)
    {
        if (LinuxNative.MkdirAt(FileDescriptor(rootDirectory), "versions", LinuxNative.DirectoryPermissions) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != LinuxNative.EExist)
            {
                ThrowForLinuxError("create versions directory", error);
            }
        }

        return OpenDirectoryAt(rootDirectory, "versions");
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        var descriptor = LinuxNative.Open(path, LinuxNative.ReadOnly | LinuxNative.Directory | LinuxNative.NoFollow | LinuxNative.CloseOnExec, 0);
        if (descriptor < 0)
        {
            ThrowForLinuxError("open content root", Marshal.GetLastPInvokeError());
        }

        return ToSafeFileHandle(descriptor);
    }

    private static SafeFileHandle OpenDirectoryAt(SafeFileHandle parentDirectory, string path)
    {
        var descriptor = LinuxNative.OpenAt(
            FileDescriptor(parentDirectory),
            path,
            LinuxNative.ReadOnly | LinuxNative.Directory | LinuxNative.NoFollow | LinuxNative.CloseOnExec,
            0);
        if (descriptor < 0)
        {
            ThrowForLinuxError("open content directory", Marshal.GetLastPInvokeError());
        }

        return ToSafeFileHandle(descriptor);
    }

    private static FileStream CreateNewFile(SafeFileHandle parentDirectory, string fileName)
    {
        var descriptor = LinuxNative.OpenAt(
            FileDescriptor(parentDirectory),
            fileName,
            LinuxNative.WriteOnly | LinuxNative.Create | LinuxNative.Exclusive | LinuxNative.NoFollow | LinuxNative.CloseOnExec,
            LinuxNative.FilePermissions);
        if (descriptor < 0)
        {
            ThrowForLinuxError("create temporary content file", Marshal.GetLastPInvokeError());
        }

        return new FileStream(ToSafeFileHandle(descriptor), FileAccess.Write, bufferSize: 4096, isAsync: false);
    }

    private static FileStream OpenExistingFile(SafeFileHandle parentDirectory, string fileName)
    {
        var descriptor = LinuxNative.OpenAt(
            FileDescriptor(parentDirectory),
            fileName,
            LinuxNative.ReadOnly | LinuxNative.NoFollow | LinuxNative.NonBlocking | LinuxNative.CloseOnExec,
            0);
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == LinuxNative.ENoEnt)
            {
                throw new FileNotFoundException("The content file does not exist.", fileName);
            }

            ThrowForLinuxError("open content file", error);
        }

        return new FileStream(ToSafeFileHandle(descriptor), FileAccess.Read, bufferSize: 4096, isAsync: false);
    }

    private static void DeleteFileBestEffort(SafeFileHandle parentDirectory, string fileName)
    {
        if (LinuxNative.UnlinkAt(FileDescriptor(parentDirectory), fileName, 0) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != LinuxNative.ENoEnt)
            {
                // The original storage failure must remain observable.
            }
        }
    }

    private static SafeFileHandle ToSafeFileHandle(int descriptor) => new((IntPtr)descriptor, ownsHandle: true);

    private static int FileDescriptor(SafeFileHandle handle) => checked((int)handle.DangerousGetHandle());

    private static void EnsureLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Immutable content storage requires Linux descriptor-relative file operations.");
        }
    }

    private static void ThrowForLinuxError(string operation, int error)
    {
        if (error is LinuxNative.ELoop or LinuxNative.ENotDir)
        {
            throw new InvalidOperationException("Content storage paths must not traverse symbolic links.");
        }

        throw new IOException($"Unable to {operation}; Linux errno: {error}.");
    }

    private static class LinuxNative
    {
        internal const int ReadOnly = 0;
        internal const int WriteOnly = 1;
        internal const int Create = 0x40;
        internal const int Exclusive = 0x80;
        internal const int NonBlocking = 0x800;
        internal const int NoFollow = 0x20000;
        internal const int Directory = 0x10000;
        internal const int CloseOnExec = 0x80000;
        internal const int ENoEnt = 2;
        internal const int EExist = 17;
        internal const int ENotDir = 20;
        internal const int ELoop = 40;
        internal const uint DirectoryPermissions = 0x1C0;
        internal const uint FilePermissions = 0x180;

        [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int Open(string path, int flags, uint mode);

        [DllImport("libc", EntryPoint = "openat", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int OpenAt(int directoryDescriptor, string path, int flags, uint mode);

        [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int MkdirAt(int directoryDescriptor, string path, uint mode);

        [DllImport("libc", EntryPoint = "linkat", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int LinkAt(int oldDirectoryDescriptor, string oldPath, int newDirectoryDescriptor, string newPath, int flags);

        [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int UnlinkAt(int directoryDescriptor, string path, int flags);
    }
}
