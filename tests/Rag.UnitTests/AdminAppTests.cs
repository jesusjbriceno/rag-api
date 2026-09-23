using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;
using Rag.AdminApp;
using Rag.AdminApp.Host;
using Rag.Infrastructure;

namespace Rag.UnitTests;

public sealed class AdminAppTests
{
    private const string TeamIssuer = "https://team.cloudflareaccess.com";
    private const string CloudflareAud = "cf-app-aud";
    private const string Subject = "cf-subject-123";
    private const string AppId = "admin-app";

    private static (RSA Rsa, string PrivatePem, string PublicPem) NewKey()
    {
        var rsa = RSA.Create(2048);
        return (rsa, rsa.ExportRSAPrivateKeyPem(), rsa.ExportRSAPublicKeyPem());
    }

    [Fact]
    public void Valid_cloudflare_credential_yields_the_verified_subject()
    {
        using var cf = NewKey().Rsa;
        var validator = new CloudflareAccessValidator(new CloudflareAccessOptions(
            TeamIssuer, CloudflareAud, [new CloudflareAccessKey("cf-key", cf.ExportRSAPublicKeyPem())]));
        var credential = CreateCloudflareToken(cf, "cf-key", Subject, TeamIssuer, CloudflareAud, DateTimeOffset.UtcNow);

        Assert.Equal(Subject, validator.Validate(credential));
    }

    [Fact]
    public void Absent_expired_forged_or_wrong_audience_credential_is_rejected()
    {
        using var cf = NewKey().Rsa;
        using var other = NewKey().Rsa;
        var validator = new CloudflareAccessValidator(new CloudflareAccessOptions(
            TeamIssuer, CloudflareAud, [new CloudflareAccessKey("cf-key", cf.ExportRSAPublicKeyPem())]));
        var now = DateTimeOffset.UtcNow;

        Assert.Null(validator.Validate(string.Empty));
        Assert.Null(validator.Validate("not-a-token"));
        Assert.Null(validator.Validate(CreateCloudflareToken(cf, "cf-key", Subject, TeamIssuer, CloudflareAud, now.AddHours(-1))));
        Assert.Null(validator.Validate(CreateCloudflareToken(other, "cf-key", Subject, TeamIssuer, CloudflareAud, now)));
        Assert.Null(validator.Validate(CreateCloudflareToken(cf, "cf-key", Subject, TeamIssuer, "other-aud", now)));
        Assert.Null(validator.Validate(CreateCloudflareToken(cf, "cf-key", string.Empty, TeamIssuer, CloudflareAud, now)));
    }

    [Fact]
    public void Issued_assertion_carries_required_claims_and_is_short_lived()
    {
        using var key = NewKey().Rsa;
        var issuer = new AdminAssertionIssuer(new AdminAssertionIssuerOptions(
            "admin-issuer", "admin-audience", AppId, "assertion-key", key.ExportRSAPrivateKeyPem()));
        var now = DateTimeOffset.UtcNow;

        var handler = new JwtSecurityTokenHandler();
        var token = handler.ReadJwtToken(issuer.Issue(Subject, now));

        Assert.Equal(Subject, token.Payload["sub"]);
        Assert.Equal(AppId, token.Payload["app_id"]);
        Assert.NotNull(token.Payload["jti"]);
        Assert.Equal("admin-issuer", token.Issuer);
        Assert.Equal("admin-audience", token.Audiences.Single());
        Assert.InRange(token.ValidTo - token.ValidFrom, TimeSpan.Zero, TimeSpan.FromSeconds(61));
    }

    [Fact]
    public void Admin_app_assertion_is_accepted_by_the_api_validator()
    {
        using var key = NewKey().Rsa;
        var issuer = new AdminAssertionIssuer(new AdminAssertionIssuerOptions(
            "admin-issuer", "admin-audience", AppId, "assertion-key", key.ExportRSAPrivateKeyPem()));
        var apiOptions = new AdminAssertionOptions
        {
            Issuer = "admin-issuer",
            Audience = "admin-audience",
            ValidationKeys = [new AdminAssertionValidationKeyOptions { KeyId = "assertion-key", PublicKeyPem = key.ExportRSAPublicKeyPem() }],
        };
        using var keyRing = new AdminAssertionKeyRing(apiOptions);
        var validator = new AdminAssertionValidator(apiOptions, keyRing);
        var now = DateTimeOffset.UtcNow;

        var result = validator.Validate(issuer.Issue(Subject, now), now);

        Assert.NotNull(result);
        Assert.Equal(Subject, result.Subject);
        Assert.Equal(AppId, result.AppId);
        Assert.Equal("admin-issuer", result.Issuer);
    }

