using System.Text;
using System.Text.Json;
using Rag.Companion.Ingestion;

namespace Rag.Companion.Tests;

public sealed class FailureRecorderTests
{
    [Fact]
    public async Task RecordAsync_writes_one_json_line_with_error_code()
    {
        using var writer = new StringWriter();
        var recorder = new FailureRecorder(writer);

        await recorder.RecordAsync("extraction_failed");

        var lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var entry = Assert.Single(lines);
        Assert.Equal("extraction_failed", JsonSerializer.Deserialize<JsonElement>(entry).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task RecordAsync_omits_paths_and_secrets()
    {
        using var writer = new StringWriter();
        var recorder = new FailureRecorder(writer);
        var secret = "s3cret-value-1234567890";
        var path = "/home/admin/dropbox/report.doc";

        await recorder.RecordAsync("operation_failed");

        var output = writer.ToString();
        Assert.Contains("operation_failed", output, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
        Assert.DoesNotContain(path, output, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/", output, StringComparison.Ordinal);
        Assert.DoesNotContain("dropbox", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordAsync_writes_one_line_per_failure()
    {
        using var writer = new StringWriter();
        var recorder = new FailureRecorder(writer);

        await recorder.RecordAsync("extraction_failed");
        await recorder.RecordAsync("operation_failed");
        await recorder.RecordAsync("extracted_text_too_large");

        var lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public async Task RecordAsync_rejects_a_blank_error_code()
    {
        var recorder = new FailureRecorder(new StringWriter());

        await Assert.ThrowsAsync<ArgumentException>(() => recorder.RecordAsync("   "));
    }
}
