using Rag.Companion.Snapshot;

namespace Rag.Companion.Tests;

public sealed class ConfinedSnapshotServiceTests : IDisposable
{
    private readonly string _root;
    private readonly ConfinedSnapshotService _service = new();

    public ConfinedSnapshotServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "snap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => TryDelete(_root);

    [Fact]
    public void Discover_accepts_readme_md_and_ignores_non_documents()
    {
        File.WriteAllText(Path.Combine(_root, "README.md"), "hi");
        File.WriteAllText(Path.Combine(_root, "requirements.txt"), "dep");
        File.WriteAllText(Path.Combine(_root, "run.sh"), "#!/bin/sh");
        File.WriteAllText(Path.Combine(_root, "notes.mdx"), "notes");

        var files = _service.Discover([_root]);

        Assert.Single(files);
        Assert.Equal(Path.Combine(_root, "README.md"), files[0]);
    }

    [Fact]
    public void Discover_skips_temp_files()
    {
        File.WriteAllText(Path.Combine(_root, "draft.txt"), "draft");
        File.WriteAllText(Path.Combine(_root, "draft.tmp"), "temp");
        File.WriteAllText(Path.Combine(_root, "sync.dropbox"), "partial");

        var files = _service.Discover([_root]);

        Assert.Single(files);
        Assert.Equal(Path.Combine(_root, "draft.txt"), files[0]);
    }

    [Fact]
    public void Discover_rejects_file_symlink_escaping_root()
    {
        var outside = Path.Combine(Path.GetTempPath(), "snap-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            var outsideFile = Path.Combine(outside, "secret.txt");
            File.WriteAllText(outsideFile, "secret");
            File.WriteAllText(Path.Combine(_root, "README.md"), "hi");

            File.CreateSymbolicLink(Path.Combine(_root, "link.txt"), outsideFile);

            var files = _service.Discover([_root]);

            Assert.Single(files);
            Assert.Equal(Path.Combine(_root, "README.md"), files[0]);
        }
        finally
        {
            TryDelete(outside);
        }
    }

    [Fact]
    public void Discover_rejects_directory_symlink_escaping_root()
    {
        var outside = Path.Combine(Path.GetTempPath(), "snap-outdir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
            Directory.CreateSymbolicLink(Path.Combine(_root, "linked"), outside);

            var files = _service.Discover([_root]);

            Assert.Empty(files);
        }
        finally
        {
            TryDelete(outside);
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
