using System.Net;
using System.Text;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Rag.Application;
using Rag.Infrastructure;

namespace Rag.UnitTests;

public sealed class LlamaCppReadinessHealthCheckTests
{
    [Fact]
    public async Task Reports_healthy_from_the_health_endpoint_without_generating_an_embedding()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "{\"status\":\"ok\"}");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://llama-cpp/") };
        var check = new LlamaCppReadinessHealthCheck(client);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("/health", handler.Path);
    }

    [Fact]
    public async Task Reports_unhealthy_while_llama_cpp_is_loading()
    {
        using var handler = new RecordingHandler(HttpStatusCode.ServiceUnavailable, "{\"error\":{\"message\":\"loading\"}}");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://llama-cpp/") };
        var check = new LlamaCppReadinessHealthCheck(client);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task Reports_unhealthy_for_a_malformed_health_response()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "not json");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://llama-cpp/") };
        var check = new LlamaCppReadinessHealthCheck(client);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    private sealed class RecordingHandler(HttpStatusCode statusCode, string payload) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public string? Path { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
        }
    }
}
