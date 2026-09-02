using System.Security.Cryptography;
using System.Text.Json;
using Rag.Companion.Adapters;

namespace Rag.Companion.Tests;

/// <summary>
/// Verifies the redistributable import fixtures against their manifest: every fixture named in the
/// manifest exists and its SHA-256 matches, and the DOCX fixture extracts the expected text.
/// </summary>
public sealed class ImportFixtureManifestTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void Manifest_sha256_matches_every_fixture()
    {
        var fixturesDir = FixturesDirectory();
        var manifest = JsonSerializer.Deserialize<FixtureManifest>(
            File.ReadAllText(Path.Combine(fixturesDir, "manifest.json")),
            JsonOptions);
        Assert.NotNull(manifest);

        Assert.Equal(5, manifest.Fixtures.Count);
        foreach (var fixture in manifest.Fixtures)
        {
            var path = Path.Combine(fixturesDir, fixture.Name);
            Assert.True(File.Exists(path), $"Fixture missing: {fixture.Name}");
            Assert.Equal(fixture.Sha256, Sha256Of(path));
        }
    }

    [Fact]
    public async Task Docx_fixture_extracts_expected_text()
    {
        var path = Path.Combine(FixturesDirectory(), "sample.docx");
        var text = await new DocxAdapter().ExtractAsync(path);

        Assert.Contains("Historical Import Companion sample document.", text);
        Assert.Contains("This DOCX verifies Open XML extraction.", text);
    }

    [Fact]
    public async Task Pdf_fixture_extracts_expected_text()
    {
        var path = Path.Combine(FixturesDirectory(), "sample.pdf");
        var text = await new PdfAdapter().ExtractAsync(path);

        Assert.Contains("Historical Import Companion sample document.", text);
    }

    private static string FixturesDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Rag.sln")))
            {
                return Path.Combine(directory.FullName, "tests", "fixtures", "import");
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root (Rag.sln).");
    }

    private static string Sha256Of(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private sealed record FixtureManifest(List<FixtureEntry> Fixtures);

    private sealed record FixtureEntry(string Name, string Format, string Origin, string License, string Sha256);
}
