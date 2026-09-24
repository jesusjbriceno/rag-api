using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rag.Companion.Adapters;
using Rag.Companion.Host;
using Rag.Companion.Ingestion;
using Rag.Companion.Protocol;

namespace Rag.Companion.Tests;

public sealed class CompanionRunTests : IDisposable
{
    private const string Secret = "s3cret-value-1234567890abcdef";
    private const string CompanionId = "companion-0001";
    private const string KeyId = "key-0001";

    private readonly string _dir;

    public CompanionRunTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "companion-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => TryDelete(_dir);

    [Fact]
    public async Task No_lease_returns_success_without_scan_or_ingest()
    {
        var root = CreateRoot();
        File.WriteAllText(Path.Combine(root, "a.txt"), "Hello");
        var api = new FakeIngestionApi();
        var bff = new CompanionBffHandler(Secret, CompanionId, KeyId, TimeProvider.System); // no NextLease => 204
        var output = new StringWriter();
        var run = CreateRun(Config(root), bff, api, output);

        var code = await run.RunAsync();

        Assert.Equal(ExitCodes.Success, code);
        Assert.Empty(api.Requests);
        Assert.Single(bff.SignedRequests); // the lease request only; no events
    }

    [Fact]
    public async Task Successful_txt_run_processes_file_and_reports_succeeded()
    {
        var root = CreateRoot();
        File.WriteAllText(Path.Combine(root, "a.txt"), "Hello World");
        var api = new FakeIngestionApi();
        var bff = new CompanionBffHandler(Secret, CompanionId, KeyId, TimeProvider.System) { NextLease = Lease(nextSequence: 5) };
        var output = new StringWriter();
        var run = CreateRun(Config(root), bff, api, output);

        var code = await run.RunAsync();

        Assert.Equal(ExitCodes.Success, code);
        Assert.Equal(1, api.IngestionRequestCount);

        // external_reference is the SHA-256 of the normalized text, never the source bytes.
        var ingestion = api.Requests.Single(r => r.Kind == RequestKind.Ingestion);
        Assert.NotNull(ingestion.Body);
        using var body = JsonDocument.Parse(ingestion.Body);
        var expectedReference = ComputeSha256("Hello World\n");
        Assert.Equal(expectedReference, body.RootElement.GetProperty("external_reference").GetString());
        Assert.Equal("Hello World\n", body.RootElement.GetProperty("content").GetString());

        // A running event precedes a terminal succeeded event with the correct counts.
        var events = Events(bff);
        Assert.Equal(2, events.Count);
        Assert.Equal("running", events[0].GetProperty("state").GetString());
        Assert.Equal(5, events[0].GetProperty("sequence").GetInt64());
        Assert.Equal("succeeded", events[1].GetProperty("state").GetString());
        Assert.Equal(1, events[1].GetProperty("processed").GetInt64());
        Assert.Equal(0, events[1].GetProperty("failed").GetInt64());

        // No event body may carry a source path or file name.
        foreach (var request in bff.SignedRequests)
        {
            Assert.DoesNotContain(root, request.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("a.txt", request.Body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Failed_file_records_failure_and_reports_terminal_failure()
    {
        var root = CreateRoot();
        File.WriteAllBytes(Path.Combine(root, "bad.txt"), [0x48, 0x69, 0xFF, 0xFE]); // "Hi" + invalid UTF-8 (no leading BOM)
        var failureLog = Path.Combine(_dir, "failures.jsonl");
        var api = new FakeIngestionApi();
        var bff = new CompanionBffHandler(Secret, CompanionId, KeyId, TimeProvider.System) { NextLease = Lease(nextSequence: 1) };
        var output = new StringWriter();
        var run = CreateRun(Config(root, failureLog), bff, api, output);

        var code = await run.RunAsync();

        Assert.Equal(ExitCodes.TerminalFailure, code);
        Assert.Equal(0, api.IngestionRequestCount);

        var events = Events(bff);
        Assert.Equal(2, events.Count);
        Assert.Equal("failed", events[1].GetProperty("state").GetString());
        Assert.Equal(0, events[1].GetProperty("processed").GetInt64());
        Assert.Equal(1, events[1].GetProperty("failed").GetInt64());

        // The local failure log carries only an error code — never the source path.
        var log = File.ReadAllText(failureLog);
        Assert.Contains(ExtractionErrorCodes.ExtractionFailed, log);
        Assert.DoesNotContain(root, log, StringComparison.Ordinal);
        Assert.DoesNotContain("bad.txt", log, StringComparison.Ordinal);
        Assert.Contains("1 failed", output.ToString());
    }

    private static CompanionRun CreateRun(CompanionConfig config, CompanionBffHandler bff, FakeIngestionApi api, TextWriter output)
    {
        var clock = TimeProvider.System;
        var companion = new CompanionHttpClient(
            new HttpClient(bff),
            new CompanionCredentials(config.CompanionBaseUrl, config.CompanionId, config.CompanionKeyId, config.CompanionSecret),
            clock);
        var ingestion = new IngestionClient(
            new HttpClient(api),
            config.ApiBaseUrl,
            config.ServiceClientKeyId,
            config.ServiceClientSecret,
            clock);
        var registry = AdapterRegistry.CreateDefault();
        return new CompanionRun(config, companion, ingestion, registry, clock, output);
    }

    private CompanionConfig Config(string root, string? failureLogPath = null) => new()
    {
        ApiBaseUrl = "https://api.test",
        CompanionBaseUrl = "https://bff.test",
        CompanionId = CompanionId,
        CompanionKeyId = KeyId,
        CompanionSecret = Secret,
        ServiceClientKeyId = "svc-key",
        ServiceClientSecret = "svc-secret",
        Roots = [root],
        FailureLogPath = failureLogPath ?? Path.Combine(_dir, "failures.jsonl"),
    };

    private string CreateRoot()
    {
        var root = Path.Combine(_dir, "root");
        Directory.CreateDirectory(root);
        return root;
    }

    private static LeaseResponse Lease(long nextSequence) => new()
    {
        JobId = "job-1",
        CollectionId = Guid.NewGuid(),
        LeaseId = "lease-1",
        LeaseExpiresAt = 1735689660,
        NextSequence = nextSequence,
        Snapshot = true,
    };

    private static List<JsonElement> Events(CompanionBffHandler bff)
    {
        var events = new List<JsonElement>();
        foreach (var request in bff.SignedRequests)
        {
            using var doc = JsonDocument.Parse(request.Body);
            if (doc.RootElement.TryGetProperty("state", out _))
            {
                events.Add(doc.RootElement.Clone());
            }
        }

        return events;
    }

    private static string ComputeSha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
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
