using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Rag.Infrastructure;

namespace Rag.IntegrationTests.Historical;

public sealed class HistoricalApiFactory(
    string connectionString,
    string contentRoot,
    IReadOnlyDictionary<string, string?>? overrides = null) : WebApplicationFactory<global::Program>
{
    private readonly RSA _rsa = RSA.Create(2048);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Rag"] = connectionString,
            ["ContentStore:RootPath"] = contentRoot,
            ["Embeddings:Default:Provider"] = "llama.cpp",
            ["Embeddings:Default:Model"] = "hf://Qwen/Qwen3-Embedding-0.6B-GGUF@370f27d7550e0def9b39c1f16d3fbaa13aa67728/Qwen3-Embedding-0.6B-Q8_0.gguf",
            ["Embeddings:Default:Version"] = "sha256:06507c7b42688469c4e7298b0a1e16deff06caf291cf0a5b278c308249c3e439",
            ["Embeddings:Default:Dimensions"] = "1024",
            ["Embeddings:AllowedProfiles:0:Provider"] = "llama.cpp",
            ["Embeddings:AllowedProfiles:0:Model"] = "hf://Qwen/Qwen3-Embedding-0.6B-GGUF@370f27d7550e0def9b39c1f16d3fbaa13aa67728/Qwen3-Embedding-0.6B-Q8_0.gguf",
            ["Embeddings:AllowedProfiles:0:Version"] = "sha256:06507c7b42688469c4e7298b0a1e16deff06caf291cf0a5b278c308249c3e439",
            ["Embeddings:AllowedProfiles:0:Dimensions"] = "1024",
            ["Jwt:Issuer"] = "integration-issuer",
            ["Jwt:Audience"] = "integration-audience",
            ["Jwt:CurrentSigningKey:KeyId"] = "integration-key",
            ["Jwt:CurrentSigningKey:PrivateKeyPem"] = _rsa.ExportRSAPrivateKeyPem(),
            ["Jwt:ValidationKeys:0:KeyId"] = "integration-key",
            ["Jwt:ValidationKeys:0:PublicKeyPem"] = _rsa.ExportRSAPublicKeyPem(),
            ["HistoricalIngestion:Enabled"] = "true",
            ["HistoricalIngestion:MaxNormalizedTextBytes"] = "1048576",
            ["HistoricalIngestion:PerClientPendingQuota"] = "3",
            ["HistoricalIngestion:TotalStorageWatermarkBytes"] = "10485760",
            ["HistoricalIngestion:AbandonedUploadExpiry"] = "00:05:00",
            ["HistoricalIngestion:UploadsRateLimitPermit"] = "10000",
            ["HistoricalIngestion:UploadsRateLimitWindow"] = "00:01:00",
        };

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                configuration[key] = value;
            }
        }

        builder.ConfigureAppConfiguration((_, appConfiguration) => appConfiguration.AddInMemoryCollection(configuration));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<NpgsqlDataSource>();
            services.RemoveAll<IDbContextFactory<IngestionDbContext>>();
            services.AddSingleton(_ =>
            {
                var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
                dataSourceBuilder.UseVector();
                return dataSourceBuilder.Build();
            });
            services.AddDbContextFactory<IngestionDbContext>((serviceProvider, options) =>
                options.UseNpgsql(serviceProvider.GetRequiredService<NpgsqlDataSource>(), providerOptions => providerOptions.UseVector()));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _rsa.Dispose();
        }
    }
}