    private static string CreateCloudflareToken(RSA key, string kid, string subject, string issuer, string audience, DateTimeOffset now)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.AddMinutes(5).UtcDateTime,
            Claims = new Dictionary<string, object> { ["sub"] = subject },
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(key) { KeyId = kid }, SecurityAlgorithms.RsaSha256),
        };
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityTokenHandler().CreateToken(descriptor));
    }
}

public sealed class AdminProxyOperationCatalogTests
{
    [Fact]
    public void Catalog_contains_exactly_the_nine_supported_operations_without_duplicates()
    {
        var operations = AdminProxyOperationCatalog.Operations;

        Assert.Equal(9, operations.Count);
        Assert.Equal(9, operations.Select(op => (op.Method, op.Template)).Distinct().Count());

        Assert.Contains(operations, op => op.Method == "POST" && op.Template == "/clients");
        Assert.Contains(operations, op => op.Method == "GET" && op.Template == "/clients");
        Assert.Contains(operations, op => op.Method == "GET" && op.Template == "/clients/{clientId:guid}");
        Assert.Contains(operations, op => op.Method == "POST" && op.Template == "/clients/{clientId:guid}/credentials");
        Assert.Contains(operations, op => op.Method == "GET" && op.Template == "/clients/{clientId:guid}/credentials");
        Assert.Contains(operations, op => op.Method == "GET" && op.Template == "/credentials/{credentialId:guid}");
        Assert.Contains(operations, op => op.Method == "POST" && op.Template == "/credentials/{credentialId:guid}/rotate");
        Assert.Contains(operations, op => op.Method == "POST" && op.Template == "/credentials/{credentialId:guid}/revoke");
        Assert.Contains(operations, op => op.Method == "GET" && op.Template == "/audit");
    }

    [Fact]
    public void Only_rotate_and_revoke_require_an_if_match_header()
    {
        foreach (var operation in AdminProxyOperationCatalog.Operations)
        {
var expected = operation.Template.EndsWith("/rotate", StringComparison.Ordinal) ||
operation.Template.EndsWith("/revoke", StringComparison.Ordinal);
Assert.Equal(expected, operation.IfMatchApplicable);
        }
    }
}

public sealed class AdminProxyRequestSignerTests
{
    private const string AppId = "admin-app";
    private const string MachineKeyId = "machine-key-1";
    private const string CurrentSecret = "current-secret";
    private const string PreviousSecret = "previous-secret";
    private const string AssertionKeyId = "assertion-key-1";
    private const string Subject = "cf-subject-123";

    [Fact]
    public void Signed_request_round_trips_through_the_real_verifier()
    {
        using var assertionKey = RSA.Create(2048);
        var signer = CreateSigner(assertionKey);
        var now = DateTimeOffset.UtcNow;
        var body = Encoding.UTF8.GetBytes("{\"name\":\"acme\"}");

        var signed = signer.Sign("POST", "/api/v1/admin/clients", body, Subject, "idem-1", now);

        Assert.True(new AdminMachineProofVerifier(CreateAppAuthOptions()).Verify(signed.Proof, signed.Signature, now));
    }

    [Fact]
    public void Signer_uses_the_current_secret_and_never_the_previous_secret()
    {
        using var assertionKey = RSA.Create(2048);
        var signer = CreateSigner(assertionKey);
        var now = DateTimeOffset.UtcNow;
        var signed = signer.Sign("GET", "/api/v1/admin/clients", Encoding.UTF8.GetBytes("{}"), Subject, "idem-2", now);

        Assert.Equal(HmacSign(CurrentSecret, AdminMachineProofVerifier.Canonicalize(signed.Proof)), signed.Signature);
        Assert.NotEqual(HmacSign(PreviousSecret, AdminMachineProofVerifier.Canonicalize(signed.Proof)), signed.Signature);
    }

