using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using Rag.Application;
using Rag.Infrastructure;

namespace Rag.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
public sealed class CredentialReadAdminApiTests(PostgreSqlFixture fixture) : IAsyncLifetime
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

    [Fact]
    public async Task Credential_routes_issue_list_and_get_without_redelivering_secret()
    {
        var clientId = await CreateClientAsync();

        var issue = await SendAdminAsync(HttpMethod.Post, $"/api/v1/admin/clients/{clientId}/credentials", "{}");
        Assert.Equal(HttpStatusCode.Created, issue.StatusCode);
        var issuedJson = await ReadJsonAsync(issue);
        Assert.Equal(Keys("credential", "secret"), PropertyNames(issuedJson.RootElement));
        var issuedCredential = issuedJson.RootElement.GetProperty("credential");
        Assert.Equal(CredentialKeys(), PropertyNames(issuedCredential));
        Assert.Equal("active", issuedCredential.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, issuedCredential.GetProperty("expiresAt").ValueKind);
        var credentialId = issuedCredential.GetProperty("id").GetGuid();
        Assert.False(string.IsNullOrWhiteSpace(issuedJson.RootElement.GetProperty("secret").GetString()));

        var list = await SendAdminAsync(HttpMethod.Get, $"/api/v1/admin/clients/{clientId}/credentials", null);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listJson = await ReadJsonAsync(list);
        Assert.Single(listJson.RootElement.EnumerateArray());
        Assert.Equal(CredentialKeys(), PropertyNames(listJson.RootElement[0]));
        Assert.Equal(credentialId, listJson.RootElement[0].GetProperty("id").GetGuid());
        Assert.False(listJson.RootElement[0].TryGetProperty("secret", out _));

        var get = await SendAdminAsync(HttpMethod.Get, $"/api/v1/admin/credentials/{credentialId}", null);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var getJson = await ReadJsonAsync(get);
        Assert.Equal(CredentialKeys(), PropertyNames(getJson.RootElement));
        Assert.Equal(credentialId, getJson.RootElement.GetProperty("id").GetGuid());
        Assert.False(getJson.RootElement.TryGetProperty("secret", out _));
    }

    private async Task<Guid> CreateClientAsync()
    {
        var response = await SendAdminAsync(HttpMethod.Post, "/api/v1/admin/clients", "{\"name\":\"credential-read-client\"}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadJsonAsync(response)).RootElement.GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> SendAdminAsync(HttpMethod method, string pathAndQuery, string? body)
    {
        var now = DateTimeOffset.UtcNow;
        var assertion = _factory.CreateAssertion(now);
        var bodyBytes = body is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body);
        var bodyHash = Convert.ToHexStringLower(SHA256.HashData(bodyBytes));
        var assertionHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(assertion)));
        var proof = new AdminMachineProof(
            method.Method,
            pathAndQuery,
            bodyHash,
            assertionHash,
            AdminApiFactory.AppId,
            AdminApiFactory.KeyId,
            now.ToUnixTimeSeconds(),
            Guid.NewGuid().ToString("D"));
        var signature = Convert.ToBase64String(AdminApiFactory.HmacSign(AdminApiFactory.MachineSecret, AdminMachineProofVerifier.Canonicalize(proof)));

        var request = new HttpRequestMessage(method, pathAndQuery);
        request.Headers.Add("X-Admin-App-Id", AdminApiFactory.AppId);
        request.Headers.Add("X-Admin-Key-Id", AdminApiFactory.KeyId);
        request.Headers.Add("X-Admin-Timestamp", now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("X-Admin-Signature", signature);
        request.Headers.Add("X-Admin-Assertion", assertion);
        request.Headers.Add("Idempotency-Key", proof.IdempotencyKey);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return _client.SendAsync(request);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }

    private static string[] PropertyNames(JsonElement element) =>
        [.. element.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal)];

    private static string[] Keys(params string[] names) =>
        [.. names.OrderBy(name => name, StringComparer.Ordinal)];

    private static string[] CredentialKeys() =>
        Keys("id", "clientId", "keyId", "description", "version", "state", "createdAt", "expiresAt", "lastRotatedAt", "revokedAt");
}
