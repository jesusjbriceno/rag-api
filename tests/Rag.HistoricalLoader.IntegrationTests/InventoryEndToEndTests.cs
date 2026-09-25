using System.Security.Cryptography;
using System.Text.Json;
using Rag.HistoricalLoader.Core.Data;
using Rag.HistoricalLoader.Core.Persistence;
using Rag.HistoricalLoader.Engine;

namespace Rag.HistoricalLoader.IntegrationTests;

public sealed class InventoryEndToEndTests
{
    private const string Sentinel = "UNIQUE_SENTINEL_CONTENT_1c9f4b2c";

    [Fact]
    public async Task Inventory_RunsEndToEndAndReconcilesWithStoredRows()
    {
        var dir = NewDirectory();
        try
        {
            var root = Path.Combine(dir, "root");
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "a.docx"), Sentinel);
            await File.WriteAllTextAsync(Path.Combine(root, "b.txt"), Sentinel);
            var unicode = Path.Combine(root, "café_文档");
            Directory.CreateDirectory(unicode);
            await File.WriteAllTextAsync(Path.Combine(unicode, "c.md"), Sentinel);
            Directory.CreateSymbolicLink(Path.Combine(root, "junction"), unicode);

            var databasePath = Path.Combine(dir, "store.sqlite");
            var output = Path.Combine(dir, "export");
            var engine = new HistoricalLoaderEngine();

            var result = await engine.RunInventoryAsync(new InventoryRunRequest(
                databasePath,
                output,
                [new SourceRoot(Guid.NewGuid(), "docs", root, DateTimeOffset.UtcNow)],
                MaxCandidateBytes: 10L * 1024 * 1024));

            Assert.True(result.Export.IsComplete);

            await using (var store = new SqliteStore(databasePath))
            {
                await store.InitializeAsync();
                Assert.Equal(4, await store.CountCandidatesAsync(result.ManifestId));
                Assert.Equal(ManifestState.Complete, await store.GetManifestStateAsync(result.ManifestId));
            }

            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(result.Export.ManifestJsonPath));
            var rootElement = manifest.RootElement;
            Assert.Equal("complete", rootElement.GetProperty("state").GetString());
            Assert.Equal(4, rootElement.GetProperty("totals").GetProperty("candidateCount").GetInt32());
            Assert.Equal(0, rootElement.GetProperty("totals").GetProperty("errorCount").GetInt32());

            var candidatesHash = await Sha256Async(result.Export.CandidatesNdjsonPath);
            Assert.Equal(candidatesHash, result.Export.CandidatesNdjsonSha256);
            Assert.Equal(candidatesHash, rootElement.GetProperty("files").GetProperty("candidatesNdjson").GetProperty("sha256").GetString());

            var summary = await File.ReadAllTextAsync(result.Export.SummaryMdPath);
            Assert.Contains("# Inventory summary", summary);
            Assert.Contains("Candidates: 4", summary);

            var everything = await File.ReadAllTextAsync(result.Export.ManifestJsonPath)
                + await File.ReadAllTextAsync(result.Export.CandidatesNdjsonPath)
                + summary;
            Assert.DoesNotContain(Sentinel, everything);
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task Inventory_PartialEnumerationExportsInspectableIncompleteManifest()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // POSIX permission semantics are not available on Windows.
        }

        var dir = NewDirectory();
        try
        {
            var root = Path.Combine(dir, "root");
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "ok.txt"), Sentinel);
            var locked = Path.Combine(root, "locked");
            Directory.CreateDirectory(locked);
            await File.WriteAllTextAsync(Path.Combine(locked, "hidden.txt"), Sentinel);
            File.SetUnixFileMode(locked, UnixFileMode.None);
            try
            {
                var databasePath = Path.Combine(dir, "store.sqlite");
                var engine = new HistoricalLoaderEngine();

                var result = await engine.RunInventoryAsync(new InventoryRunRequest(
                    databasePath,
                    Path.Combine(dir, "export"),
                    [new SourceRoot(Guid.NewGuid(), "docs", root, DateTimeOffset.UtcNow)],
                    MaxCandidateBytes: 10L * 1024 * 1024));

                Assert.False(result.Export.IsComplete);
                using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(result.Export.ManifestJsonPath));
                Assert.Equal("scanning", manifest.RootElement.GetProperty("state").GetString());
                Assert.Equal(1, manifest.RootElement.GetProperty("totals").GetProperty("candidateCount").GetInt32());
                Assert.Equal(1, manifest.RootElement.GetProperty("totals").GetProperty("errorCount").GetInt32());
            }
            finally
            {
                File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static string NewDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rag-e2e-{Guid.NewGuid():N}");
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