    [Fact]
    public void Proof_binds_method_path_body_assertion_and_identity()
    {
        using var assertionKey = RSA.Create(2048);
        var signer = CreateSigner(assertionKey);
        var now = DateTimeOffset.UtcNow;
        var body = Encoding.UTF8.GetBytes("{\"x\":1}");

        var signed = signer.Sign("POST", "/api/v1/admin/credentials?cursor=a", body, Subject, "idem-3", now);

        Assert.Equal("POST", signed.Proof.Method);
        Assert.Equal("/api/v1/admin/credentials?cursor=a", signed.Proof.PathAndQuery);
        Assert.Equal(ComputeSha256Hex(body), signed.Proof.BodyHash);
        Assert.Equal(ComputeSha256Hex(Encoding.UTF8.GetBytes(signed.AssertionJws)), signed.Proof.AssertionHash);
        Assert.Equal(AppId, signed.Proof.AppId);
        Assert.Equal(MachineKeyId, signed.Proof.KeyId);
        Assert.Equal(now.ToUnixTimeSeconds(), signed.Proof.TimestampUnixSeconds);
        Assert.Equal("idem-3", signed.Proof.IdempotencyKey);
    }

    private static AdminProxyRequestSigner CreateSigner(RSA assertionKey)
    {
        var options = new AdminAssertionIssuerOptions(
"admin-issuer", "admin-audience", AppId, AssertionKeyId, assertionKey.ExportRSAPrivateKeyPem());
        return new AdminProxyRequestSigner(new AdminAssertionIssuer(options), options, CreateAppAuthOptions());
    }

    private static AdminAppAuthOptions CreateAppAuthOptions() => new()
    {
        Apps =
        [
new AdminAppOptions
{
AppId = AppId,
KeyId = MachineKeyId,
CurrentSecret = CurrentSecret,
PreviousSecret = PreviousSecret,
},
        ],
    };

    private static string HmacSign(string secret, string canonical)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string ComputeSha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

public sealed class AdminIdentityHeaderPolicyTests
{
    private const string AppId = "admin-app";
    private const string MachineKeyId = "machine-key-1";
    private const string Subject = "cf-subject-123";

    [Fact]
    public async Task Forbidden_and_client_supplied_identity_headers_never_reach_the_downstream_request()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://api.internal/") };
        var client = new AdminApiClient(httpClient, CreateSigner());
        var context = CreateContext();

        foreach (var header in AdminIdentityHeaderPolicy.ForbiddenHeaders)
        {
context.Request.Headers[header] = "attacker-value";
        }

        context.Request.Headers[AdminAuthenticationContract.AppIdHeader] = "attacker-app";
        context.Request.Headers[AdminAuthenticationContract.KeyIdHeader] = "attacker-key";
        context.Request.Headers[AdminAuthenticationContract.TimestampHeader] = "1";
        context.Request.Headers[AdminAuthenticationContract.SignatureHeader] = "attacker-sig";
        context.Request.Headers[AdminAuthenticationContract.AssertionHeader] = "attacker-assertion";
        context.Request.Headers["Authorization"] = "Bearer attacker";
        context.Request.Headers["Cookie"] = "session=attacker";
        context.Request.Headers["Host"] = "evil.example";
        context.Request.Headers["Forwarded"] = "for=attacker";
        context.Request.Headers["X-Forwarded-For"] = "1.2.3.4";

        await client.SendAsync(new AdminProxyOperation("POST", "/clients", false), context, Subject, CancellationToken.None);

        var recorded = Assert.Single(handler.Requests);
        foreach (var header in AdminIdentityHeaderPolicy.ForbiddenHeaders)
        {
Assert.False(recorded.Headers.ContainsKey(header), $"{header} must not reach the API.");
        }

        Assert.False(recorded.Headers.ContainsKey("Authorization"));
        Assert.False(recorded.Headers.ContainsKey("Cookie"));
        Assert.False(recorded.Headers.ContainsKey("Host"));
        Assert.False(recorded.Headers.ContainsKey("Forwarded"));
        Assert.False(recorded.Headers.ContainsKey("X-Forwarded-For"));

