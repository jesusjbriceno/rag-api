using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Rag.AdminApp;
using Rag.AdminApp.Host;
using Rag.Infrastructure;

namespace Rag.IntegrationTests;

internal sealed record RecordedUpstreamRequest(
    string Method,
    string PathAndQuery,
    byte[] Body,
    string? AssertionJws,
    IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// In-process stand-in for the admin API: records every request that actually leaves
/// the BFF so tests can assert unauthorized attempts never arrive.
/// </summary>
internal sealed class FakeAdminApiHandler : HttpMessageHandler
{
    public List<RecordedUpstreamRequest> Requests { get; } = [];

    public Func<RecordedUpstreamRequest, HttpResponseMessage> Responder { get; set; } =
        static _ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("{\"id\":\"d1c0ffee-0000-0000-0000-000000000001\"}", Encoding.UTF8, "application/json"),
        };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var recorded = new RecordedUpstreamRequest(
            request.Method.ToString().ToUpperInvariant(),
            request.RequestUri!.PathAndQuery,
            body,
            request.Headers.TryGetValues("X-Admin-Assertion", out var assertion) ? assertion.Single() : null,
            request.Headers.ToDictionary(pair => pair.Key, pair => string.Join(",", pair.Value), StringComparer.OrdinalIgnoreCase));
        Requests.Add(recorded);
        return Responder(recorded);
    }
}

internal sealed class AdminAppBffFactory(string[]? allowedSubjects = null) : WebApplicationFactory<Rag.AdminApp.Host.Program>
{
    public const string AppId = "admin-app";
    public const string KeyId = "bff-key-1";
    public const string MachineSecret = "bff-machine-secret";
    public const string AssertionKid = "bff-assertion-key";
    public const string AssertionIssuer = "bff-assertion-issuer";
    public const string AssertionAudience = "bff-assertion-audience";
    public const string CfIssuer = "https://team.cloudflareaccess.com";
    public const string CfAudience = "cf-app-aud";
    public const string AllowedSubject = "cf-allowed-admin";
    public const string AllowedOrigin = "https://admin.example.com";

    public FakeAdminApiHandler Upstream { get; } = new();
    public RSA CfKey { get; } = RSA.Create(2048);
    public RSA AssertionKey { get; } = RSA.Create(2048);

    private readonly string[] _allowedSubjects = allowedSubjects ?? [AllowedSubject];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // UseSetting (not ConfigureAppConfiguration): the deferred test host applies host
        // settings before Program.Main reads configuration, and Main-time validation must
        // see the exact test configuration.
        var configuration = new Dictionary<string, string>
        {
            ["AdminBff:UpstreamBaseUrl"] = "http://upstream.invalid/",
            ["AdminBff:AllowedOrigin"] = AllowedOrigin,
            ["AdminBff:Signing:AppId"] = AppId,
            ["AdminBff:Signing:KeyId"] = KeyId,
            ["AdminBff:Signing:MachineSecret"] = MachineSecret,
            ["CloudflareAccess:Issuer"] = CfIssuer,
            ["CloudflareAccess:Audience"] = CfAudience,
            ["CloudflareAccess:Keys:0:KeyId"] = "cf-key-1",
            ["CloudflareAccess:Keys:0:PublicKeyPem"] = CfKey.ExportRSAPublicKeyPem(),
            ["AdminAssertion:Issuer"] = AssertionIssuer,
            ["AdminAssertion:Audience"] = AssertionAudience,
            ["AdminAssertion:AppId"] = AppId,
            ["AdminAssertion:KeyId"] = AssertionKid,
            ["AdminAssertion:PrivateKeyPem"] = AssertionKey.ExportRSAPrivateKeyPem(),
        };
        for (var index = 0; index < _allowedSubjects.Length; index++)
        {
            configuration[$"AdminBff:AllowedSubjects:{index}"] = _allowedSubjects[index];
        }

