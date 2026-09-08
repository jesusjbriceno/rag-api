using System.Net;
using System.Net.Http;
using System.Text.Json;
using Rag.Companion.Ingestion;

namespace Rag.Companion.Tests;

public sealed class IngestionClientTests
{
    private const string BaseUrl = "https://api.test";
    private const string KeyId = "key-123";
    private const string Secret = "s3cret-value";

    [Fact]
    public async Task SubmitTextAsync_derives_same_external_reference_for_same_normalized_text()
    {
        var api = new FakeIngestionApi();
        var client = CreateClient(api);

        await client.SubmitTextAsync(Guid.NewGuid(), "a.txt", "hello\n");
        await client.SubmitTextAsync(Guid.NewGuid(), "b.txt", "hello\n");

        var references = api.Requests
            .Where(r => r.Kind == RequestKind.Ingestion)
            .Select(r => JsonSerializer.Deserialize<JsonElement>(r.Body!).GetProperty("external_reference").GetString())
            .ToList();

        Assert.Equal(2, references.Count);
        Assert.NotNull(references[0]);
        Assert.Equal(references[0], references[1]);
        Assert.Equal("5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03", references[0]);
    }

    [Fact]
    public async Task SubmitTextAsync_caches_the_access_token()
    {
        var api = new FakeIngestionApi();
        var client = CreateClient(api);

        await client.SubmitTextAsync(Guid.NewGuid(), "a.txt", "one");
        await client.SubmitTextAsync(Guid.NewGuid(), "b.txt", "two");

        Assert.Equal(1, api.TokenRequestCount);
    }

    [Fact]
    public async Task SubmitTextAsync_duplicate_200_resolves_without_polling()
    {
        var api = new FakeIngestionApi
        {
            IngestionStatus = HttpStatusCode.OK,
            IngestionOperationId = null,
        };
        var client = CreateClient(api);

        var result = await client.SubmitTextAsync(Guid.NewGuid(), "a.txt", "one");

        Assert.True(result.IsDuplicate);
        Assert.Null(result.OperationId);
        Assert.Equal(0, api.PollRequestCount);
    }

    [Fact]
    public async Task SubmitTextAsync_rejects_duplicate_200_with_operation_id()
    {
        var api = new FakeIngestionApi
        {
            IngestionStatus = HttpStatusCode.OK,
            IngestionOperationId = Guid.NewGuid(),
        };
        var client = CreateClient(api);

        var exception = await Assert.ThrowsAsync<IngestionException>(() => client.SubmitTextAsync(Guid.NewGuid(), "a.txt", "one"));

        Assert.Equal(IngestionErrorCodes.InvalidResponse, exception.ErrorCode);
        Assert.False(exception.IsTransient);
    }

    [Fact]
    public async Task SubmitTextAsync_rejects_accepted_202_without_operation_id()
    {
        var api = new FakeIngestionApi
        {
            IngestionStatus = HttpStatusCode.Accepted,
            IngestionOperationId = null,
        };
        var client = CreateClient(api);

        var exception = await Assert.ThrowsAsync<IngestionException>(() => client.SubmitTextAsync(Guid.NewGuid(), "a.txt", "one"));

        Assert.Equal(IngestionErrorCodes.InvalidResponse, exception.ErrorCode);
        Assert.False(exception.IsTransient);
    }

    [Fact]
    public async Task SubmitTextAsync_polls_accepted_operation_to_terminal_success()
    {
        var api = new FakeIngestionApi();
        api.EnqueuePollStatuses("pending", "running", "succeeded");
        var client = CreateClient(api);

        var result = await client.SubmitTextAsync(Guid.NewGuid(), "a.txt", "one");

        Assert.False(result.IsDuplicate);
        Assert.NotNull(result.OperationId);
        Assert.Equal(3, api.PollRequestCount);
    }

    [Fact]
    public async Task SubmitTextAsync_fails_when_poll_reaches_terminal_failure()
    {
        var api = new FakeIngestionApi();
        api.EnqueuePollStatuses("pending", "failed");
        var client = CreateClient(api);

        var exception = await Assert.ThrowsAsync<IngestionException>(() => client.SubmitTextAsync(Guid.NewGuid(), "a.txt", "one"));

        Assert.Equal(IngestionErrorCodes.OperationFailed, exception.ErrorCode);
        Assert.False(exception.IsTransient);
    }

    [Fact]
    public async Task SubmitTextAsync_does_not_retry_an_unauthorized_response()
    {
        var api = new FakeIngestionApi { IngestionStatus = HttpStatusCode.Unauthorized };
        var client = CreateClient(api);

        var exception = await Assert.ThrowsAsync<IngestionException>(() => client.SubmitTextAsync(Guid.NewGuid(), "a.txt", "one"));

        Assert.Equal(IngestionErrorCodes.Unauthorized, exception.ErrorCode);
        Assert.False(exception.IsTransient);
        Assert.Equal(1, api.IngestionRequestCount);
    }

    [Fact]
    public async Task SubmitTextAsync_retries_a_transient_ingestion_failure_three_times()
    {
        var api = new FakeIngestionApi { IngestionStatus = HttpStatusCode.ServiceUnavailable };
        var client = CreateClient(api);

        var exception = await Assert.ThrowsAsync<IngestionException>(() => client.SubmitTextAsync(Guid.NewGuid(), "a.txt", "one"));

        Assert.Equal(IngestionErrorCodes.TemporarilyUnavailable, exception.ErrorCode);
        Assert.True(exception.IsTransient);
        Assert.Equal(RetryPolicy.MaxAttempts, api.IngestionRequestCount);
    }

    private static IngestionClient CreateClient(FakeIngestionApi api)
    {
        var http = new HttpClient(api);
        var retry = new RetryPolicy(jitter: () => 0, delay: (_, _) => Task.CompletedTask);
        return new IngestionClient(
            http,
            BaseUrl,
            KeyId,
            Secret,
            clock: new FixedClock(),
            retry: retry,
            delay: (_, _) => Task.CompletedTask);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }
}
