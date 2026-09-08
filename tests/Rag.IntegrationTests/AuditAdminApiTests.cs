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
public sealed class AuditAdminApiTests(PostgreSqlFixture fixture) : IAsyncLifetime
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
    public async Task Audit_route_returns_ordered_secret_free_keyset_pages()
    {
        var clientId = await CreateClientAsync("audit-a");
        await CreateClientAsync("audit-b");
        var issue = await SendAdminAsync(HttpMethod.Post, $"/api/v1/admin/clients/{clientId}/credentials", "{}");
        Assert.Equal(HttpStatusCode.Created, issue.StatusCode);
        var issuedSecret = (await ReadJsonAsync(issue)).RootElement.GetProperty("secret").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(issuedSecret));

        var audit = await SendAdminAsync(HttpMethod.Get, "/api/v1/admin/audit", null);
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        var rawAudit = await audit.Content.ReadAsStringAsync();
        using var auditJson = JsonDocument.Parse(rawAudit);
        var items = auditJson.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, items.Count);
        Assert.Contains(items, item => item.GetProperty("action").GetString() == "create_client");
        Assert.Contains(items, item => item.GetProperty("action").GetString() == "issue_credential");
        Assert.All(items, item => Assert.Equal(AdminApiFactory.Subject, item.GetProperty("actorSubject").GetString()));
        AssertOrdered(items);
        Assert.DoesNotContain(issuedSecret, rawAudit, StringComparison.Ordinal);
        Assert.DoesNotContain("secret_hash", rawAudit, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SecretHash", rawAudit, StringComparison.Ordinal);

        var firstPage = await SendAdminAsync(HttpMethod.Get, "/api/v1/admin/audit?limit=2", null);
        var firstJson = await ReadJsonAsync(firstPage);
        var firstItems = firstJson.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, firstItems.Count);
        var nextCursor = firstJson.RootElement.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(nextCursor));

        var secondPage = await SendAdminAsync(HttpMethod.Get, $"/api/v1/admin/audit?limit=2&cursor={nextCursor}", null);
        var secondJson = await ReadJsonAsync(secondPage);
        var secondItems = secondJson.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(secondItems);
        Assert.Equal(JsonValueKind.Null, secondJson.RootElement.GetProperty("nextCursor").ValueKind);
        var pagedItems = firstItems.Concat(secondItems).ToList();
        Assert.Equal(3, pagedItems.Select(item => item.GetProperty("id").GetGuid()).Distinct().Count());
        AssertOrdered(pagedItems);

        var invalidCursor = await SendAdminAsync(HttpMethod.Get, "/api/v1/admin/audit?cursor=not-base64", null);
        Assert.Equal(HttpStatusCode.BadRequest, invalidCursor.StatusCode);
        var invalidLimit = await SendAdminAsync(HttpMethod.Get, "/api/v1/admin/audit?limit=0", null);
        Assert.Equal(HttpStatusCode.BadRequest, invalidLimit.StatusCode);
    }

    private async Task<Guid> CreateClientAsync(string name)
    {
        var response = await SendAdminAsync(HttpMethod.Post, "/api/v1/admin/clients", $"{{\"name\":\"{name}\"}}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadJsonAsync(response)).RootElement.GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> SendAdminAsync(HttpMethod method, string pathAndQuery, string? body)
    {
        var now = DateTimeOffset.UtcNow;
        var assertion = _factory.CreateAssertion(now);
        var bodyBytes = body is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body);
        var proof = new AdminMachineProof(
            method.Method,
            pathAndQuery,
            Convert.ToHexStringLower(SHA256.HashData(bodyBytes)),
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(assertion))),
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

    private static void AssertOrdered(IReadOnlyList<JsonElement> items)
    {
        for (var index = 1; index < items.Count; index++)
        {
            var previous = items[index - 1];
            var current = items[index];
            var previousOccurredAt = DateTimeOffset.Parse(previous.GetProperty("occurredAt").GetString()!, CultureInfo.InvariantCulture);
            var currentOccurredAt = DateTimeOffset.Parse(current.GetProperty("occurredAt").GetString()!, CultureInfo.InvariantCulture);
            Assert.True(
                previousOccurredAt < currentOccurredAt ||
                (previousOccurredAt == currentOccurredAt && previous.GetProperty("id").GetGuid().CompareTo(current.GetProperty("id").GetGuid()) < 0),
                "Audit events must be ordered by occurredAt and id.");
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }
}
