using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
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
using Rag.Application;
using Rag.Infrastructure;

namespace Rag.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
public sealed class AuthApiTests : IClassFixture<AuthApiFactory>
{
    private readonly HttpClient _client;
    private readonly PostgreSqlFixture _fixture;

    public AuthApiTests(AuthApiFactory factory, PostgreSqlFixture fixture)
    {
        _client = factory.CreateClient();
        _fixture = fixture;
    }

    [Fact]
    public async Task Health_is_explicitly_anonymous_and_invalid_credential_exchange_is_non_enumerating()
    {
        var health = await _client.GetAsync("/api/v1/health");
        var malformed = await _client.PostAsJsonAsync("/api/v1/auth/token", new { keyId = "unknown", secret = "secret" });
        var missingSecret = await _client.PostAsJsonAsync("/api/v1/auth/token", new { keyId = "unknown" });
        var third = await _client.PostAsJsonAsync("/api/v1/auth/token", new { keyId = "unknown", secret = "secret" });
        var fourth = await _client.PostAsJsonAsync("/api/v1/auth/token", new { keyId = "unknown", secret = "secret" });
        var fifth = await _client.PostAsJsonAsync("/api/v1/auth/token", new { keyId = "unknown", secret = "secret" });
        var rateLimited = await _client.PostAsJsonAsync("/api/v1/auth/token", new { keyId = "unknown", secret = "secret" });

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, missingSecret.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, third.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, fourth.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, fifth.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rateLimited.StatusCode);
        Assert.False(rateLimited.Headers.Contains("Retry-After"));
        Assert.Equal(string.Empty, await rateLimited.Content.ReadAsStringAsync());
        Assert.Equal(await malformed.Content.ReadAsStringAsync(), await missingSecret.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Health_reports_assembly_informational_version()
    {
        var expected = typeof(global::Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Assert.False(string.IsNullOrWhiteSpace(expected));

        var health = await _client.GetAsync("/api/v1/health");
        health.EnsureSuccessStatusCode();

        using var payload = JsonDocument.Parse(await health.Content.ReadAsStringAsync());
        var version = payload.RootElement.GetProperty("version").GetString();

        Assert.Equal(expected, version);
    }

    [Fact]
    public async Task Token_exchange_success_body_pins_the_token_contract()
    {
        using var factory = new AuthApiFactory { ConnectionString = _fixture.ConnectionString };
        using var client = factory.CreateClient();
        var options = new DbContextOptionsBuilder<IngestionDbContext>()
            .UseNpgsql(_fixture.ConnectionString, options => options.UseVector())
            .Options;
        await using var context = new IngestionDbContext(options);
        await context.Database.MigrateAsync();
        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE client_credentials, service_clients CASCADE;");

        using var scope = factory.Services.CreateScope();
        var issued = await scope.ServiceProvider.GetRequiredService<CredentialOperator>().IssueAsync($"auth-token-{Guid.NewGuid():N}", null);

        var response = await client.PostAsJsonAsync("/api/v1/auth/token", new { keyId = issued.KeyId, secret = issued.Secret });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = document.RootElement;
        Assert.Equal(Keys("access_token", "token_type", "expires_in"), PropertyNames(body));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("access_token").GetString()));
        Assert.Equal("Bearer", body.GetProperty("token_type").GetString());
        Assert.Equal(900, body.GetProperty("expires_in").GetInt32());
    }

    private static string[] PropertyNames(JsonElement element) =>
        [.. element.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal)];

    private static string[] Keys(params string[] names) =>
        [.. names.OrderBy(name => name, StringComparer.Ordinal)];
}

public sealed class AuthApiFactory : WebApplicationFactory<global::Program>
{
    private readonly RSA _rsa = RSA.Create(2048);

    public string? ConnectionString { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var values = new Dictionary<string, string?>
            {
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
            };
            if (ConnectionString is not null)
            {
                values["ConnectionStrings:Rag"] = ConnectionString;
            }

            configuration.AddInMemoryCollection(values);
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();
            if (ConnectionString is not null)
            {
                services.RemoveAll<NpgsqlDataSource>();
                services.RemoveAll<IDbContextFactory<IngestionDbContext>>();
                services.AddSingleton(_ =>
                {
                    var dataSourceBuilder = new NpgsqlDataSourceBuilder(ConnectionString);
                    dataSourceBuilder.UseVector();
                    return dataSourceBuilder.Build();
                });
                services.AddDbContextFactory<IngestionDbContext>((serviceProvider, options) =>
                    options.UseNpgsql(serviceProvider.GetRequiredService<NpgsqlDataSource>(), providerOptions => providerOptions.UseVector()));
            }
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
