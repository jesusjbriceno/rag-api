using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
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
}

public sealed class AuthApiFactory : WebApplicationFactory<global::Program>
{
    private readonly RSA _rsa = RSA.Create(2048);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Embeddings:Default:Provider"] = "ollama",
            ["Embeddings:Default:Model"] = "qwen3-embedding:0.6b",
            ["Embeddings:Default:Version"] = "0.6b",
            ["Embeddings:Default:Dimensions"] = "1024",
            ["Embeddings:AllowedProfiles:0:Provider"] = "ollama",
            ["Embeddings:AllowedProfiles:0:Model"] = "qwen3-embedding:0.6b",
            ["Embeddings:AllowedProfiles:0:Version"] = "0.6b",
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
