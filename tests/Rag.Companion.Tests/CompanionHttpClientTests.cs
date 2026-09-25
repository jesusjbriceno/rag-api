using System.Net;
using System.Net.Http;
using Rag.Companion.Protocol;

namespace Rag.Companion.Tests;

public sealed class CompanionHttpClientTests
{
    private const string Secret = "s3cret-value-1234567890abcdef";
    private const string CompanionId = "companion-0001";
    private const string KeyId = "key-0001";

    [Fact]
    public async Task AcquireLease_204_returns_no_work()
    {
        var handler = new CompanionBffHandler(Secret, CompanionId, KeyId, TimeProvider.System);
        var client = CreateClient(handler);

        var lease = await client.AcquireLeaseAsync();

        Assert.Null(lease);
    }

    [Fact]
    public async Task AcquireLease_200_returns_lease()
    {
        var handler = new CompanionBffHandler(Secret, CompanionId, KeyId, TimeProvider.System)
        {
            NextLease = new LeaseResponse { JobId = "job-1", CollectionId = Guid.NewGuid(), LeaseId = "lease-1", LeaseExpiresAt = 1735689660, NextSequence = 1, Snapshot = true },
        };
        var client = CreateClient(handler);

        var lease = await client.AcquireLeaseAsync();

        Assert.NotNull(lease);
        Assert.Equal("job-1", lease.JobId);
        Assert.Equal("lease-1", lease.LeaseId);
    }

    [Theory]
    [InlineData("wrong-secret", CompanionId, KeyId)]   // bad HMAC
    [InlineData(Secret, "other-companion", KeyId)]     // unknown companion id
    [InlineData(Secret, CompanionId, "other-key")]     // unknown key id
    public async Task AcquireLease_rejects_bad_credentials_with_401(string secret, string companionId, string keyId)
    {
        var handler = new CompanionBffHandler(Secret, CompanionId, KeyId, TimeProvider.System);
        var client = CreateClient(handler, secret, companionId, keyId);

        var exception = await Assert.ThrowsAsync<CompanionHttpException>(() => client.AcquireLeaseAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
    }

    [Fact]
    public async Task AcquireLease_stale_timestamp_is_rejected_with_401()
    {
        var handler = new CompanionBffHandler(Secret, CompanionId, KeyId, TimeProvider.System);
        var client = CreateClient(handler, clock: new FixedClock(DateTimeOffset.UtcNow.AddMinutes(-5)));

        var exception = await Assert.ThrowsAsync<CompanionHttpException>(() => client.AcquireLeaseAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict)]   // stale lease / regressed sequence
    [InlineData(HttpStatusCode.BadRequest)] // forbidden field
    public async Task SendEvent_maps_rejection_status(HttpStatusCode status)
    {
        var handler = new CompanionBffHandler(Secret, CompanionId, KeyId, TimeProvider.System) { ForcedStatus = status };
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<CompanionHttpException>(() => client.SendEventAsync("job-1", Event()));

        Assert.Equal(status, exception.StatusCode);
    }

    [Fact]
    public async Task SendEvent_duplicate_eventId_returns_replayed()
    {
        var handler = new CompanionBffHandler(Secret, CompanionId, KeyId, TimeProvider.System)
        {
            NextEvent = new EventResponse { AcceptedSequence = 1, State = CompanionState.Running, Replayed = true },
        };
        var client = CreateClient(handler);

        var response = await client.SendEventAsync("job-1", Event());

        Assert.True(response.Replayed);
    }

    [Fact]
    public async Task Retry_reuses_exact_body_and_eventId_with_fresh_envelope()
    {
        var handler = new CompanionBffHandler(Secret, CompanionId, KeyId, TimeProvider.System);
        var client = CreateClient(handler);
        var request = Event();

        await client.SendEventAsync("job-1", request);
        await client.SendEventAsync("job-1", request);

        var first = handler.SignedRequests[0];
        var second = handler.SignedRequests[1];
        Assert.Equal(first.BodySha256, second.BodySha256);
        Assert.Equal(first.Body, second.Body);
        Assert.NotEqual(first.Nonce, second.Nonce);
        Assert.NotEqual(first.Signature, second.Signature);
        Assert.Equal(2, handler.SeenNonces.Count);
    }

    private static CompanionHttpClient CreateClient(
        HttpMessageHandler handler,
        string secret = Secret,
        string companionId = CompanionId,
        string keyId = KeyId,
        TimeProvider? clock = null)
    {
        var http = new HttpClient(handler);
        var credentials = new CompanionCredentials("https://bff.test", companionId, keyId, secret);
        return new CompanionHttpClient(http, credentials, clock);
    }

    private static EventRequest Event() =>
        new() { LeaseId = "lease-1", EventId = "evt-1", Sequence = 1, State = CompanionState.Running, Processed = 0, Failed = 0 };

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