        Assert.Equal(AppId, recorded.Headers[AdminAuthenticationContract.AppIdHeader]);
        Assert.Equal(MachineKeyId, recorded.Headers[AdminAuthenticationContract.KeyIdHeader]);
        Assert.NotEqual("attacker-assertion", recorded.Headers[AdminAuthenticationContract.AssertionHeader]);
    }

    private static AdminProxyRequestSigner CreateSigner()
    {
        using var assertionKey = RSA.Create(2048);
        var options = new AdminAssertionIssuerOptions(
"admin-issuer", "admin-audience", AppId, "assertion-key-1", assertionKey.ExportRSAPrivateKeyPem());
        return new AdminProxyRequestSigner(new AdminAssertionIssuer(options), options, new AdminAppAuthOptions
        {
Apps = [new AdminAppOptions { AppId = AppId, KeyId = MachineKeyId, CurrentSecret = "machine-secret" }],
        });
    }

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/v1/admin/clients";
        context.Request.QueryString = QueryString.Empty;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"name\":\"acme\"}"));
        context.Request.ContentType = "application/json";
        context.Response.Body = new MemoryStream();
        return context;
    }
}

public sealed class AdminApiClientTests
{
    private const string AppId = "admin-app";
    private const string MachineKeyId = "machine-key-1";
    private const string Subject = "cf-subject-123";

    [Fact]
    public async Task Oversized_body_returns_413_without_calling_downstream()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://api.internal/") };
        var client = new AdminApiClient(httpClient, CreateSigner());
        var context = CreateContext(new string('x', AdminAuthenticationContract.MaxBodyBytes + 1));

        await client.SendAsync(new AdminProxyOperation("POST", "/clients", false), context, Subject, CancellationToken.None);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Multiple_or_blank_idempotency_key_returns_400_without_calling_downstream()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://api.internal/") };
        var client = new AdminApiClient(httpClient, CreateSigner());

        var multiple = CreateContext("{}");
        multiple.Request.Headers[AdminAuthenticationContract.IdempotencyKeyHeader] = new[] { "a", "b" };
        await client.SendAsync(new AdminProxyOperation("POST", "/clients", false), multiple, Subject, CancellationToken.None);

        var blank = CreateContext("{}");
        blank.Request.Headers[AdminAuthenticationContract.IdempotencyKeyHeader] = "  ";
        await client.SendAsync(new AdminProxyOperation("POST", "/clients", false), blank, Subject, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, multiple.Response.StatusCode);
        Assert.Equal(StatusCodes.Status400BadRequest, blank.Response.StatusCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Absent_idempotency_key_generates_a_fresh_guid()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://api.internal/") };
        var client = new AdminApiClient(httpClient, CreateSigner());
        var context = CreateContext("{}");

        await client.SendAsync(new AdminProxyOperation("POST", "/clients", false), context, Subject, CancellationToken.None);

        var recorded = Assert.Single(handler.Requests);
        Assert.True(Guid.TryParse(recorded.Headers[AdminAuthenticationContract.IdempotencyKeyHeader], out _));
    }

    [Fact]
    public async Task If_match_is_forwarded_only_for_rotate_and_revoke()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://api.internal/") };
        var client = new AdminApiClient(httpClient, CreateSigner());

        var rotate = CreateContext("{}");
        rotate.Request.Headers["If-Match"] = "\"v2\"";
        await client.SendAsync(new AdminProxyOperation("POST", "/credentials/{credentialId:guid}/rotate", true), rotate, Subject, CancellationToken.None);

        var create = CreateContext("{}");
        create.Request.Headers["If-Match"] = "\"v2\"";
        await client.SendAsync(new AdminProxyOperation("POST", "/clients", false), create, Subject, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("\"v2\"", handler.Requests[0].Headers["If-Match"]);
        Assert.False(handler.Requests[1].Headers.ContainsKey("If-Match"));
    }

    [Fact]
    public async Task Semantic_headers_are_copied_to_the_downstream_request()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://api.internal/") };
        var client = new AdminApiClient(httpClient, CreateSigner());
        var context = CreateContext("{\"name\":\"acme\"}");
        context.Request.Headers["Accept"] = "application/json";

