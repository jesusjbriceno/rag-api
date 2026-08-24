using System.Net;
using System.Text;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Rag.Application;
using Rag.Infrastructure;

namespace Rag.UnitTests;

public sealed class OllamaModelReadinessHealthCheckTests
{
    [Fact]
    public async Task Reports_healthy_when_the_configured_model_is_listed_without_generating_an_embedding()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "{\"models\":[{\"name\":\"qwen3-embedding:0.6b\"}]}");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://ollama/") };
        var check = new OllamaModelReadinessHealthCheck(client, Options.Create(CreateOptions()));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("/api/tags", handler.Path);
    }

    [Fact]
    public async Task Reports_unhealthy_when_the_configured_model_is_not_listed()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "{\"models\":[]}");
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://ollama/") };
        var check = new OllamaModelReadinessHealthCheck(client, Options.Create(CreateOptions()));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    private static EmbeddingOptions CreateOptions() => new()
    {
        Default = new EmbeddingProfileOptions
        {
            Provider = "ollama",
            Model = "qwen3-embedding:0.6b",
            Version = "0.6b",
            Dimensions = 1024,
        },
        AllowedProfiles =
        [
            new EmbeddingProfileOptions
            {
                Provider = "ollama",
                Model = "qwen3-embedding:0.6b",
                Version = "0.6b",
                Dimensions = 1024,
            },
        ],
    };

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
