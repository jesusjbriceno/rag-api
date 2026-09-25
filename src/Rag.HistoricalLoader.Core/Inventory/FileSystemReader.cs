namespace Rag.HistoricalLoader.Core.Inventory;

public sealed record FileSystemEntry(
    string Path,
    bool IsDirectory,
    bool IsReparsePoint,
    long ByteSize,
    DateTimeOffset LastWriteTime,
    string? AccessErrorCode = null);

public interface IFileSystemReader
{
    IReadOnlyList<FileSystemEntry> Enumerate(string directoryPath);
}

/// <summary>
/// Metadata-only filesystem reader. It never opens file content; it only reads
/// directory entries and file attributes/sizes/timestamps.
/// </summary>
public sealed class PhysicalFileSystemReader : IFileSystemReader
{
    public IReadOnlyList<FileSystemEntry> Enumerate(string directoryPath)
    {
        var entries = new List<FileSystemEntry>();
        foreach (var path in Directory.EnumerateFileSystemEntries(directoryPath))
        {
            entries.Add(ReadEntry(path));
        }

        return entries;
    }

    private static FileSystemEntry ReadEntry(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            var isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;
            var isDirectory = (attributes & FileAttributes.Directory) != 0;

            if (isReparsePoint)
            {
                return new FileSystemEntry(path, isDirectory, true, 0, DateTimeOffset.MinValue);
            }

            if (isDirectory)
            {
                var directory = new DirectoryInfo(path);
                return new FileSystemEntry(path, true, false, 0, directory.LastWriteTimeUtc);
            }

            var file = new FileInfo(path);
            return new FileSystemEntry(path, false, false, file.Length, file.LastWriteTimeUtc);
        }
        catch (Exception exception) when (IsMetadataFailure(exception))
        {
            return new FileSystemEntry(path, Directory.Exists(path), false, 0, DateTimeOffset.MinValue, EnumerationErrorCodes.AccessDenied);
        }
    }

    private static bool IsMetadataFailure(Exception exception)
        => exception is UnauthorizedAccessException or FileNotFoundException or IOException;
}
