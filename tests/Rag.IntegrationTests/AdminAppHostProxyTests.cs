using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.IdentityModel.Tokens;
using Rag.Infrastructure;

namespace Rag.IntegrationTests;

/// <summary>
/// One downstream request observed by the recording handler: the effective method,
/// path and query, the buffered body bytes, and the merged request and content headers.
/// </summary>
internal sealed record CapturedDownstreamRequest(
    HttpMethod Method,
    string PathAndQuery,
    byte[] Body,
    IReadOnlyDictionary<string, string[]> Headers)
{
    public static async Task<CapturedDownstreamRequest> CaptureAsync(HttpRequestMessage request)
    {
        var body = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync();

        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in request.Headers)
        {
            headers[name] = values.ToArray();
        }

        if (request.Content is not null)
        {
            foreach (var (name, values) in request.Content.Headers)
            {
                headers[name] = values.ToArray();
            }
        }

        return new CapturedDownstreamRequest(
            request.Method,
            request.RequestUri!.PathAndQuery,
            body,
            headers);
    }
}

/// <summary>
/// Records every downstream request and returns a scripted response, so tests can
/// assert exactly what the BFF forwarded without a real network dependency.
/// </summary>
internal sealed class RecordingDownstreamHandler : DelegatingHandler
{
    private readonly List<CapturedDownstreamRequest> _requests = [];

    public Func<HttpRequestMessage, HttpResponseMessage>? Responder { get; set; }

    public Exception? ThrowOnSend { get; set; }

    public IReadOnlyList<CapturedDownstreamRequest> Requests => _requests;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (ThrowOnSend is not null)
        {
            throw ThrowOnSend;
        }

        _requests.Add(await CapturedDownstreamRequest.CaptureAsync(request));
        return Responder?.Invoke(request)
            ?? new HttpResponseMessage(HttpStatusCode.OK);
    }
}

/// <summary>
/// Boots the AdminApp BFF host with ephemeral RSA/HMAC material and a recording
/// downstream handler injected into every registered HttpClient pipeline.
/// </summary>
internal sealed class AdminAppHostTestFactory : WebApplicationFactory<Rag.AdminApp.Host.Program>
{
    public const string CloudflareIssuer = "https://cf-team.example.com";
    public const string CloudflareAudience = "cf-audience";
    public const string CloudflareKeyId = "cf-key-1";
    public const string AssertionAppId = "admin-app";
    public const string AssertionKeyId = "assertion-key-1";
    public const string MachineKeyId = "machine-key-1";
    public const string MachineSecret = "machine-secret";
    public const string AllowedSubject = "cf-subject-admin";
    public const string StrangerSubject = "cf-subject-stranger";
    public const string DownstreamOrigin = "http://admin-downstream.test/";

    private static readonly string HostContentRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Rag.AdminApp.Host"));

    private readonly RSA _cloudflareKey = RSA.Create(2048);
    private readonly RSA _assertionKey = RSA.Create(2048);

    public RecordingDownstreamHandler Downstream { get; } = new();

    public string CreateCloudflareAssertion(string subject, DateTimeOffset now) =>
        CreateAssertion(_cloudflareKey, subject, now.AddSeconds(60), now, now, CloudflareKeyId);

    public string CreateExpiredCloudflareAssertion(string subject, DateTimeOffset now) =>
        CreateAssertion(_cloudflareKey, subject, now.AddSeconds(-120), now.AddSeconds(-240), now.AddSeconds(-240), CloudflareKeyId);