        await client.SendAsync(new AdminProxyOperation("POST", "/clients", false), context, Subject, CancellationToken.None);

        var recorded = Assert.Single(handler.Requests);
        Assert.Equal("application/json", recorded.Headers["Accept"]);
        Assert.Equal("application/json", recorded.Headers["Content-Type"]);
        Assert.Equal("{\"name\":\"acme\"}", Encoding.UTF8.GetString(recorded.Body));
    }

    [Fact]
    public async Task Response_headers_are_filtered_to_the_allowlist()
    {
        var handler = new RecordingHandler
        {
Respond = _ =>
{
var response = new HttpResponseMessage(HttpStatusCode.OK);
response.Headers.TryAddWithoutValidation("Location", "/api/v1/admin/clients/abc");
response.Headers.TryAddWithoutValidation("ETag", "\"v1\"");
response.Headers.TryAddWithoutValidation("Retry-After", "120");
response.Headers.TryAddWithoutValidation("X-Custom", "secret");
response.Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"ok\":true}"));
response.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
return response;
},
        };
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://api.internal/") };
        var client = new AdminApiClient(httpClient, CreateSigner());
        var context = CreateContext("{}");

        await client.SendAsync(new AdminProxyOperation("POST", "/clients", false), context, Subject, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(context.Response.Headers.ContainsKey("Location"));
        Assert.True(context.Response.Headers.ContainsKey("ETag"));
        Assert.True(context.Response.Headers.ContainsKey("Retry-After"));
        Assert.True(context.Response.Headers.ContainsKey("Content-Type"));
        Assert.False(context.Response.Headers.ContainsKey("X-Custom"));
    }

    [Fact]
    public async Task Downstream_connection_failure_returns_generic_502()
    {
        var handler = new RecordingHandler
        {
Respond = _ => throw new HttpRequestException("connection refused"),
        };
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://api.internal/") };
        var client = new AdminApiClient(httpClient, CreateSigner());
        var context = CreateContext("{}");

        await client.SendAsync(new AdminProxyOperation("POST", "/clients", false), context, Subject, CancellationToken.None);

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
    }

    private static AdminProxyRequestSigner CreateSigner()
    {
        using var assertionKey = RSA.Create(2048);
        var options = new AdminAssertionIssuerOptions(
"admin-issuer", "admin-audience", AppId, "assertion-key-1", assertionKey.ExportRSAPrivateKeyPem());
        return new AdminProxyRequestSigner(new AdminAssertionIssuer(options), options, new AdminAppAuthOptions
        {
Apps = [new AdminAppOptions { AppId = AppId, KeyId = MachineKeyId, CurrentSecret = "machine-secret" }],
        });
    }

    private static DefaultHttpContext CreateContext(string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/v1/admin/clients";
        context.Request.QueryString = QueryString.Empty;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentType = "application/json";
        context.Response.Body = new MemoryStream();
        return context;
    }
}
    
public sealed class CloudflareAssertionHeaderReaderTests
{
    [Fact]
    public void Single_non_empty_assertion_is_returned()
    {
        var reader = new CloudflareAssertionHeaderReader();
        var context = new DefaultHttpContext();
        context.Request.Headers[CloudflareAssertionHeaderReader.HeaderName] = "cf-assertion-token";
    
        Assert.Equal("cf-assertion-token", reader.Read(context.Request));
    }
    
    [Fact]
    public void Missing_blank_or_multiple_assertions_are_rejected()
    {
        var reader = new CloudflareAssertionHeaderReader();
    
        var missing = new DefaultHttpContext();
        Assert.Null(reader.Read(missing.Request));
    
        var blank = new DefaultHttpContext();
        blank.Request.Headers[CloudflareAssertionHeaderReader.HeaderName] = "   ";
        Assert.Null(reader.Read(blank.Request));
    
        var multiple = new DefaultHttpContext();
        multiple.Request.Headers[CloudflareAssertionHeaderReader.HeaderName] = new[] { "first", "second" };
        Assert.Null(reader.Read(multiple.Request));
    }
}
    
