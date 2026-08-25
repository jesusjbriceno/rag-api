using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

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

public sealed class LlamaCppReadinessHealthCheck(HttpClient httpClient) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await httpClient.GetAsync("health", cancellationToken);
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                return HealthCheckResult.Unhealthy("llama.cpp is not ready.");
            }

            var payload = await response.Content.ReadFromJsonAsync<LlamaCppHealthResponse>(cancellationToken: cancellationToken);
            return string.Equals(payload?.Status, "ok", StringComparison.OrdinalIgnoreCase)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("llama.cpp returned a malformed health response.");
        }
        catch (Exception exception) when (exception is HttpRequestException or NotSupportedException or System.Text.Json.JsonException)
        {
            return HealthCheckResult.Unhealthy("llama.cpp is unavailable.", exception);
        }
    }

    private sealed class LlamaCppHealthResponse
    {
        public string? Status { get; init; }
    }
}
