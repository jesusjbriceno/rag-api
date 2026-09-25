using Rag.HistoricalLoader.Core.Classification;

namespace Rag.HistoricalLoader.Core.Inventory;

public static class EnumerationErrorCodes
{
    public const string AccessDenied = "access_denied";
    public const string DirectoryEnumerationFailed = "directory_enumeration_failed";
    public const string PathEscape = "path_escape";
}

public sealed record EnumerationError(string RootPath, string Path, string ErrorCode);

public sealed record RootScanOutcome(Guid RootId, string RootPath, bool ReachedTerminal);

public static class PathEscapeGuard
{
    public static bool IsWithinRoot(string rootPath, string candidatePath)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(rootPath), Path.GetFullPath(candidatePath));
        if (relative == ".." || Path.IsPathRooted(relative))
        {
            return false;
        }

        return !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && (Path.AltDirectorySeparatorChar == Path.DirectorySeparatorChar
                || !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal));
    }
}

public sealed class RootConfinedWalker
{
    private readonly IFileSystemReader _fileSystem;

    public RootConfinedWalker(IFileSystemReader fileSystem) => _fileSystem = fileSystem;

    public async Task<(bool ReachedTerminal, IReadOnlyList<EnumerationError> Errors)> WalkAsync(
        string rootPath,
        Func<CandidateDiscovery, DateTimeOffset, Task> onDiscovery)
    {
        var fullRoot = Path.GetFullPath(rootPath);
        var errors = new List<EnumerationError>();
        var complete = await WalkDirectoryAsync(fullRoot, fullRoot, onDiscovery, errors).ConfigureAwait(false);
        return (complete, errors);
    }

    private async Task<bool> WalkDirectoryAsync(
        string root,
        string directory,
        Func<CandidateDiscovery, DateTimeOffset, Task> onDiscovery,
        List<EnumerationError> errors)
    {
        IReadOnlyList<FileSystemEntry> entries;
        try
        {
            entries = _fileSystem.Enumerate(directory);
        }
        catch (Exception exception) when (IsEnumerationFailure(exception))
        {
            errors.Add(new EnumerationError(root, directory, EnumerationErrorCodes.DirectoryEnumerationFailed));
            return false;
        }

        var complete = true;
        foreach (var entry in entries)
        {
            if (!PathEscapeGuard.IsWithinRoot(root, entry.Path))
            {
                errors.Add(new EnumerationError(root, entry.Path, EnumerationErrorCodes.PathEscape));
                complete = false;
                continue;
            }

            if (entry.IsDirectory && !entry.IsReparsePoint)
            {
                if (entry.AccessErrorCode is not null)
                {
                    errors.Add(new EnumerationError(root, entry.Path, entry.AccessErrorCode));
                    complete = false;
                }
                else
                {
                    complete &= await WalkDirectoryAsync(root, entry.Path, onDiscovery, errors).ConfigureAwait(false);
                }
            }
            else
            {
                var discovery = new CandidateDiscovery(
                    Path.GetRelativePath(root, entry.Path),
                    entry.ByteSize,
                    entry.IsReparsePoint,
                    entry.AccessErrorCode);
                await onDiscovery(discovery, entry.LastWriteTime).ConfigureAwait(false);
            }
        }

        return complete;
    }

    private static bool IsEnumerationFailure(Exception exception)
        => exception is UnauthorizedAccessException or DirectoryNotFoundException or IOException;
}
