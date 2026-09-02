namespace Rag.Companion.Tests;

/// <summary>
/// Verifies the companion documentation covers the required sections and that every local reference
/// it makes resolves to a real file on disk.
/// </summary>
public sealed class CompanionDocsTests
{
    [Fact]
    public void Documentation_lists_dependencies_and_sections()
    {
        var doc = File.ReadAllText(DocsPath());

        Assert.Contains("LibreOffice", doc);
        Assert.Contains("DocumentFormat.OpenXml", doc);
        Assert.Contains("PdfPig", doc);
        Assert.Contains("## Install", doc);
        Assert.Contains("## Operation", doc);
        Assert.Contains("## License notices", doc);
        Assert.Contains("## Rollback", doc);
        Assert.Contains("## Prerequisites", doc);
    }

    [Fact]
    public void Documentation_references_resolve_to_existing_files()
    {
        var root = RepoRoot();
        var doc = File.ReadAllText(DocsPath());

        foreach (var relative in new[]
                 {
                     "scripts/companion-bff-smoke.sh",
                     "tests/fixtures/import/manifest.json",
                     "src/Rag.Companion/",
                 })
        {
            Assert.Contains(relative, doc);
            var path = Path.Combine(root, relative);
            Assert.True(File.Exists(path) || Directory.Exists(path), $"Documentation reference does not resolve: {relative}");
        }
    }

    private static string DocsPath() => Path.Combine(RepoRoot(), "docs", "historical-import-companion.md");

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Rag.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root (Rag.sln).");
    }
}