public sealed class AdminSubjectAllowlistTests
{
    [Fact]
    public void Membership_is_ordinal_and_case_sensitive()
    {
        var allowlist = new AdminSubjectAllowlist(new HashSet<string>(["cf-subject-123"], StringComparer.Ordinal));
    
        Assert.True(allowlist.Contains("cf-subject-123"));
        Assert.False(allowlist.Contains("CF-SUBJECT-123"));
        Assert.False(allowlist.Contains(" cf-subject-123"));
    }
    
    [Fact]
    public void Empty_allowlist_authorises_nobody()
    {
        var allowlist = new AdminSubjectAllowlist(new HashSet<string>(StringComparer.Ordinal));
    
        Assert.False(allowlist.Contains("cf-subject-123"));
        Assert.False(allowlist.Contains(string.Empty));
    }
}
    
public sealed class CloudflareAdminEndpointFilterTests
{
    private const string TeamIssuer = "https://team.cloudflareaccess.com";
    private const string CloudflareAud = "cf-app-aud";
    private const string AllowlistedSubject = "cf-subject-123";
    private const string SubjectItemKey = "adminapp.authenticated_subject";
    
    [Fact]
    public async Task Missing_blank_or_multiple_assertions_are_unauthorized_without_reaching_the_endpoint()
    {
        using var key = RSA.Create(2048);
    
        var missing = await InvokeAsync(key, Subjects(AllowlistedSubject), null);
        var blank = await InvokeAsync(key, Subjects(AllowlistedSubject), ["   "]);
        var multiple = await InvokeAsync(key, Subjects(AllowlistedSubject), ["first", "second"]);
    
        AssertUnauthorized(missing);
        AssertUnauthorized(blank);
        AssertUnauthorized(multiple);
    }
    
    [Fact]
    public async Task Invalid_or_foreign_assertion_is_unauthorized_without_reaching_the_endpoint()
    {
        using var key = RSA.Create(2048);
        using var foreign = RSA.Create(2048);
        var now = DateTimeOffset.UtcNow;
    
        var malformed = await InvokeAsync(key, Subjects(AllowlistedSubject), ["not-a-token"]);
        var wrongKey = await InvokeAsync(key, Subjects(AllowlistedSubject), [CreateToken(foreign, "cf-key", AllowlistedSubject, TeamIssuer, CloudflareAud, now)]);
        var wrongAudience = await InvokeAsync(key, Subjects(AllowlistedSubject), [CreateToken(key, "cf-key", AllowlistedSubject, TeamIssuer, "other-aud", now)]);
    
        AssertUnauthorized(malformed);
        AssertUnauthorized(wrongKey);
        AssertUnauthorized(wrongAudience);
    }
    
