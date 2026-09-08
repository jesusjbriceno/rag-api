using System.IO;

namespace Rag.Companion.Snapshot;

/// <summary>
/// Discovers supported documents below administrator-selected roots only. Discovery is bounded:
/// reparse points, symlinks, and junctions are rejected outright (a conservative superset of
/// "reject escape" that also prevents directory cycles). Temp files and non-document manifests
/// are skipped.
/// </summary>
public sealed class ConfinedSnapshotService
{
    public static readonly IReadOnlySet<string> AllowedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".doc", ".docx", ".md", ".pdf", ".txt" };

    public static readonly IReadOnlySet<string> TempExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".tmp", ".dropbox" };

    /// <summary>
    /// Dependency/build manifests that carry a document extension (.txt) but are not documents
    /// and MUST NOT be ingested (e.g. <c>requirements.txt</c>, <c>CMakeLists.txt</c>).
    /// </summary>
    public static readonly IReadOnlySet<string> NonDocumentBaseNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "requirements", "cmakelists" };

    /// <summary>Returns the absolute paths of discovered files, in deterministic (sorted) order.</summary>
    public IReadOnlyList<string> Discover(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var normalizedRoots = new List<string>();
        foreach (var root in roots)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(root);
            var fullPath = Path.GetFullPath(root);
            if (!Directory.Exists(fullPath))
            {
                throw new DirectoryNotFoundException($"Configured root does not exist: '{fullPath}'.");
            }
            if (IsLink(new DirectoryInfo(fullPath)))
            {
                throw new IOException($"Configured root is a reparse point, symlink, or junction: '{fullPath}'.");
            }
            normalizedRoots.Add(Path.TrimEndingDirectorySeparator(fullPath));
        }

        var results = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var root in normalizedRoots)
        {
            Collect(root, root, results);
        }

        return results.ToList();
    }

    private static void Collect(string root, string directory, ISet<string> results)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if (IsLink(entry))
            {
                continue; // reject reparse points, symlinks, and junctions
            }

            if (entry is DirectoryInfo subdirectory)
            {
                Collect(root, subdirectory.FullName, results);
            }
            else if (entry is FileInfo file)
            {
                if (TempExtensions.Contains(file.Extension))
                {
                    continue;
                }
                if (NonDocumentBaseNames.Contains(Path.GetFileNameWithoutExtension(file.Name)))
                {
                    continue;
                }
                if (AllowedExtensions.Contains(file.Extension))
                {
                    results.Add(file.FullName);
                }
            }
        }
    }

    internal static bool IsLink(FileSystemInfo info)
    {
        try
        {
            return info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return true; // fail closed: unreadable attributes are treated as suspicious
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