        foreach (var (key, value) in configuration)
        {
            builder.UseSetting(key, value);
        }
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<AdminApiProxyClient>();
            services.AddSingleton(_ => new AdminApiProxyClient(
                new HttpClient(Upstream) { BaseAddress = new Uri("http://upstream.invalid/") },
                new AdminRequestSigner(
                    new AdminAssertionIssuer(new AdminAssertionIssuerOptions(AssertionIssuer, AssertionAudience, AppId, AssertionKid, AssertionKey.ExportRSAPrivateKeyPem())),
                    new AdminRequestSignerOptions(AppId, KeyId, MachineSecret)),
                NullLogger<AdminApiProxyClient>.Instance));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            CfKey.Dispose();
            AssertionKey.Dispose();
        }
    }
}

/// <summary>
/// Shared request building and upstream-contract verification for the AdminApp BFF
/// integration tests. Every proxied browser mutation carries a valid UUID
/// <c>Idempotency-Key</c> by default, mirroring what the Task 2 UI will always send.
/// </summary>
internal static class AdminAppBffTestRequests
{
    public const string DefaultIdempotencyKey = "6f9619ff-8b86-d011-b42d-00c04fc964ff";

    public static Task<HttpResponseMessage> PostAsync(
        this HttpClient client,
        string path,
        string json,
        string? credential,
        string? origin,
        string? headerName = null,
        string? headerValue = null,
        string? idempotencyKey = DefaultIdempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (credential is not null)
        {
            request.Headers.Add("Cf-Access-Jwt-Assertion", credential);
        }

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        if (headerName is not null)
        {
            request.Headers.Add(headerName, headerValue);
        }

        return client.SendAsync(request);
    }

    public static string CreateCloudflareToken(RSA key, string subject, DateTimeOffset now)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = AdminAppBffFactory.CfIssuer,
            Audience = AdminAppBffFactory.CfAudience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.AddMinutes(5).UtcDateTime,
            Claims = new Dictionary<string, object> { ["sub"] = subject },
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(key) { KeyId = "cf-key-1" }, SecurityAlgorithms.RsaSha256),
        };
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityTokenHandler().CreateToken(descriptor));
    }

    public static AdminMachineProof ProofFrom(RecordedUpstreamRequest recorded) => new(
        recorded.Method,
        recorded.PathAndQuery,
        Convert.ToHexStringLower(SHA256.HashData(recorded.Body)),
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(recorded.AssertionJws!))),
        recorded.Headers["X-Admin-App-Id"],
        recorded.Headers["X-Admin-Key-Id"],
        long.Parse(recorded.Headers["X-Admin-Timestamp"], System.Globalization.CultureInfo.InvariantCulture),
        recorded.Headers["Idempotency-Key"]);

    public static AdminMachineProofVerifier CreateVerifier() => new(new AdminAppAuthOptions
    {
        Apps = [new AdminAppOptions { AppId = AdminAppBffFactory.AppId, KeyId = AdminAppBffFactory.KeyId, CurrentSecret = AdminAppBffFactory.MachineSecret }],
        ClockSkewSeconds = 60,
    });

    /// <summary>
    /// Validates the upstream assertion exactly as the admin API would and returns it so
    /// tests can assert the bound subject and app id.
    /// </summary>
    public static AdminAssertion ValidateAssertion(RecordedUpstreamRequest recorded, RSA assertionPublicKey)
    {
        var apiOptions = new AdminAssertionOptions
        {
            Issuer = AdminAppBffFactory.AssertionIssuer,
            Audience = AdminAppBffFactory.AssertionAudience,
            ValidationKeys = [new AdminAssertionValidationKeyOptions { KeyId = AdminAppBffFactory.AssertionKid, PublicKeyPem = assertionPublicKey.ExportRSAPublicKeyPem() }],
        };
        using var keyRing = new AdminAssertionKeyRing(apiOptions);
        var assertion = new AdminAssertionValidator(apiOptions, keyRing).Validate(recorded.AssertionJws!, DateTimeOffset.UtcNow);
        Assert.NotNull(assertion);
        return assertion;
    }

    public static async Task<string> ProblemCodeAsync(this HttpResponseMessage response)
    {
        using var document = await System.Text.Json.JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return document.RootElement.GetProperty("code").GetString() ?? string.Empty;
    }
}
