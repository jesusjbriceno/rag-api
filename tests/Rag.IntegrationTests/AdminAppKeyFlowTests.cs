using System.Net;
using Rag.AdminApp.Host;

namespace Rag.IntegrationTests;

/// <summary>
/// Idempotency-key coverage for the AdminApp BFF: every browser mutation must supply a
/// valid UUID <c>Idempotency-Key</c>, the BFF forwards that exact key unchanged into the
/// upstream request and its HMAC canonical string, and an HTTP retry of one logical
/// operation reuses the same key so the admin API can deduplicate. Guard enforcement
/// and proxy signing contracts live in the sibling test files.
/// </summary>
public sealed class AdminAppKeyFlowTests : IDisposable
{
    private readonly AdminAppBffFactory _factory = new();
    private readonly HttpClient _client;

    public AdminAppKeyFlowTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Missing_idempotency_key_is_rejected_before_the_admin_api()
    {
        var credential = AdminAppBffTestRequests.CreateCloudflareToken(_factory.CfKey, AdminAppBffFactory.AllowedSubject, DateTimeOffset.UtcNow);

        var response = await _client.PostAsync("/api/admin/clients", "{\"name\":\"acme\"}", credential, AdminAppBffFactory.AllowedOrigin, idempotencyKey: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_idempotency_key", await response.ProblemCodeAsync());
        Assert.Empty(_factory.Upstream.Requests);
    }

    [Fact]
    public async Task Malformed_idempotency_keys_are_rejected_before_the_admin_api()
    {
        var credential = AdminAppBffTestRequests.CreateCloudflareToken(_factory.CfKey, AdminAppBffFactory.AllowedSubject, DateTimeOffset.UtcNow);

        // The BFF requires the canonical "D" UUID form: free text, braces, the compact
        // "N" form, and whitespace variants all fail closed before any upstream call.
        var malformed = new[]
        {
            "not-a-uuid",
            "{6F9619FF-8B86-D011-B42D-00C04FC964FF}",
            "6F9619FF8B86D011B42D00C04FC964FF",
            " ",
            $"{AdminAppBffTestRequests.DefaultIdempotencyKey}/extra",
        };

        foreach (var key in malformed)
        {
            var response = await _client.PostAsync("/api/admin/clients", "{\"name\":\"acme\"}", credential, AdminAppBffFactory.AllowedOrigin, idempotencyKey: key);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid_idempotency_key", await response.ProblemCodeAsync());
        }

        Assert.Empty(_factory.Upstream.Requests);
    }

    [Fact]
    public async Task Exact_caller_idempotency_key_is_forwarded_and_bound_into_the_hmac_proof()
    {
        var callerKey = "0f8fad5b-d9cb-469f-a165-70867728950e";
        var credential = AdminAppBffTestRequests.CreateCloudflareToken(_factory.CfKey, AdminAppBffFactory.AllowedSubject, DateTimeOffset.UtcNow);

        var response = await _client.PostAsync("/api/admin/clients", "{\"name\":\"acme\"}", credential, AdminAppBffFactory.AllowedOrigin, idempotencyKey: callerKey);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // The upstream request carries the caller's key unchanged, and the API's HMAC
        // verification succeeds with that same key in the canonical string: the BFF
        // neither regenerates nor mutates the logical operation's key.
        var recorded = Assert.Single(_factory.Upstream.Requests);
        Assert.Equal(callerKey, recorded.Headers["Idempotency-Key"]);
        Assert.True(AdminAppBffTestRequests.CreateVerifier().Verify(
            AdminAppBffTestRequests.ProofFrom(recorded), recorded.Headers["X-Admin-Signature"], DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Http_retry_of_one_logical_operation_reuses_the_same_idempotency_key()
    {
        var callerKey = "e902893a-9d22-4a4d-3ba5-1a3b2c3d4e5f";
        var credential = AdminAppBffTestRequests.CreateCloudflareToken(_factory.CfKey, AdminAppBffFactory.AllowedSubject, DateTimeOffset.UtcNow);

        var first = await _client.PostAsync("/api/admin/clients", "{\"name\":\"acme\"}", credential, AdminAppBffFactory.AllowedOrigin, idempotencyKey: callerKey);
        var retry = await _client.PostAsync("/api/admin/clients", "{\"name\":\"acme\"}", credential, AdminAppBffFactory.AllowedOrigin, idempotencyKey: callerKey);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);

        // Both attempts reach upstream with the identical caller key, so the admin API
        // can deduplicate the logical mutation; each attempt still carries its own fresh
        // short-lived assertion.
        Assert.Equal(2, _factory.Upstream.Requests.Count);
        Assert.All(_factory.Upstream.Requests, recorded =>
        {
            Assert.Equal(callerKey, recorded.Headers["Idempotency-Key"]);
            Assert.True(AdminAppBffTestRequests.CreateVerifier().Verify(
                AdminAppBffTestRequests.ProofFrom(recorded), recorded.Headers["X-Admin-Signature"], DateTimeOffset.UtcNow));
        });
        Assert.NotEqual(_factory.Upstream.Requests[0].AssertionJws, _factory.Upstream.Requests[1].AssertionJws);
    }
}
