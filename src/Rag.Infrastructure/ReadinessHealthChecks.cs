using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Rag.Application;

namespace Rag.Infrastructure;

public sealed class PostgreSqlReadinessHealthCheck(IDbContextFactory<IngestionDbContext> contextFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var dbContext = await contextFactory.CreateDbContextAsync(cancellationToken);
            return await dbContext.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("PostgreSQL is unavailable.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or Npgsql.NpgsqlException)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL is unavailable.", exception);
        }
    }
}

public sealed class OllamaModelReadinessHealthCheck(HttpClient httpClient, IOptions<EmbeddingOptions> embeddingOptions) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var model = embeddingOptions.Value.Validate().DefaultProfile.Model;
        try
        {
            var response = await httpClient.GetFromJsonAsync<OllamaTagsResponse>("api/tags", cancellationToken);
            return response?.Models?.Any(candidate => string.Equals(candidate.Name, model, StringComparison.Ordinal)) is true
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"Configured Ollama model '{model}' is unavailable.");
        }
        catch (Exception exception) when (exception is HttpRequestException or NotSupportedException or System.Text.Json.JsonException)
        {
            return HealthCheckResult.Unhealthy("Ollama is unavailable.", exception);
        }
    }

    private sealed class OllamaTagsResponse
    {
        public List<OllamaModel>? Models { get; init; }
    }

    private sealed class OllamaModel
    {
        public string? Name { get; init; }
    }
}
