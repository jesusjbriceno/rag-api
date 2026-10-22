using System.Net;
using System.Net.Http.Headers;
using Rag.AdminApp.Host;

namespace Rag.IntegrationTests;

/// <summary>
/// Static app shell/asset coverage for the Cloudflare-guarded AdminApp host: the SPA
/// shell and its assets live behind the exact same Cloudflare JWT + subject-allowlist
/// guard as the BFF operations, so only the health probe remains unauthenticated.
/// An unauthenticated or disallowed request must never see the shell, its markup,
/// or its bundled assets.
/// </summary>
public sealed class AdminAppStaticFilesTests : IDisposable
{
    private readonly AdminAppBffFactory _factory = new();
    private readonly HttpClient _client;

    public AdminAppStaticFilesTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Spa_shell_is_not_served_without_a_cloudflare_credential()
    {
        var root = await _client.GetAsync("/");
        var fallback = await _client.GetAsync("/some/client/route");

        foreach (var response in new[] { root, fallback })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("unauthorized", await response.ProblemCodeAsync());
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("<div id=\"root\">", body);
            Assert.DoesNotContain("<script", body);
        }
    }

    [Fact]
    public async Task Spa_shell_is_not_served_to_a_disallowed_subject()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Cf-Access-Jwt-Assertion",
            AdminAppBffTestRequests.CreateCloudflareToken(_factory.CfKey, "cf-stranger-subject", DateTimeOffset.UtcNow));

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("not_allowlisted", await response.ProblemCodeAsync());
        Assert.DoesNotContain("<div id=\"root\">", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Spa_assets_are_not_served_without_a_cloudflare_credential()
    {
        foreach (var path in new[] { "/index.html", "/assets/index-test.js", "/assets/index-test.css" })
        {
            var response = await _client.GetAsync(path);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("unauthorized", await response.ProblemCodeAsync());
        }
    }

    [Fact]
    public async Task Browser_supplied_identity_headers_are_rejected_for_the_shell()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Forwarded-User", "attacker");

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Allowlisted_administrator_can_retrieve_the_built_shell()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Cf-Access-Jwt-Assertion",
            AdminAppBffTestRequests.CreateCloudflareToken(_factory.CfKey, AdminAppBffFactory.AllowedSubject, DateTimeOffset.UtcNow));

        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new MediaTypeHeaderValue("text/html").MediaType, response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("<div id=\"root\">", body);
    }

    [Fact]
    public async Task Health_subpaths_are_not_an_unauthenticated_probe_or_shell_bypass()
    {
        // The health exemption is exact-path only: /health/ and /health/<anything> must
        // not fall through the guard to the static files or the SPA fallback.
        foreach (var path in new[] { "/health/", "/health/anything", "/health/../index.html" })
        {
            var response = await _client.GetAsync(path);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("unauthorized", await response.ProblemCodeAsync());
            Assert.DoesNotContain("<div id=\"root\">", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Health_remains_the_only_unauthenticated_probe()
    {
        var health = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var shell = await _client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Unauthorized, shell.StatusCode);
    }
}
