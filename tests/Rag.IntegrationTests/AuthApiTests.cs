using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Rag.IntegrationTests;

public sealed class AuthApiTests : IClassFixture<AuthApiFactory>
{
    private readonly HttpClient _client;

    public AuthApiTests(AuthApiFactory factory) => _client = factory.CreateClient();

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
}

public sealed class AuthApiFactory : WebApplicationFactory<global::Program>
{
    private readonly RSA _rsa = RSA.Create(2048);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
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
        }));
        builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
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
