using Rag.Domain;
using Rag.Infrastructure;

namespace Rag.UnitTests;

public sealed class FileSystemImmutableContentStoreTests
{
    [Fact]
    public async Task Existing_content_reference_cannot_be_overwritten_with_different_content()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"rag-content-store-{Guid.NewGuid():N}");
        try
        {
            var store = new FileSystemImmutableContentStore(rootPath);
            var reference = ContentReference.ForVersion(Guid.NewGuid());
            var first = "first"u8.ToArray();
            var replacement = "replacement"u8.ToArray();

            await store.StoreAsync(reference, ContentHash.FromBytes(first), first, CancellationToken.None);

            await Assert.ThrowsAsync<IOException>(() =>
                store.StoreAsync(reference, ContentHash.FromBytes(replacement), replacement, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Unsafe_content_reference_is_rejected_before_writing()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"rag-content-store-{Guid.NewGuid():N}");
        try
        {
            var store = new FileSystemImmutableContentStore(rootPath);

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.StoreAsync(
                new ContentReference("../outside.txt"),
                ContentHash.FromBytes("content"u8),
                "content"u8.ToArray(),
                CancellationToken.None));

            Assert.False(Directory.Exists(rootPath));
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Symbolic_linked_content_directory_is_rejected_without_escaping_root()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"rag-content-store-{Guid.NewGuid():N}");
        var outsidePath = Path.Combine(Path.GetTempPath(), $"rag-content-outside-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(rootPath);
            Directory.CreateDirectory(outsidePath);
            Directory.CreateSymbolicLink(Path.Combine(rootPath, "versions"), outsidePath);
            var store = new FileSystemImmutableContentStore(rootPath);

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.StoreAsync(
                ContentReference.ForVersion(Guid.NewGuid()),
                ContentHash.FromBytes("content"u8),
                "content"u8.ToArray(),
                CancellationToken.None));

            Assert.Empty(Directory.EnumerateFiles(outsidePath));
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }

            if (Directory.Exists(outsidePath))
            {
                Directory.Delete(outsidePath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Cancellation_removes_the_same_directory_temporary_file()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"rag-content-store-{Guid.NewGuid():N}");
        try
        {
            var store = new FileSystemImmutableContentStore(rootPath);
            var reference = ContentReference.ForVersion(Guid.NewGuid());
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.StoreAsync(
                reference,
                ContentHash.FromBytes("content"u8),
                "content"u8.ToArray(),
                cancellation.Token));

            var versionDirectory = Path.Combine(rootPath, "versions");
            Assert.Empty(Directory.EnumerateFiles(versionDirectory));
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Concurrent_versions_directory_swaps_cannot_write_outside_the_root()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var rootPath = Path.Combine(Path.GetTempPath(), $"rag-content-store-{Guid.NewGuid():N}");
        var versionsPath = Path.Combine(rootPath, "versions");
        var holdingPath = Path.Combine(rootPath, "versions-holding");
        var outsidePath = Path.Combine(Path.GetTempPath(), $"rag-content-outside-{Guid.NewGuid():N}");
        using var cancellation = new CancellationTokenSource();
        try
        {
            Directory.CreateDirectory(versionsPath);
            Directory.CreateDirectory(outsidePath);
            var store = new FileSystemImmutableContentStore(rootPath);
            var swapper = Task.Run(() => SwapVersionsDirectoryUntilCancelled(
                versionsPath,
                holdingPath,
                outsidePath,
                cancellation.Token));
            var content = new byte[1024 * 1024];
            Random.Shared.NextBytes(content);

            for (var index = 0; index < 32; index++)
            {
                try
                {
                    await store.StoreAsync(
                        ContentReference.ForVersion(Guid.NewGuid()),
                        ContentHash.FromBytes(content),
                        content,
                        CancellationToken.None);
                }
                catch (IOException)
                {
                    // A concurrent replacement can make the service-owned directory unavailable.
                }
                catch (InvalidOperationException)
                {
                    // A concurrent replacement can expose a symbolic link, which must be rejected.
                }
            }

            cancellation.Cancel();
            var swaps = await swapper;
            RestoreVersionsDirectory(versionsPath, holdingPath);
            Assert.True(swaps > 0);
            Assert.Empty(Directory.EnumerateFiles(outsidePath));
        }
        finally
        {
            cancellation.Cancel();
            RestoreVersionsDirectory(versionsPath, holdingPath);
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }

            if (Directory.Exists(outsidePath))
            {
                Directory.Delete(outsidePath, recursive: true);
            }
        }
    }

    private static int SwapVersionsDirectoryUntilCancelled(
        string versionsPath,
        string holdingPath,
        string outsidePath,
        CancellationToken cancellationToken)
    {
        var swaps = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!IsSymbolicLink(versionsPath) && Directory.Exists(versionsPath) && !Directory.Exists(holdingPath))
                {
                    Directory.Move(versionsPath, holdingPath);
                }

                if (!Directory.Exists(versionsPath))
                {
                    Directory.CreateSymbolicLink(versionsPath, outsidePath);
                    swaps++;
                }

                Thread.Yield();

                if (IsSymbolicLink(versionsPath))
                {
                    Directory.Delete(versionsPath);
                }

                if (!Directory.Exists(versionsPath) && Directory.Exists(holdingPath))
                {
                    Directory.Move(holdingPath, versionsPath);
                }
            }
            catch (IOException)
            {
                // Writers can create or move the directory between swap steps.
            }
        }

        return swaps;
    }

    private static void RestoreVersionsDirectory(string versionsPath, string holdingPath)
    {
        if (IsSymbolicLink(versionsPath))
        {
            Directory.Delete(versionsPath);
        }

        if (!Directory.Exists(versionsPath) && Directory.Exists(holdingPath))
        {
            Directory.Move(holdingPath, versionsPath);
        }
    }

    private static bool IsSymbolicLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }
}