    public string ExportCloudflarePublicKey() => _cloudflareKey.ExportRSAPublicKeyPem();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(HostContentRoot);
        builder.UseSetting("AllowedAdminSubjects:0", AllowedSubject);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["CloudflareAccess:Issuer"] = CloudflareIssuer,
                ["CloudflareAccess:Audience"] = CloudflareAudience,
                ["CloudflareAccess:Keys:0:KeyId"] = CloudflareKeyId,
                ["CloudflareAccess:Keys:0:PublicKeyPem"] = _cloudflareKey.ExportRSAPublicKeyPem(),
                ["AdminAssertion:Issuer"] = "admin-assertion-issuer",
                ["AdminAssertion:Audience"] = "admin-assertion-audience",
                ["AdminAssertion:AppId"] = AssertionAppId,
                ["AdminAssertion:KeyId"] = AssertionKeyId,
                ["AdminAssertion:PrivateKeyPem"] = _assertionKey.ExportRSAPrivateKeyPem(),
                ["AdminAppAuth:Apps:0:AppId"] = AssertionAppId,
                ["AdminAppAuth:Apps:0:KeyId"] = MachineKeyId,
                ["AdminAppAuth:Apps:0:CurrentSecret"] = MachineSecret,
                ["AdminApi:BaseUrl"] = DownstreamOrigin,
            }));
        builder.ConfigureTestServices(services =>
        {
            services.ConfigureAll<HttpClientFactoryOptions>(options =>
                options.HttpMessageHandlerBuilderActions.Add(handlerBuilder =>
                    handlerBuilder.AdditionalHandlers.Add(Downstream)));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _cloudflareKey.Dispose();
            _assertionKey.Dispose();
        }
    }

    private static string CreateAssertion(
        RSA signingKey,
        string subject,
        DateTimeOffset expires,
        DateTimeOffset issuedAt,
        DateTimeOffset notBefore,
        string keyId)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = CloudflareIssuer,
            Audience = CloudflareAudience,
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = notBefore.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = new Dictionary<string, object> { ["sub"] = subject },
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(signingKey) { KeyId = keyId },
                SecurityAlgorithms.RsaSha256),
        };
        return new JwtSecurityTokenHandler().WriteToken(
            new JwtSecurityTokenHandler().CreateToken(descriptor));
    }
}

public sealed class AdminAppHostProxyTests : IDisposable
{
    private static readonly (HttpMethod Method, string Path, string? Body)[] Routes =
    [
        (HttpMethod.Post, "/api/v1/admin/clients", "{\"name\":\"r\"}"),
        (HttpMethod.Get, "/api/v1/admin/clients", null),
        (HttpMethod.Get, "/api/v1/admin/clients/00000000-0000-0000-0000-000000000001", null),
        (HttpMethod.Post, "/api/v1/admin/clients/00000000-0000-0000-0000-000000000001/credentials", "{}"),
        (HttpMethod.Get, "/api/v1/admin/clients/00000000-0000-0000-0000-000000000001/credentials", null),
        (HttpMethod.Get, "/api/v1/admin/credentials/00000000-0000-0000-0000-000000000001", null),
        (HttpMethod.Post, "/api/v1/admin/credentials/00000000-0000-0000-0000-000000000001/rotate", null),
        (HttpMethod.Post, "/api/v1/admin/credentials/00000000-0000-0000-0000-000000000001/revoke", null),
        (HttpMethod.Get, "/api/v1/admin/audit", null),
    ];

    private readonly AdminAppHostTestFactory _factory = new();
    private readonly HttpClient _client;

    public AdminAppHostProxyTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Every_admin_route_rejects_missing_credentials_before_any_downstream_call()
    {
        foreach (var route in Routes)
        {
            _factory.Downstream.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK);

            var response = await _client.SendAsync(new HttpRequestMessage(route.Method, route.Path));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        Assert.Empty(_factory.Downstream.Requests);
    }

