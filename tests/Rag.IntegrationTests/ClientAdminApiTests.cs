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
public sealed class ClientAdminApiTests(PostgreSqlFixture fixture) : IAsyncLifetime
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
    public async Task Client_lifecycle_creates_lists_and_inspects_without_secrets()
    {
        var created = await SendAdminAsync(HttpMethod.Post, "/api/v1/admin/clients", "{\"name\":\"integration-a\",\"description\":\"first\"}");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdJson = await ReadJsonAsync(created);
        var clientId = createdJson.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("integration-a", createdJson.RootElement.GetProperty("name").GetString());
        Assert.Equal("first", createdJson.RootElement.GetProperty("description").GetString());
        Assert.False(createdJson.RootElement.TryGetProperty("secret", out _));

        var duplicate = await SendAdminAsync(HttpMethod.Post, "/api/v1/admin/clients", "{\"name\":\"integration-a\"}");
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("duplicate", await ProblemCodeAsync(duplicate));

        var blank = await SendAdminAsync(HttpMethod.Post, "/api/v1/admin/clients", "{\"name\":\"\"}");
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);

        var overlong = await SendAdminAsync(HttpMethod.Post, "/api/v1/admin/clients", $"{{\"name\":\"{new string('x', 201)}\"}}");
        Assert.Equal(HttpStatusCode.BadRequest, overlong.StatusCode);

        var list = await SendAdminAsync(HttpMethod.Get, "/api/v1/admin/clients", null);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var listJson = await ReadJsonAsync(list);
        Assert.Contains(listJson.RootElement.GetProperty("items").EnumerateArray(), item => item.GetProperty("id").GetGuid() == clientId);

        var detail = await SendAdminAsync(HttpMethod.Get, $"/api/v1/admin/clients/{clientId}", null);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var detailJson = await ReadJsonAsync(detail);
        Assert.Equal(clientId, detailJson.RootElement.GetProperty("client").GetProperty("id").GetGuid());
        Assert.Equal(0, detailJson.RootElement.GetProperty("credentials").GetArrayLength());

        var missing = await SendAdminAsync(HttpMethod.Get, $"/api/v1/admin/clients/{Guid.NewGuid()}", null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
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

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = await ReadJsonAsync(response);
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
