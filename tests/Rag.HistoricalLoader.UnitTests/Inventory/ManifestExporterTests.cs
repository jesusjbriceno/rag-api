using System.Security.Cryptography;
using System.Text.Json;
using Rag.HistoricalLoader.Core.Classification;
using Rag.HistoricalLoader.Core.Data;
using Rag.HistoricalLoader.Core.Inventory;

namespace Rag.HistoricalLoader.UnitTests.Inventory;

public sealed class ManifestExporterTests
{
    [Fact]
    public async Task Export_WritesVersionedIndexAndReconcilesCandidateStream()
    {
        var dir = NewDirectory();
        try
        {
            var exporter = new ManifestExporter();

            var result = await exporter.ExportAsync(CompleteSnapshot(), Path.Combine(dir, "export"));

            Assert.True(result.IsComplete);
            Assert.True(File.Exists(result.ManifestJsonPath));
            Assert.True(File.Exists(result.CandidatesNdjsonPath));
            Assert.True(File.Exists(result.SummaryMdPath));
            Assert.Empty(Directory.GetFiles(Path.Combine(dir, "export"), "*.tmp-*"));

            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(result.ManifestJsonPath));
            var root = manifest.RootElement;
            Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("complete", root.GetProperty("state").GetString());
            Assert.Equal(2, root.GetProperty("totals").GetProperty("candidateCount").GetInt32());
            Assert.Equal(1, root.GetProperty("totals").GetProperty("errorCount").GetInt32());
            Assert.Equal(150, root.GetProperty("totals").GetProperty("totalBytes").GetInt64());
            Assert.Equal(1, root.GetProperty("eligibility").GetProperty("eligible").GetInt32());
            Assert.Equal(1, root.GetProperty("eligibility").GetProperty("unsupported_format").GetInt32());

            var lines = await File.ReadAllLinesAsync(result.CandidatesNdjsonPath);
            Assert.Equal(2, lines.Length);
            Assert.Equal("a.docx", JsonDocument.Parse(lines[0]).RootElement.GetProperty("relativePath").GetString());
            Assert.Equal("eligible", JsonDocument.Parse(lines[0]).RootElement.GetProperty("eligibilityCode").GetString());
            Assert.Equal("b.xlsx", JsonDocument.Parse(lines[1]).RootElement.GetProperty("relativePath").GetString());
            Assert.Equal("unsupported_format", JsonDocument.Parse(lines[1]).RootElement.GetProperty("eligibilityCode").GetString());
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task Export_RecordsSha256HashesThatReconcileAgainstArtifactBytes()
    {
        var dir = NewDirectory();
        try
        {
            var exporter = new ManifestExporter();

            var result = await exporter.ExportAsync(CompleteSnapshot(), Path.Combine(dir, "export"));

            Assert.Equal(await Sha256Async(result.ManifestJsonPath), result.ManifestJsonSha256);
            Assert.Equal(await Sha256Async(result.CandidatesNdjsonPath), result.CandidatesNdjsonSha256);
            Assert.Equal(await Sha256Async(result.SummaryMdPath), result.SummaryMdSha256);

            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(result.ManifestJsonPath));
            var files = manifest.RootElement.GetProperty("files");
            Assert.Equal(result.CandidatesNdjsonSha256, files.GetProperty("candidatesNdjson").GetProperty("sha256").GetString());
            Assert.Equal(result.SummaryMdSha256, files.GetProperty("summaryMd").GetProperty("sha256").GetString());
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task Export_PartialScanNeverSatisfiesCompletionSemantics()
    {
        var dir = NewDirectory();
        try
        {
            var exporter = new ManifestExporter();

            var result = await exporter.ExportAsync(ScanningSnapshot(), Path.Combine(dir, "export"));

            Assert.False(result.IsComplete);
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(result.ManifestJsonPath));
            Assert.Equal("scanning", manifest.RootElement.GetProperty("state").GetString());
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task Export_AtomicallyReplacesPreExistingArtifacts()
    {
        var dir = NewDirectory();
        try
        {
            var output = Path.Combine(dir, "export");
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), "garbage");

            var exporter = new ManifestExporter();

            var result = await exporter.ExportAsync(CompleteSnapshot(), output);

            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(result.ManifestJsonPath));
            Assert.Equal(1, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Empty(Directory.GetFiles(output, "*.tmp-*"));
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task Export_SummaryIsHumanReadable()
    {
        var dir = NewDirectory();
        try
        {
            var exporter = new ManifestExporter();

            var result = await exporter.ExportAsync(CompleteSnapshot(), Path.Combine(dir, "export"));

            var summary = await File.ReadAllTextAsync(result.SummaryMdPath);
            Assert.Contains("# Inventory summary", summary);
            Assert.Contains("Candidates: 2", summary);
            Assert.Contains("unsupported_format: 1", summary);
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    private static ManifestSnapshot CompleteSnapshot() => Snapshot(ManifestState.Complete);

    private static ManifestSnapshot ScanningSnapshot() => Snapshot(ManifestState.Scanning);

    private static ManifestSnapshot Snapshot(ManifestState state)
    {
        var manifestId = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        return new ManifestSnapshot(
            manifestId,
            1,
            state,
            DateTimeOffset.UtcNow,
            state == ManifestState.Complete ? DateTimeOffset.UtcNow : null,
            [
                new Candidate(Guid.NewGuid(), manifestId, rootId, "a.docx", ".docx", 100, DateTimeOffset.UtcNow, EligibilityCodes.Eligible),
                new Candidate(Guid.NewGuid(), manifestId, rootId, "b.xlsx", ".xlsx", 50, DateTimeOffset.UtcNow, EligibilityCodes.UnsupportedFormat, ".xlsx"),
            ],
            ErrorCount: 1);
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static string NewDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rag-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteDirectory(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
