using System.Net;
using Rag.Infrastructure;

namespace Rag.IntegrationTests;

/// <summary>
/// Complementary health coverage that exercises the BFF host through the shared
/// recording downstream: the probe path itself, plus anonymous liveness without
/// any downstream contact. Basic 200/503 outcomes are also covered by
/// AdminAppHostHealthTests in ProtectedApiTests.cs.
/// </summary>
public sealed class AdminAppHostBffHealthTests : IDisposable
{
    private readonly AdminAppHostTestFactory _factory = new();
    private readonly HttpClient _client;

    public AdminAppHostBffHealthTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Liveness_is_anonymous_and_reports_the_bff_process_itself()
    {
        var response = await _client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(_factory.Downstream.Requests);
    }

    [Fact]
    public async Task Readiness_probes_the_downstream_liveness_endpoint_and_reports_healthy()
    {
        _factory.Downstream.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK);

        var response = await _client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var captured = Assert.Single(_factory.Downstream.Requests);
        Assert.Equal(HttpMethod.Get, captured.Method);
        Assert.Equal("/api/v1/health/live", captured.PathAndQuery);
        Assert.False(captured.Headers.ContainsKey(AdminAuthenticationContract.AppIdHeader));
        Assert.False(captured.Headers.ContainsKey(AdminAuthenticationContract.SignatureHeader));
        Assert.False(captured.Headers.ContainsKey(AdminAuthenticationContract.AssertionHeader));
    }

    [Fact]
    public async Task Readiness_reports_unavailable_when_the_downstream_is_unreachable()
    {
        _factory.Downstream.ThrowOnSend = new HttpRequestException("connection refused");

        var response = await _client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
