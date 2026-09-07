using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Rag.Infrastructure;

namespace Rag.IntegrationTests;

public sealed class AdminApiFactory(string connectionString, string contentRoot) : WebApplicationFactory<global::Program>
{
    public const string AppId = "admin-app";
    public const string KeyId = "machine-key-1";
    public const string MachineSecret = "machine-secret";
    public const string AssertionKid = "assertion-key-1";
    public const string Issuer = "admin-issuer";
    public const string Audience = "admin-audience";
    public const string Subject = "cf-subject-123";

    private readonly RSA _assertionKey = RSA.Create(2048);
    private readonly RSA _jwtKey = RSA.Create(2048);

    public string CreateAssertion(DateTimeOffset now)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = Subject,
            ["app_id"] = AppId,
            ["jti"] = Guid.NewGuid().ToString("N"),
        };
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.AddSeconds(60).UtcDateTime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_assertionKey) { KeyId = AssertionKid }, SecurityAlgorithms.RsaSha256),
        };
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityTokenHandler().CreateToken(descriptor));
    }

    public byte[] HmacSign(string canonical) =>
        HmacSign(MachineSecret, canonical);

    public static byte[] HmacSign(string secret, string canonical)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("AdminPlane:Enabled", "true");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
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
            ["Jwt:CurrentSigningKey:PrivateKeyPem"] = _jwtKey.ExportRSAPrivateKeyPem(),
            ["Jwt:ValidationKeys:0:KeyId"] = "integration-key",
            ["Jwt:ValidationKeys:0:PublicKeyPem"] = _jwtKey.ExportRSAPublicKeyPem(),
            ["AdminPlane:Enabled"] = "true",
            ["AdminAssertion:Issuer"] = Issuer,
            ["AdminAssertion:Audience"] = Audience,
            ["AdminAssertion:ValidationKeys:0:KeyId"] = AssertionKid,
            ["AdminAssertion:ValidationKeys:0:PublicKeyPem"] = _assertionKey.ExportRSAPublicKeyPem(),
            ["AdminAppAuth:Apps:0:AppId"] = AppId,
            ["AdminAppAuth:Apps:0:KeyId"] = KeyId,
            ["AdminAppAuth:Apps:0:CurrentSecret"] = MachineSecret,
        }));
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
            _assertionKey.Dispose();
            _jwtKey.Dispose();
        }
    }
}