    [Fact]
    public async Task Forged_expired_and_out_of_allowlist_credentials_never_reach_the_downstream()
    {
        var route = Routes[0];
        var now = DateTimeOffset.UtcNow;

        var forged = new HttpRequestMessage(route.Method, route.Path);
        forged.Headers.Add("Cf-Access-Jwt-Assertion", "forged-identity");
        var expired = new HttpRequestMessage(route.Method, route.Path);
        expired.Headers.Add(
            "Cf-Access-Jwt-Assertion",
            _factory.CreateExpiredCloudflareAssertion(AdminAppHostTestFactory.AllowedSubject, now));
        var stranger = new HttpRequestMessage(route.Method, route.Path);
        stranger.Headers.Add(
            "Cf-Access-Jwt-Assertion",
            _factory.CreateCloudflareAssertion(AdminAppHostTestFactory.StrangerSubject, now));

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(forged)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(expired)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(stranger)).StatusCode);

        Assert.Empty(_factory.Downstream.Requests);
    }

    [Fact]
    public async Task Blank_and_multiple_cloudflare_assertion_headers_are_rejected_without_downstream_call()
    {
        _factory.Downstream.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK);

        var blank = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/clients");
        blank.Headers.Add("Cf-Access-Jwt-Assertion", "   ");
        var multiple = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/clients");
        multiple.Headers.Add("Cf-Access-Jwt-Assertion", new[] { "first", "second" });

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(blank)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(multiple)).StatusCode);

        Assert.Empty(_factory.Downstream.Requests);
    }

    [Fact]
    public async Task Unknown_routes_wrong_methods_and_invalid_guids_never_reach_the_downstream()
    {
        _factory.Downstream.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK);

        var unknownPath = await _client.GetAsync("/api/v1/admin/nonexistent");
        var wrongMethod = await _client.DeleteAsync("/api/v1/admin/clients");
        var invalidGuid = await _client.GetAsync("/api/v1/admin/clients/not-a-guid");
        var invalidGuidCredential = await _client.GetAsync("/api/v1/admin/credentials/not-a-guid");

        Assert.Equal(HttpStatusCode.NotFound, unknownPath.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, invalidGuid.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, invalidGuidCredential.StatusCode);

        Assert.Empty(_factory.Downstream.Requests);
    }

    [Fact]
    public async Task Allowlisted_subject_request_is_proxied_with_a_downstream_verifiable_signature()
    {
        var downstreamResponse = new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("{\"id\":\"00000000-0000-0000-0000-000000000001\"}", Encoding.UTF8, "application/json"),
        };
        downstreamResponse.Headers.Location = new Uri("/api/v1/admin/clients/00000000-0000-0000-0000-000000000001", UriKind.Relative);
        _factory.Downstream.Responder = _ => downstreamResponse;

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/clients")
        {
            Content = new StringContent("{\"name\":\"proxied\"}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", "00000000-0000-0000-0000-00000000abcd");

        var response = await Authorize(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("/api/v1/admin/clients/00000000-0000-0000-0000-000000000001", response.Headers.Location!.ToString());
        Assert.Equal("{\"id\":\"00000000-0000-0000-0000-000000000001\"}", await response.Content.ReadAsStringAsync());

        var captured = Assert.Single(_factory.Downstream.Requests);
        Assert.Equal(HttpMethod.Post, captured.Method);
        Assert.Equal("/api/v1/admin/clients", captured.PathAndQuery);
        Assert.Equal(
            AdminAppHostTestFactory.AssertionAppId,
            Assert.Single(captured.Headers[AdminAuthenticationContract.AppIdHeader]));
        Assert.Equal(
            AdminAppHostTestFactory.MachineKeyId,
            Assert.Single(captured.Headers[AdminAuthenticationContract.KeyIdHeader]));
        Assert.Equal(
            "00000000-0000-0000-0000-00000000abcd",
            Assert.Single(captured.Headers[AdminAuthenticationContract.IdempotencyKeyHeader]));
        Assert.True(
            long.TryParse(
                Assert.Single(captured.Headers[AdminAuthenticationContract.TimestampHeader]),
                CultureInfo.InvariantCulture,
                out _),
            "downstream timestamp must be a unix-seconds integer");
        Assert.True(verifierSignatureIsValid(captured), "forwarded signature must verify against the shared machine-proof contract");
    }

    [Fact]
    public async Task If_match_is_forwarded_only_for_applicable_operations()
    {
        _factory.Downstream.Responder = _ => new HttpResponseMessage(HttpStatusCode.NoContent);

        var rotate = new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/credentials/00000000-0000-0000-0000-000000000001/rotate")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        rotate.Headers.Add("If-Match", "\"etag-1\"");
        Assert.Equal(HttpStatusCode.NoContent, (await Authorize(rotate)).StatusCode);
        Assert.Equal(
            new[] { "\"etag-1\"" },
            Assert.Single(_factory.Downstream.Requests).Headers.GetValueOrDefault("If-Match"));

        var list = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/clients");
        list.Headers.Add("If-Match", "\"etag-2\"");
        Assert.Equal(HttpStatusCode.NoContent, (await Authorize(list)).StatusCode);
        Assert.DoesNotContain(
            _factory.Downstream.Requests,
            captured => captured.Method == HttpMethod.Get && captured.Headers.ContainsKey("If-Match"));
    }

    [Fact]
    public async Task Missing_idempotency_key_generates_one_and_multiple_keys_are_rejected_without_downstream_call()
    {
        _factory.Downstream.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK);

        var generated = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/clients");
        Assert.Equal(HttpStatusCode.OK, (await Authorize(generated)).StatusCode);
        var forwardedKey = Assert.Single(Assert.Single(_factory.Downstream.Requests).Headers[AdminAuthenticationContract.IdempotencyKeyHeader]);
        Assert.False(string.IsNullOrWhiteSpace(forwardedKey));

        var duplicated = new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/clients");
        duplicated.Headers.Add("Idempotency-Key", "first");
        duplicated.Headers.Add("Idempotency-Key", "second");
        var rejected = await Authorize(duplicated);

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Single(_factory.Downstream.Requests);
    }

    [Fact]
    public async Task Downstream_transport_failure_maps_to_a_bad_gateway_problem()
    {
        _factory.Downstream.ThrowOnSend = new HttpRequestException("connection refused");

        var response = await Authorize(new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/clients"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Only_allowlisted_downstream_response_headers_are_copied_back()
    {
        var downstreamResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        downstreamResponse.Headers.Add("X-Powered-By", "hostile");
        downstreamResponse.Headers.Add("Set-Cookie", "session=hostile");
        downstreamResponse.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
        _factory.Downstream.Responder = _ => downstreamResponse;

        var response = await Authorize(new HttpRequestMessage(HttpMethod.Get, "/api/v1/admin/clients"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(7, response.Headers.RetryAfter!.Delta!.Value.TotalSeconds);
        Assert.False(response.Headers.Contains("X-Powered-By"));
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Health_endpoints_are_reachable_without_admin_credentials()
    {
        _factory.Downstream.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK);

        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health/ready")).StatusCode);
    }

    private async Task<HttpResponseMessage> Authorize(HttpRequestMessage request)
    {
        request.Headers.Add(
            "Cf-Access-Jwt-Assertion",
            _factory.CreateCloudflareAssertion(AdminAppHostTestFactory.AllowedSubject, DateTimeOffset.UtcNow));
        return await _client.SendAsync(request);
    }

    private bool verifierSignatureIsValid(CapturedDownstreamRequest captured)
    {
        var proof = new AdminMachineProof(
            captured.Method.Method,
            captured.PathAndQuery,
            Convert.ToHexStringLower(SHA256.HashData(captured.Body)),
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                Assert.Single(captured.Headers[AdminAuthenticationContract.AssertionHeader])))),
            Assert.Single(captured.Headers[AdminAuthenticationContract.AppIdHeader]),
            Assert.Single(captured.Headers[AdminAuthenticationContract.KeyIdHeader]),
            long.Parse(Assert.Single(captured.Headers[AdminAuthenticationContract.TimestampHeader]), CultureInfo.InvariantCulture),
            Assert.Single(captured.Headers[AdminAuthenticationContract.IdempotencyKeyHeader]));
        var verifier = new AdminMachineProofVerifier(new AdminAppAuthOptions
        {
            Apps =
            [
                new AdminAppOptions
                {
                    AppId = AdminAppHostTestFactory.AssertionAppId,
                    KeyId = AdminAppHostTestFactory.MachineKeyId,
                    CurrentSecret = AdminAppHostTestFactory.MachineSecret,
                },
            ],
        });
        return verifier.Verify(
            proof,
            Assert.Single(captured.Headers[AdminAuthenticationContract.SignatureHeader]),
            DateTimeOffset.UtcNow);
    }
}
