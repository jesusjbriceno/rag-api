using System.Text;
using Microsoft.Data.Sqlite;
using Rag.HistoricalLoader.Core.Benchmark;
using Rag.HistoricalLoader.Core.Data;
using Rag.HistoricalLoader.Core.Persistence;
using Rag.HistoricalLoader.Core.Sampling;
using Rag.HistoricalLoader.Engine;

namespace Rag.HistoricalLoader.IntegrationTests.Benchmark;

/// <summary>
/// Wires the preserved real-extractor components to <c>benchmark extraction</c>: the
/// <c>--companion</c> opt-in selects real local extraction through the reflection adapter;
/// the default remains the synthetic reference identity. Only duration, byte count,
/// outcome/error code are persisted — never document content or source paths.
/// </summary>
public sealed class BenchmarkExtractionWiringTests : IDisposable
{
    private const string MarkdownContent = "benchmark-wire-content";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"wiring-{Guid.NewGuid():N}");

    public BenchmarkExtractionWiringTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task WithCompanion_Doc_ReportsUnsupportedPolicy()
    {
        // Legacy DOC is intentionally unsupported; users should convert DOC files to DOCX.
        var (databasePath, sampleSetId, bytes) = await BuildFixtureAsync(".doc", writeFile: true);

        var companion = LocateCompanionAssembly();
        Assert.True(File.Exists(companion), $"Companion assembly not found at '{companion}'.");

        var engine = new HistoricalLoaderEngine();
        var result = await engine.RunBenchmarkExtractionAsync(new BenchmarkExtractionRequest(
            databasePath, sampleSetId, Options(sampleSetId), companion));

        Assert.Equal(1, result.ObservationCount);
        Assert.Equal("companion-reflection", result.Report.Environment.AdapterName);

        var (normalized, source, outcome, error) = await ReadObservationAsync(databasePath, sampleSetId);
        Assert.Equal(0, normalized);
        Assert.Equal(bytes, source);
        Assert.Equal((int)BenchmarkOutcome.Error, outcome);
        Assert.Equal(LocalExtractionErrorCodes.DocLibreOfficeRequired, error);

        var columns = await ReadColumnNamesAsync(databasePath);
        Assert.DoesNotContain(columns, c => c.Contains("path", StringComparison.OrdinalIgnoreCase)
            || c.Contains("content", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WithCompanion_Markdown_ExtractsRealNormalizedBytes()
    {
        var (databasePath, sampleSetId, _) = await BuildFixtureAsync(".md", writeFile: true);

        var engine = new HistoricalLoaderEngine();
        var result = await engine.RunBenchmarkExtractionAsync(new BenchmarkExtractionRequest(
            databasePath, sampleSetId, Options(sampleSetId), LocateCompanionAssembly()));

        Assert.Equal(1, result.ObservationCount);
        Assert.Equal("companion-reflection", result.Report.Environment.AdapterName);

        var (normalized, _, outcome, error) = await ReadObservationAsync(databasePath, sampleSetId);
        Assert.Equal(Encoding.UTF8.GetByteCount(MarkdownContent), normalized);
        Assert.Equal((int)BenchmarkOutcome.Completed, outcome);
        Assert.Null(error);
    }

    [Fact]
    public async Task WithoutCompanion_KeepsSyntheticReferenceIdentity()
    {
        var (databasePath, sampleSetId, bytes) = await BuildFixtureAsync(".doc", writeFile: false);

        var engine = new HistoricalLoaderEngine();
        var result = await engine.RunBenchmarkExtractionAsync(new BenchmarkExtractionRequest(
            databasePath, sampleSetId, Options(sampleSetId)));

        Assert.Equal(1, result.ObservationCount);
        Assert.Equal("reference-identity", result.Report.Environment.AdapterName);

        var (normalized, source, outcome, error) = await ReadObservationAsync(databasePath, sampleSetId);
        Assert.Equal(source, normalized); // synthetic identity: no real extraction
        Assert.Equal(bytes, source);
        Assert.Equal((int)BenchmarkOutcome.Completed, outcome);
        Assert.Null(error);
    }

    private static ProbeOptions Options(Guid sampleSetId) => new()
    {
        SampleSetId = sampleSetId,
        SustainedDuration = TimeSpan.Zero,
    };

    private async Task<(string DatabasePath, Guid SampleSetId, long Bytes)> BuildFixtureAsync(string extension, bool writeFile)
    {
        var databasePath = Path.Combine(_dir, "store.sqlite");
        var rootPath = Path.Combine(_dir, "corpus");
        Directory.CreateDirectory(rootPath);

        var content = extension == ".md" ? MarkdownContent : "legacy";
        var samplePath = Path.Combine(rootPath, "sample" + extension);
        if (writeFile)
        {
            if (extension == ".doc")
            {
                await File.WriteAllBytesAsync(samplePath,
                    [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0x00]);
            }
            else
            {
                await File.WriteAllTextAsync(samplePath, content);
            }
        }

        var bytes = extension == ".doc" && writeFile
            ? 9
            : Encoding.UTF8.GetByteCount(content);
        var rootId = Guid.NewGuid();
        var manifestId = Guid.NewGuid();
        var sampleSetId = Guid.NewGuid();
        var candidateId = Guid.NewGuid();

        await using (var store = new SqliteStore(databasePath))
        {
            await store.InitializeAsync();
            await store.AddSourceRootAsync(new SourceRoot(rootId, "docs", rootPath, DateTimeOffset.UtcNow));
            await store.CreateManifestAsync(new Manifest(manifestId, 1, ManifestState.Complete, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await store.AddCandidateAsync(new Candidate(candidateId, manifestId, rootId, "sample" + extension, extension, bytes, DateTimeOffset.UtcNow, "eligible"));
        }

        await using (var store = new SampleSetStore(databasePath))
        {
            await store.InitializeAsync();
            await store.SaveAsync(
                new SampleSet(sampleSetId, manifestId, "v1", "splitmix64", 42, 1, 1, "95%|10%", true, true, DateTimeOffset.UtcNow, "{}"),
                [new SampleMember(sampleSetId, candidateId, extension + "|1", "fp-0", 0)]);
        }

        return (databasePath, sampleSetId, bytes);
    }

    private static async Task<(long Normalized, long Source, int Outcome, string? Error)> ReadObservationAsync(string databasePath, Guid sampleSetId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT normalized_bytes, source_bytes, outcome, error_code FROM benchmark_observation WHERE sample_set_id = $id;";
        command.Parameters.AddWithValue("$id", sampleSetId.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "Expected one persisted observation.");
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private static async Task<IReadOnlyList<string>> ReadColumnNamesAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(benchmark_observation);";
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(1));
        }

        return names;
    }

    private static string LocateCompanionAssembly()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Rag.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName
            ?? throw new InvalidOperationException("Repository root (Rag.sln) not found above the test output directory.");
        foreach (var config in new[] { "Release", "Debug" })
        {
            var candidate = Path.Combine(root, "src", "Rag.Companion", "bin", config, "net10.0", "win-x64", "Rag.Companion.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(root, "src", "Rag.Companion", "bin", "Release", "net10.0", "win-x64", "Rag.Companion.dll");
    }
}
