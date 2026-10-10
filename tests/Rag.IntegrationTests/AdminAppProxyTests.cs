using System.Net;
using System.Security.Cryptography;
using System.Text;
using Rag.AdminApp.Host;

namespace Rag.IntegrationTests;

/// <summary>
/// Signed-bridge coverage for the two fixed admin proxy operations: the API's expected
/// HMAC machine-proof contract, assertions bound to the verified subject and configured
/// app id on both operations, safe response forwarding, redacted upstream failures, and
/// the absence of any forwarded browser header. Guard enforcement lives in the sibling
/// test file; idempotency-key flow lives in AdminAppKeyFlowTests.
/// </summary>
public sealed class AdminAppProxyTests : IDisposable
{
    private readonly AdminAppBffFactory _factory = new();
    private readonly HttpClient _client;

    public AdminAppProxyTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Allowed_administrator_creates_a_client_through_the_signed_proxy()
    {
        var credential = AdminAppBffTestRequests.CreateCloudflareToken(_factory.CfKey, AdminAppBffFactory.AllowedSubject, DateTimeOffset.UtcNow);
        _factory.Upstream.Responder = static _ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("{\"id\":\"d1c0ffee-0000-0000-0000-000000000001\",\"name\":\"acme\"}", Encoding.UTF8, "application/json"),
        };

        var response = await _client.PostAsync("/api/admin/clients", "{\"name\":\"acme\"}", credential, AdminAppBffFactory.AllowedOrigin);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains("d1c0ffee", await response.Content.ReadAsStringAsync());

        var recorded = Assert.Single(_factory.Upstream.Requests);
        Assert.Equal(HttpMethod.Post.Method, recorded.Method);
        Assert.Equal("/api/v1/admin/clients", recorded.PathAndQuery);
        Assert.Equal("{\"name\":\"acme\"}", Encoding.UTF8.GetString(recorded.Body));

        // The API's expected machine-proof contract holds: the HMAC verifies against the
        // configured secret and the assertion carries the verified subject and app id.
        Assert.True(AdminAppBffTestRequests.CreateVerifier().Verify(
            AdminAppBffTestRequests.ProofFrom(recorded), recorded.Headers["X-Admin-Signature"], DateTimeOffset.UtcNow));
        AssertBoundAssertion(recorded);
    }

    [Fact]
    public async Task Allowed_administrator_issues_a_credential_and_receives_the_secret_once()
    {
        var clientId = Guid.NewGuid();
        var credential = AdminAppBffTestRequests.CreateCloudflareToken(_factory.CfKey, AdminAppBffFactory.AllowedSubject, DateTimeOffset.UtcNow);
        _factory.Upstream.Responder = static _ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(
                $"{{\"credential\":{{\"id\":\"{Guid.NewGuid()}\"}},\"secret\":\"once-only-secret\"}}",
                Encoding.UTF8, "application/json"),
        };

        var response = await _client.PostAsync($"/api/admin/clients/{clientId}/credentials", "{\"description\":\"first\",\"expiresAt\":null}", credential, AdminAppBffFactory.AllowedOrigin);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("once-only-secret", body);

        // The secret travels in the one-time body only: never in response headers,
        // cookies, or location headers.
        Assert.DoesNotContain("once-only-secret", string.Join(";", response.Headers.SelectMany(h => h.Value)));
        Assert.False(response.Headers.Contains("Set-Cookie"));

        // The credential-issue operation is bound exactly like client creation: the HMAC
        // proof verifies and the assertion carries the verified subject and app id.
        var recorded = Assert.Single(_factory.Upstream.Requests);
        Assert.Equal($"/api/v1/admin/clients/{clientId}/credentials", recorded.PathAndQuery);
        Assert.True(AdminAppBffTestRequests.CreateVerifier().Verify(
            AdminAppBffTestRequests.ProofFrom(recorded), recorded.Headers["X-Admin-Signature"], DateTimeOffset.UtcNow));
        AssertBoundAssertion(recorded);
    }

    [Fact]
    public async Task Unrelated_browser_headers_are_never_forwarded_upstream()
    {
        var credential = AdminAppBffTestRequests.CreateCloudflareToken(_factory.CfKey, AdminAppBffFactory.AllowedSubject, DateTimeOffset.UtcNow);

        var createResponse = await _client.PostAsync(
            "/api/admin/clients", "{\"name\":\"acme\"}", credential, AdminAppBffFactory.AllowedOrigin,
            "X-Custom-Trace", "browser-trace-value");
        var issueResponse = await _client.PostAsync(
            $"/api/admin/clients/{Guid.NewGuid()}/credentials", "{\"description\":\"first\",\"expiresAt\":null}", credential, AdminAppBffFactory.AllowedOrigin,
            "X-Custom-Trace", "browser-trace-value");

        // Both proxy operations forward only the fixed signed header set: a browser
        // header unrelated to identity or signing must never reach the admin API.
        Assert.Equal(2, _factory.Upstream.Requests.Count);
        foreach (var recorded in _factory.Upstream.Requests)
        {
            Assert.False(recorded.Headers.ContainsKey("X-Custom-Trace"));
            Assert.DoesNotContain("browser-trace-value", string.Join(";", recorded.Headers.Values));
        }
    }

    [Fact]
    public async Task Upstream_conflict_contract_is_forwarded_verbatim()
    {
        var credential = AdminAppBffTestRequests.CreateCloudflareToken(_factory.CfKey, AdminAppBffFactory.AllowedSubject, DateTimeOffset.UtcNow);
        _factory.Upstream.Responder = static _ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("{\"title\":\"Conflict\",\"code\":\"duplicate\"}", Encoding.UTF8, "application/json"),
        };

        var response = await _client.PostAsync("/api/admin/clients", "{\"name\":\"acme\"}", credential, AdminAppBffFactory.AllowedOrigin);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("duplicate", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Upstream_server_failures_are_redacted()
    {
        var credential = AdminAppBffTestRequests.CreateCloudflareToken(_factory.CfKey, AdminAppBffFactory.AllowedSubject, DateTimeOffset.UtcNow);
        _factory.Upstream.Responder = static _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"detail\":\"INTERNAL-SIGNING-SECRET\"}", Encoding.UTF8, "application/json"),
        };

        var response = await _client.PostAsync("/api/admin/clients", "{\"name\":\"acme\"}", credential, AdminAppBffFactory.AllowedOrigin);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain("INTERNAL-SIGNING-SECRET", await response.Content.ReadAsStringAsync());
    }

    private void AssertBoundAssertion(RecordedUpstreamRequest recorded)
    {
        var assertion = AdminAppBffTestRequests.ValidateAssertion(recorded, _factory.AssertionKey);
        Assert.Equal(AdminAppBffFactory.AllowedSubject, assertion.Subject);
        Assert.Equal(AdminAppBffFactory.AppId, assertion.AppId);
    }
}
