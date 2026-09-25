using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Rag.Application;
using Rag.Domain;
using Rag.Infrastructure;

namespace Rag.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
public sealed class AdminApiTests(PostgreSqlFixture fixture) : IAsyncLifetime
{
    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), $"rag-admin-tests-{Guid.NewGuid():N}");
    private AdminApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new AdminApiFactory(fixture.ConnectionString, _contentRoot);
        _client = _factory.CreateClient();
        var options = new DbContextOptionsBuilder<IngestionDbContext>()
            .UseNpgsql(fixture.ConnectionString, options => options.UseVector())
            .Options;
        await using var context = new IngestionDbContext(options);
        await context.Database.MigrateAsync();
        await context.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE admin_assertion_replays, admin_operations, admin_audit_events, client_credentials, service_clients, operations, chunks, document_versions, documents, collections CASCADE;");
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        if (Directory.Exists(_contentRoot))
        {
            Directory.Delete(_contentRoot, recursive: true);
        }

        return Task.CompletedTask;
    }

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

    [Fact]
    public async Task Every_admin_route_rejects_public_jwt_only_raw_header_and_one_factor_requests()
    {
        var jwtToken = await IssueServiceClientTokenAsync();

        foreach (var route in Routes)
        {
            var publicRequest = new HttpRequestMessage(route.Method, route.Path);
            var jwtRequest = new HttpRequestMessage(route.Method, route.Path);
            jwtRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwtToken);
            var rawHeaderRequest = new HttpRequestMessage(route.Method, route.Path);
            rawHeaderRequest.Headers.Add("Cf-Access-Jwt-Assertion", "forged-identity");
            var oneFactorRequest = AdminRequest(route.Method, route.Path, route.Body, includeAssertion: false);

            Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(publicRequest)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(jwtRequest)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(rawHeaderRequest)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(oneFactorRequest)).StatusCode);
        }
    }

    [Fact]
    public async Task Idempotent_mutations_do_not_duplicate_and_secrets_are_never_redelivered()
    {
        var idempotencyKey = Guid.NewGuid().ToString("D");
        var created = await SendAdminAsync(HttpMethod.Post, "/api/v1/admin/clients", "{\"name\":\"idem-client\"}", idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var clientId = (await ReadJsonAsync(created)).RootElement.GetProperty("id").GetGuid();

        var retry = await SendAdminAsync(HttpMethod.Post, "/api/v1/admin/clients", "{\"name\":\"idem-client\"}", idempotencyKey);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(clientId, (await ReadJsonAsync(retry)).RootElement.GetProperty("id").GetGuid());

        var list = await SendAdminAsync(HttpMethod.Get, "/api/v1/admin/clients", null);
        var listJson = await ReadJsonAsync(list);
        Assert.Equal(1, listJson.RootElement.GetProperty("items").EnumerateArray().Count(item => item.GetProperty("id").GetGuid() == clientId));

        var issueKey = Guid.NewGuid().ToString("D");
        var issue = await SendAdminAsync(HttpMethod.Post, $"/api/v1/admin/clients/{clientId}/credentials", "{}", issueKey);
        Assert.Equal(HttpStatusCode.Created, issue.StatusCode);

        var issueRetry = await SendAdminAsync(HttpMethod.Post, $"/api/v1/admin/clients/{clientId}/credentials", "{}", issueKey);
        Assert.Equal(HttpStatusCode.Conflict, issueRetry.StatusCode);
        var issueRetryJson = await ReadJsonAsync(issueRetry);
        Assert.Equal("secret_already_delivered", issueRetryJson.RootElement.GetProperty("code").GetString());
        Assert.False(issueRetryJson.RootElement.TryGetProperty("secret", out _));

        var mismatched = await SendAdminAsync(HttpMethod.Post, "/api/v1/admin/clients", "{\"name\":\"different-name\"}", idempotencyKey);
        Assert.Equal(HttpStatusCode.Conflict, mismatched.StatusCode);
        var mismatchedJson = await ReadJsonAsync(mismatched);
        Assert.Equal("idempotency_conflict", mismatchedJson.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Invalid_expiry_is_rejected_without_creating_a_credential()
    {
        var clientId = await CreateClientAsync("expiry-client");
        var issue = await SendAdminAsync(HttpMethod.Post, $"/api/v1/admin/clients/{clientId}/credentials", $"{{\"expiresAt\":\"{DateTimeOffset.UtcNow.AddDays(-1):O}\"}}");
        Assert.Equal(HttpStatusCode.BadRequest, issue.StatusCode);

        var list = await SendAdminAsync(HttpMethod.Get, $"/api/v1/admin/clients/{clientId}/credentials", null);
        Assert.Equal(0, (await ReadJsonAsync(list)).RootElement.GetArrayLength());
    }

    [Fact]
    public async Task Pre_admin_client_exchanges_and_coexists_with_admin_audit()
    {
        using var scope = _factory.Services.CreateScope();
        var preAdmin = await scope.ServiceProvider.GetRequiredService<CredentialOperator>()
            .IssueAsync($"pre-admin-{Guid.NewGuid():N}", null);

        var firstExchange = await _client.PostAsJsonAsync("/api/v1/auth/token", new { keyId = preAdmin.KeyId, secret = preAdmin.Secret });
        Assert.Equal(HttpStatusCode.OK, firstExchange.StatusCode);

        var adminCreated = await SendAdminAsync(HttpMethod.Post, "/api/v1/admin/clients", "{\"name\":\"admin-client\"}");
        Assert.Equal(HttpStatusCode.Created, adminCreated.StatusCode);

        var reExchange = await _client.PostAsJsonAsync("/api/v1/auth/token", new { keyId = preAdmin.KeyId, secret = preAdmin.Secret });
        Assert.Equal(HttpStatusCode.OK, reExchange.StatusCode);

        var audit = await SendAdminAsync(HttpMethod.Get, "/api/v1/admin/audit", null);
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        var auditJson = await ReadJsonAsync(audit);
        Assert.Contains(auditJson.RootElement.GetProperty("items").EnumerateArray(), item => item.GetProperty("action").GetString() == "create_client");
    }

    [Fact]
    public async Task Expired_credential_is_denied_while_active_credential_remains_usable()
    {
        var clientId = await CreateClientAsync("post-expiry-client");
        var issue = await SendAdminAsync(HttpMethod.Post, $"/api/v1/admin/clients/{clientId}/credentials", $"{{\"expiresAt\":\"{DateTimeOffset.UtcNow.AddHours(1):O}\"}}");
        Assert.Equal(HttpStatusCode.Created, issue.StatusCode);
        var issuedJson = await ReadJsonAsync(issue);
        var expiredKeyId = issuedJson.RootElement.GetProperty("credential").GetProperty("keyId").GetString()!;
        var expiredSecret = issuedJson.RootElement.GetProperty("secret").GetString()!;

        await BackdateExpiryAsync(expiredKeyId);

        var expiredExchange = await _client.PostAsJsonAsync("/api/v1/auth/token", new { keyId = expiredKeyId, secret = expiredSecret });
        Assert.Equal(HttpStatusCode.Unauthorized, expiredExchange.StatusCode);

        var activeIssue = await SendAdminAsync(HttpMethod.Post, $"/api/v1/admin/clients/{clientId}/credentials", "{}");
        Assert.Equal(HttpStatusCode.Created, activeIssue.StatusCode);
        var activeJson = await ReadJsonAsync(activeIssue);
        var activeKeyId = activeJson.RootElement.GetProperty("credential").GetProperty("keyId").GetString()!;
        var activeSecret = activeJson.RootElement.GetProperty("secret").GetString()!;

        var activeExchange = await _client.PostAsJsonAsync("/api/v1/auth/token", new { keyId = activeKeyId, secret = activeSecret });
        Assert.Equal(HttpStatusCode.OK, activeExchange.StatusCode);
    }

    private async Task BackdateExpiryAsync(string keyId)
    {
        var options = new DbContextOptionsBuilder<IngestionDbContext>()
            .UseNpgsql(fixture.ConnectionString, options => options.UseVector())
            .Options;
        await using var context = new IngestionDbContext(options);
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE client_credentials SET \"ExpiresAt\" = now() - interval '1 hour' WHERE \"KeyId\" = {keyId};");
    }

    private async Task<Guid> CreateClientAsync(string name)
    {
        var response = await SendAdminAsync(HttpMethod.Post, "/api/v1/admin/clients", $"{{\"name\":\"{name}\"}}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadJsonAsync(response)).RootElement.GetProperty("id").GetGuid();
    }

    private async Task<string> IssueServiceClientTokenAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var issued = await scope.ServiceProvider.GetRequiredService<CredentialOperator>()
            .IssueAsync($"jwt-client-{Guid.NewGuid():N}", null);
        var exchange = await _client.PostAsJsonAsync("/api/v1/auth/token", new { keyId = issued.KeyId, secret = issued.Secret });
        return (await exchange.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;
    }

    private Task<HttpResponseMessage> SendAdminAsync(HttpMethod method, string pathAndQuery, string? body, string? idempotencyKey = null, string? ifMatch = null)
    {
        var request = AdminRequest(method, pathAndQuery, body, includeAssertion: true, idempotencyKey: idempotencyKey);
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return _client.SendAsync(request);
    }

    private HttpRequestMessage AdminRequest(HttpMethod method, string pathAndQuery, string? body, bool includeAssertion, string? idempotencyKey = null)
    {
        var now = DateTimeOffset.UtcNow;
        var key = idempotencyKey ?? Guid.NewGuid().ToString("D");
        var assertion = includeAssertion ? _factory.CreateAssertion(now) : null;
        var bodyBytes = body is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body);
        var bodyHash = Convert.ToHexStringLower(SHA256.HashData(bodyBytes));
        var assertionHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(assertion ?? string.Empty)));
        var proof = new AdminMachineProof(
            method.Method,
            pathAndQuery,
            bodyHash,
            assertionHash,
            AdminApiFactory.AppId,
            AdminApiFactory.KeyId,
            now.ToUnixTimeSeconds(),
            key);
        var signature = Convert.ToBase64String(AdminApiFactory.HmacSign(AdminApiFactory.MachineSecret, AdminMachineProofVerifier.Canonicalize(proof)));

        var request = new HttpRequestMessage(method, pathAndQuery);
        request.Headers.Add("X-Admin-App-Id", AdminApiFactory.AppId);
        request.Headers.Add("X-Admin-Key-Id", AdminApiFactory.KeyId);
        request.Headers.Add("X-Admin-Timestamp", now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("X-Admin-Signature", signature);
        if (assertion is not null)
        {
            request.Headers.Add("X-Admin-Assertion", assertion);
        }

        request.Headers.Add("Idempotency-Key", key);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = await ReadJsonAsync(response);
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string AccessToken);
}
