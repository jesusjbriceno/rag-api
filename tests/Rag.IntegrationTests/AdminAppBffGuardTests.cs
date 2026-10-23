using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using Rag.AdminApp.Host;

namespace Rag.IntegrationTests;

/// <summary>
/// Guard coverage for the Cloudflare-guarded AdminApp BFF: identity and allowlist
/// enforcement, same-origin protection for mutations, the fixed two-operation surface,
/// and request limits. Every rejection must happen before any upstream request is
/// built. Proxy-flow and idempotency behavior live in the sibling test files.
/// </summary>
public sealed class AdminAppBffGuardTests : IDisposable
{
    private readonly AdminAppBffFactory _factory = new();
    private readonly HttpClient _client;

    public AdminAppBffGuardTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Health_endpoint_serves_without_a_cloudflare_credential()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("healthy", body);
    }

    [Fact]
    public async Task Missing_credential_is_rejected_without_reaching_the_admin_api()
    {
        var response = await _client.PostAsync("/api/admin/clients", "{\"name\":\"acme\"}", credential: null, origin: AdminAppBffFactory.AllowedOrigin);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthorized", await response.ProblemCodeAsync());
        Assert.Empty(_factory.Upstream.Requests);
    }

    [Fact]
    public async Task Invalid_credential_is_rejected_without_reaching_the_admin_api()
    {
        using var foreignKey = RSA.Create(2048);
        var now = DateTimeOffset.UtcNow;

        var foreign = await _client.PostAsync("/api/admin/clients", "{\"name\":\"acme\"}",
            AdminAppBffTestRequests.CreateCloudflareToken(foreignKey, AdminAppBffFactory.AllowedSubject, now), AdminAppBffFactory.AllowedOrigin);
        var expired = await _client.PostAsync("/api/admin/clients", "{\"name\":\"acme\"}",
            AdminAppBffTestRequests.CreateCloudflareToken(_factory.CfKey, AdminAppBffFactory.AllowedSubject, now.AddHours(-2)), AdminAppBffFactory.AllowedOrigin);
        var garbage = await _client.PostAsync("/api/admin/clients", "{\"name\":\"acme\"}", "not-a-token", AdminAppBffFactory.AllowedOrigin);

        Assert.Equal(HttpStatusCode.Unauthorized, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, garbage.StatusCode);
        Assert.Empty(_factory.Upstream.Requests);
    }

    [Fact]
    public async Task Disallowed_subject_is_rejected_without_reaching_the_admin_api()
    {
        var response = await _client.PostAsync("/api/admin/clients", "{\"name\":\"acme\"}",
            AdminAppBffTestRequests.CreateCloudflareToken(_factory.CfKey, "cf-stranger-subject", DateTimeOffset.UtcNow), AdminAppBffFactory.AllowedOrigin);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("not_allowlisted", await response.ProblemCodeAsync());
        Assert.Empty(_factory.Upstream.Requests);
    }

    [Fact]
    public async Task Browser_supplied_identity_headers_are_rejected_without_reaching_the_admin_api()
    {
        var credential = AdminAppBffTestRequests.CreateCloudflareToken(_factory.CfKey, AdminAppBffFactory.AllowedSubject, DateTimeOffset.UtcNow);

        var forwardedUser = await _client.PostAsync("/api/admin/clients", "{\"name\":\"acme\"}", credential, AdminAppBffFactory.AllowedOrigin, "X-Forwarded-User", "attacker");
        var remoteUser = await _client.PostAsync("/api/admin/clients", "{\"name\":\"acme\"}", credential, AdminAppBffFactory.AllowedOrigin, "X-Remote-User", "attacker");

        Assert.Equal(HttpStatusCode.BadRequest, forwardedUser.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, remoteUser.StatusCode);
        Assert.Empty(_factory.Upstream.Requests);
    }

    [Fact]
    public async Task Cloudflare_injected_identity_headers_are_tolerated_and_never_forwarded_upstream()
    {
        // Cloudflare Access injects these on every request that reaches the origin, so a
        // legitimate browser request always carries them. They are not a browser identity
        // claim: only the credential is trusted, and no edge header may be forwarded.
        var credential = AdminAppBffTestRequests.CreateCloudflareToken(_factory.CfKey, AdminAppBffFactory.AllowedSubject, DateTimeOffset.UtcNow);

        var withEmail = await _client.PostAsync("/api/admin/clients", "{\"name\":\"acme\"}", credential, AdminAppBffFactory.AllowedOrigin, "Cf-Access-Authenticated-User-Email", "attacker@example.com");
        var withGroups = await _client.PostAsync("/api/admin/clients", "{\"name\":\"acme\"}", credential, AdminAppBffFactory.AllowedOrigin, "Cf-Access-Authenticated-User-Groups", "admins");

        Assert.Equal(HttpStatusCode.Created, withEmail.StatusCode);
        Assert.Equal(HttpStatusCode.Created, withGroups.StatusCode);

        Assert.Equal(2, _factory.Upstream.Requests.Count);
        foreach (var recorded in _factory.Upstream.Requests)
        {
            Assert.DoesNotContain("Cf-Access-Authenticated-User-Email", recorded.Headers.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("Cf-Access-Authenticated-User-Groups", recorded.Headers.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("Cf-Access-Jwt-Assertion", recorded.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Cross_site_mutations_are_rejected_without_reaching_the_admin_api()
    {
        var credential = AdminAppBffTestRequests.CreateCloudflareToken(_factory.CfKey, AdminAppBffFactory.AllowedSubject, DateTimeOffset.UtcNow);

        var missingOrigin = await _client.PostAsync("/api/admin/clients", "{\"name\":\"acme\"}", credential, origin: null);
        var foreignOrigin = await _client.PostAsync("/api/admin/clients", "{\"name\":\"acme\"}", credential, "https://evil.example.com");

        Assert.Equal(HttpStatusCode.Forbidden, missingOrigin.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreignOrigin.StatusCode);
        Assert.Equal("cross_origin_rejected", await foreignOrigin.ProblemCodeAsync());
        Assert.Empty(_factory.Upstream.Requests);
    }

    [Fact]
    public async Task Arbitrary_proxy_paths_are_not_available()
    {
        var credential = AdminAppBffTestRequests.CreateCloudflareToken(_factory.CfKey, AdminAppBffFactory.AllowedSubject, DateTimeOffset.UtcNow);

        var upstreamStyle = await _client.PostAsync("/api/v1/admin/clients", "{\"name\":\"acme\"}", credential, AdminAppBffFactory.AllowedOrigin);
        var listAttempt = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/admin/clients"));
        var rotateAttempt = await _client.PostAsync($"/api/admin/clients/{Guid.NewGuid()}/credentials:rotate", "{}", credential, AdminAppBffFactory.AllowedOrigin);
        var malformedId = await _client.PostAsync("/api/admin/clients/not-a-guid/credentials", "{}", credential, AdminAppBffFactory.AllowedOrigin);

        Assert.Equal(HttpStatusCode.NotFound, upstreamStyle.StatusCode);
        Assert.True(
            listAttempt.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
            $"expected 404 or 405 for the list attempt, got {listAttempt.StatusCode}");
        Assert.Equal(HttpStatusCode.NotFound, rotateAttempt.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, malformedId.StatusCode);
        Assert.Empty(_factory.Upstream.Requests);
    }

    [Fact]
    public void Empty_allowlist_fails_closed_at_startup()
    {
        using var factory = new AdminAppBffFactory(allowedSubjects: []);

        Assert.ThrowsAny<InvalidOperationException>(() => factory.CreateClient());
    }

    [Fact]
    public async Task Oversized_body_is_rejected_without_reaching_the_admin_api()
    {
        var credential = AdminAppBffTestRequests.CreateCloudflareToken(_factory.CfKey, AdminAppBffFactory.AllowedSubject, DateTimeOffset.UtcNow);

        var response = await _client.PostAsync("/api/admin/clients", $"{{\"name\":\"{new string('x', 1_048_600)}\"}}", credential, AdminAppBffFactory.AllowedOrigin);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(_factory.Upstream.Requests);
    }
}