    [Fact]
    public async Task Valid_but_non_allowlisted_subject_is_forbidden_without_reaching_the_endpoint()
    {
        using var key = RSA.Create(2048);
        var token = CreateToken(key, "cf-key", "cf-subject-999", TeamIssuer, CloudflareAud, DateTimeOffset.UtcNow);
    
        var outcome = await InvokeAsync(key, Subjects(AllowlistedSubject), [token]);
    
        Assert.False(outcome.NextInvoked);
        var result = Assert.IsAssignableFrom<IStatusCodeHttpResult>(outcome.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }
    
    [Fact]
    public async Task Valid_subject_with_empty_allowlist_is_forbidden_without_reaching_the_endpoint()
    {
        using var key = RSA.Create(2048);
        var token = CreateToken(key, "cf-key", AllowlistedSubject, TeamIssuer, CloudflareAud, DateTimeOffset.UtcNow);
    
        var outcome = await InvokeAsync(key, Subjects(), [token]);
    
        Assert.False(outcome.NextInvoked);
        var result = Assert.IsAssignableFrom<IStatusCodeHttpResult>(outcome.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }
    
    [Fact]
    public async Task Valid_allowlisted_subject_reaches_the_endpoint_with_the_subject_bound()
    {
        using var key = RSA.Create(2048);
        var token = CreateToken(key, "cf-key", AllowlistedSubject, TeamIssuer, CloudflareAud, DateTimeOffset.UtcNow);
    
        var outcome = await InvokeAsync(key, Subjects(AllowlistedSubject), [token]);
    
        Assert.True(outcome.NextInvoked);
        Assert.Equal(AllowlistedSubject, outcome.Context.Items[SubjectItemKey]);
    }
    
    private static async Task<FilterOutcome> InvokeAsync(RSA key, IReadOnlySet<string> allowlist, string[]? headerValues)
    {
        var validator = new CloudflareAccessValidator(new CloudflareAccessOptions(
TeamIssuer,
CloudflareAud,
[new CloudflareAccessKey("cf-key", key.ExportRSAPublicKeyPem())]));
        var filter = new CloudflareAdminEndpointFilter(
new CloudflareAssertionHeaderReader(),
validator,
new AdminSubjectAllowlist(allowlist));
    
        var context = new DefaultHttpContext();
        if (headerValues is not null)
        {
context.Request.Headers[CloudflareAssertionHeaderReader.HeaderName] = headerValues;
        }
    
        var nextInvoked = false;
        var result = await filter.InvokeAsync(
EndpointFilterInvocationContext.Create(context),
_ =>
{
nextInvoked = true;
return ValueTask.FromResult<object?>(Results.Ok());
});

        return new FilterOutcome(context, nextInvoked, result);
    }

    private static IReadOnlySet<string> Subjects(params string[] subjects) =>
        new HashSet<string>(subjects, StringComparer.Ordinal);

    private static void AssertUnauthorized(FilterOutcome outcome)
    {
        Assert.False(outcome.NextInvoked);
        var result = Assert.IsAssignableFrom<IStatusCodeHttpResult>(outcome.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
    }

    private static string CreateToken(RSA key, string kid, string subject, string issuer, string audience, DateTimeOffset now)
    {
        var descriptor = new SecurityTokenDescriptor
        {
Issuer = issuer,
Audience = audience,
IssuedAt = now.UtcDateTime,
NotBefore = now.UtcDateTime,
Expires = now.AddMinutes(5).UtcDateTime,
Claims = new Dictionary<string, object> { ["sub"] = subject },
SigningCredentials = new SigningCredentials(new RsaSecurityKey(key) { KeyId = kid }, SecurityAlgorithms.RsaSha256),
        };
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityTokenHandler().CreateToken(descriptor));
    }

    private sealed record FilterOutcome(HttpContext Context, bool NextInvoked, object? Result);
}
    
public sealed class AdminApiHealthProbeTests
{
    [Fact]
    public async Task Reachable_api_is_healthy()
    {
        var probe = CreateProbe(new RecordingHandler());
    
        Assert.True(await probe.IsReachableAsync(CancellationToken.None));
    }
    
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Non_success_response_is_unhealthy(HttpStatusCode status)
    {
        var handler = new RecordingHandler { Respond = _ => new HttpResponseMessage(status) };
        var probe = CreateProbe(handler);
    
        Assert.False(await probe.IsReachableAsync(CancellationToken.None));
    }
    
    [Fact]
    public async Task Connection_refused_is_unhealthy()
    {
        var handler = new RecordingHandler { Respond = _ => throw new HttpRequestException("connection refused") };
        var probe = CreateProbe(handler);
    
        Assert.False(await probe.IsReachableAsync(CancellationToken.None));
    }
    
    [Fact]
    public async Task Timeout_is_unhealthy()
    {
        var handler = new RecordingHandler { Respond = _ => throw new TaskCanceledException("timeout") };
        var probe = CreateProbe(handler);
    
        Assert.False(await probe.IsReachableAsync(CancellationToken.None));
    }
    
    private static AdminApiHealthProbe CreateProbe(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://api:8080/") });
}
    
internal sealed record RecordedRequest(string Method, string PathAndQuery, IReadOnlyDictionary<string, string> Headers, byte[] Body);

internal sealed class RecordingHandler : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = [];

    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ =>
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
headers[header.Key] = string.Join(",", header.Value);
        }

        if (request.Content is not null)
        {
foreach (var header in request.Content.Headers)
{
headers[header.Key] = string.Join(",", header.Value);
}
        }

        var body = request.Content is null
? Array.Empty<byte>()
: await request.Content.ReadAsByteArrayAsync(cancellationToken);

        Requests.Add(new RecordedRequest(request.Method.Method, request.RequestUri!.PathAndQuery, headers, body));
        return Respond(request);
    }
}
