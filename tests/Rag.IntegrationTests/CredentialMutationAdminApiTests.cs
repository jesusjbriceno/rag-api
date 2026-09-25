using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Rag.Application;
using Rag.Infrastructure;

namespace Rag.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
public sealed class CredentialMutationAdminApiTests(PostgreSqlFixture fixture) : IAsyncLifetime
{
    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), $"rag-credential-mutation-tests-{Guid.NewGuid():N}");
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
    public async Task Credential_mutations_enforce_versions_secret_delivery_and_concurrency()
    {
        var credentialId = await IssueCredentialAsync();

        var missingIfMatch = await SendAdminAsync(HttpMethod.Post, $"/api/v1/admin/credentials/{credentialId}/rotate", null);
        Assert.Equal(HttpStatusCode.BadRequest, missingIfMatch.StatusCode);
        Assert.Equal("invalid_input", await ProblemCodeAsync(missingIfMatch));

        var rotationKey = Guid.NewGuid().ToString("D");
        var rotated = await SendAdminAsync(HttpMethod.Post, $"/api/v1/admin/credentials/{credentialId}/rotate", null, rotationKey, "\"v1\"");
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        using var rotatedJson = await ReadJsonAsync(rotated);
        Assert.False(string.IsNullOrWhiteSpace(rotatedJson.RootElement.GetProperty("secret").GetString()));
        Assert.Equal(2, rotatedJson.RootElement.GetProperty("credential").GetProperty("version").GetInt32());

        var replayedRotation = await SendAdminAsync(HttpMethod.Post, $"/api/v1/admin/credentials/{credentialId}/rotate", null, rotationKey, "\"v2\"");
        Assert.Equal(HttpStatusCode.Conflict, replayedRotation.StatusCode);
        using var replayedJson = await ReadJsonAsync(replayedRotation);
        Assert.Equal("secret_already_delivered", replayedJson.RootElement.GetProperty("code").GetString());
        Assert.False(replayedJson.RootElement.TryGetProperty("secret", out _));

        var staleRevoke = await SendAdminAsync(HttpMethod.Post, $"/api/v1/admin/credentials/{credentialId}/revoke", null, ifMatch: "\"v1\"");
        Assert.Equal(HttpStatusCode.Conflict, staleRevoke.StatusCode);
        Assert.Equal("version_conflict", await ProblemCodeAsync(staleRevoke));

        var concurrentCredentialId = await IssueCredentialAsync();
        var rotate = SendAdminAsync(HttpMethod.Post, $"/api/v1/admin/credentials/{concurrentCredentialId}/rotate", null, ifMatch: "\"v1\"");
        var revoke = SendAdminAsync(HttpMethod.Post, $"/api/v1/admin/credentials/{concurrentCredentialId}/revoke", null, ifMatch: "\"v1\"");
        var statuses = (await Task.WhenAll(rotate, revoke)).Select(response => response.StatusCode).OrderBy(status => status).ToList();

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], statuses);
    }

    [Fact]
    public async Task Credential_mutation_responses_pin_the_credential_and_delivery_key_sets()
    {
        var credentialId = await IssueCredentialAsync();

        var rotated = await SendAdminAsync(HttpMethod.Post, $"/api/v1/admin/credentials/{credentialId}/rotate", null, ifMatch: "\"v1\"");
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        using var rotatedJson = await ReadJsonAsync(rotated);
        Assert.Equal(Keys("credential", "secret"), PropertyNames(rotatedJson.RootElement));
        var rotatedCredential = rotatedJson.RootElement.GetProperty("credential");
        Assert.Equal(CredentialKeys(), PropertyNames(rotatedCredential));
        Assert.Equal("active", rotatedCredential.GetProperty("state").GetString());
        Assert.False(string.IsNullOrWhiteSpace(rotatedJson.RootElement.GetProperty("secret").GetString()));

        var revoked = await SendAdminAsync(HttpMethod.Post, $"/api/v1/admin/credentials/{credentialId}/revoke", null, ifMatch: "\"v2\"");
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        using var revokedJson = await ReadJsonAsync(revoked);
        Assert.Equal(CredentialKeys(), PropertyNames(revokedJson.RootElement));
        Assert.Equal("revoked", revokedJson.RootElement.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.String, revokedJson.RootElement.GetProperty("revokedAt").ValueKind);
    }

    private async Task<Guid> IssueCredentialAsync()
    {
        var client = await SendAdminAsync(HttpMethod.Post, "/api/v1/admin/clients", $"{{\"name\":\"credential-mutation-{Guid.NewGuid():N}\"}}");
        Assert.Equal(HttpStatusCode.Created, client.StatusCode);
        using var clientJson = await ReadJsonAsync(client);
        var clientId = clientJson.RootElement.GetProperty("id").GetGuid();

        var credential = await SendAdminAsync(HttpMethod.Post, $"/api/v1/admin/clients/{clientId}/credentials", "{}");
        Assert.Equal(HttpStatusCode.Created, credential.StatusCode);
        using var credentialJson = await ReadJsonAsync(credential);
        return credentialJson.RootElement.GetProperty("credential").GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> SendAdminAsync(HttpMethod method, string pathAndQuery, string? body, string? idempotencyKey = null, string? ifMatch = null)
    {
        var now = DateTimeOffset.UtcNow;
        var key = idempotencyKey ?? Guid.NewGuid().ToString("D");
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
            key);
        var signature = Convert.ToBase64String(AdminApiFactory.HmacSign(AdminApiFactory.MachineSecret, AdminMachineProofVerifier.Canonicalize(proof)));
        var request = new HttpRequestMessage(method, pathAndQuery);
        request.Headers.Add("X-Admin-App-Id", AdminApiFactory.AppId);
        request.Headers.Add("X-Admin-Key-Id", AdminApiFactory.KeyId);
        request.Headers.Add("X-Admin-Timestamp", now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("X-Admin-Signature", signature);
        request.Headers.Add("X-Admin-Assertion", assertion);
        request.Headers.Add("Idempotency-Key", key);
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return _client.SendAsync(request);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = await ReadJsonAsync(response);
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static string[] PropertyNames(JsonElement element) =>
        [.. element.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal)];

    private static string[] Keys(params string[] names) =>
        [.. names.OrderBy(name => name, StringComparer.Ordinal)];

    private static string[] CredentialKeys() =>
        Keys("id", "clientId", "keyId", "description", "version", "state", "createdAt", "expiresAt", "lastRotatedAt", "revokedAt");
}
