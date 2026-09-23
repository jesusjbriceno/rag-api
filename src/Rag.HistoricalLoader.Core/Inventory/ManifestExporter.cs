using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rag.HistoricalLoader.Core.Data;

namespace Rag.HistoricalLoader.Core.Inventory;

public sealed record ManifestExportResult(
    string ManifestJsonPath,
    string CandidatesNdjsonPath,
    string SummaryMdPath,
    string ManifestJsonSha256,
    string CandidatesNdjsonSha256,
    string SummaryMdSha256,
    bool IsComplete);

/// <summary>
/// Atomically exports <c>manifest.json</c>, <c>candidates.ndjson</c>, and
/// <c>summary.md</c>. Each artifact is written to a temporary file, fsynced, and
/// atomically renamed into place; SHA-256 hashes are recorded in the index and
/// returned. A snapshot whose manifest is not complete is exported as inspectable
/// but never marked complete.
/// </summary>
public sealed class ManifestExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<ManifestExportResult> ExportAsync(ManifestSnapshot snapshot, string outputDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        Directory.CreateDirectory(outputDirectory);
        var manifestPath = Path.Combine(outputDirectory, "manifest.json");
        var candidatesPath = Path.Combine(outputDirectory, "candidates.ndjson");
        var summaryPath = Path.Combine(outputDirectory, "summary.md");

        var eligibility = EligibilityCounts(snapshot.Candidates);
        var totalBytes = snapshot.Candidates.Sum(candidate => candidate.ByteSize);

        var candidatesHash = await WriteCandidatesAsync(candidatesPath, snapshot.Candidates, cancellationToken);
        var summaryHash = await WriteTextAsync(summaryPath, BuildSummary(snapshot, totalBytes, eligibility), cancellationToken);
        var manifestHash = await WriteTextAsync(manifestPath, BuildManifest(snapshot, totalBytes, eligibility, candidatesHash, summaryHash), cancellationToken);

        return new ManifestExportResult(manifestPath, candidatesPath, summaryPath, manifestHash, candidatesHash, summaryHash, snapshot.IsComplete);
    }

    private static Dictionary<string, int> EligibilityCounts(IReadOnlyList<Candidate> candidates)
        => candidates.GroupBy(candidate => candidate.EligibilityCode, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    private static string BuildManifest(ManifestSnapshot snapshot, long totalBytes, IReadOnlyDictionary<string, int> eligibility, string candidatesHash, string summaryHash)
    {
        var index = new
        {
            schemaVersion = 1,
            manifestId = snapshot.ManifestId,
            scanVersion = snapshot.ScanVersion,
            state = snapshot.IsComplete ? "complete" : "scanning",
            startedAt = snapshot.StartTime,
            completedAt = snapshot.EndTime,
            totals = new { candidateCount = snapshot.Candidates.Count, errorCount = snapshot.ErrorCount, totalBytes },
            eligibility,
            files = new
            {
                candidatesNdjson = new { sha256 = candidatesHash },
                summaryMd = new { sha256 = summaryHash },
            },
        };

        return JsonSerializer.Serialize(index, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
    }

    private static string BuildSummary(ManifestSnapshot snapshot, long totalBytes, IReadOnlyDictionary<string, int> eligibility)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Inventory summary");
        builder.AppendLine();
        builder.AppendLine($"- Manifest: {snapshot.ManifestId}");
        builder.AppendLine($"- Scan version: {snapshot.ScanVersion}");
        builder.AppendLine($"- State: {(snapshot.IsComplete ? "complete" : "scanning")}");
        builder.AppendLine($"- Candidates: {snapshot.Candidates.Count}");
        builder.AppendLine($"- Enumeration errors: {snapshot.ErrorCount}");
        builder.AppendLine($"- Total bytes: {totalBytes}");
        builder.AppendLine();
        builder.AppendLine("## Eligibility breakdown");
        builder.AppendLine();
        foreach (var (code, count) in eligibility.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            builder.AppendLine($"- {code}: {count}");
        }

        return builder.ToString();
    }

    private static async Task<string> WriteCandidatesAsync(string path, IReadOnlyList<Candidate> candidates, CancellationToken cancellationToken)
    {
        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true))
        {
            foreach (var candidate in candidates)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(candidate, JsonOptions));
            }

            await writer.FlushAsync();
            stream.Flush(flushToDisk: true);
        }

        File.Move(tempPath, path, overwrite: true);
        return await ComputeSha256Async(path, cancellationToken);
    }

    private static async Task<string> WriteTextAsync(string path, string content, CancellationToken cancellationToken)
    {
        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(content), cancellationToken);
            stream.Flush(flushToDisk: true);
        }

        File.Move(tempPath, path, overwrite: true);
        return await ComputeSha256Async(path, cancellationToken);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }
}
