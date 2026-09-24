using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Rag.Application;
using Rag.Application.Auth;
using Rag.Domain;
using Rag.Infrastructure;

namespace Rag.IntegrationTests;

[Collection(PostgreSqlCollection.Name)]
public sealed class ProtectedApiTests(PostgreSqlFixture fixture) : IAsyncLifetime
{
    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), $"rag-api-tests-{Guid.NewGuid():N}");
    private ProtectedApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new ProtectedApiFactory(fixture.ConnectionString, _contentRoot);
        _client = _factory.CreateClient();
        var options = new DbContextOptionsBuilder<IngestionDbContext>()
            .UseNpgsql(fixture.ConnectionString, options => options.UseVector())
            .Options;
        await using var context = new IngestionDbContext(options);
        await context.Database.MigrateAsync();
        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE client_credentials, service_clients, operations, chunks, document_versions, documents, collections CASCADE;");
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
    public async Task Protected_routes_require_a_bearer_token_with_problem_details()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/collections", new { name = "reports" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Liveness_is_anonymous_and_readiness_is_not_hidden_by_authorization()
    {
        var liveness = await _client.GetAsync("/api/v1/health/live");
        var readiness = await _client.GetAsync("/api/v1/health/ready");

        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, readiness.StatusCode);
    }

    [Fact]
    public async Task Collection_and_txt_ingestion_enforce_owner_and_input_contracts()
    {
        var owner = await CreateAuthenticatedClientAsync("owner");
        var foreign = await CreateAuthenticatedClientAsync("foreign");
        var collection = await CreateCollectionAsync(owner, "Reports");

        var wrongType = await SendAsync(owner.Token, HttpMethod.Post, $"/api/v1/collections/{collection.Id}/ingestions:txt", "text/plain", "text");
        var wrongName = await SendAsync(owner.Token, HttpMethod.Post, $"/api/v1/collections/{collection.Id}/ingestions:txt", "application/json", "{\"file_name\":\"report.pdf\",\"content\":\"text\"}");
        var accepted = await SendAsync(owner.Token, HttpMethod.Post, $"/api/v1/collections/{collection.Id}/ingestions:txt", "application/json", "{\"file_name\":\"report.txt\",\"content\":\"text\",\"external_reference\":\"source://report\"}");
        var duplicate = await SendAsync(owner.Token, HttpMethod.Post, $"/api/v1/collections/{collection.Id}/ingestions:txt", "application/json", "{\"file_name\":\"report.txt\",\"content\":\"text\",\"external_reference\":\"source://report\"}");
        var foreignRead = await SendAsync(foreign.Token, HttpMethod.Get, $"/api/v1/collections/{collection.Id}/operations/{(await accepted.Content.ReadFromJsonAsync<IngestionResponse>())!.OperationId}");
        var tooLarge = await SendAsync(owner.Token, HttpMethod.Post, $"/api/v1/collections/{collection.Id}/ingestions:txt", "application/json", "{\"file_name\":\"large.txt\",\"content\":\"" + new string('x', ApiEndpointSupport.MaxIngestionBodyBytes) + "\"}");

        AssertProblem(wrongType, HttpStatusCode.UnsupportedMediaType);
        AssertProblem(wrongName, HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        AssertProblem(foreignRead, HttpStatusCode.NotFound);
        AssertProblem(tooLarge, HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Operation_status_is_owner_scoped_and_never_exposes_failure_messages()
    {
        var owner = await CreateAuthenticatedClientAsync("owner");
        var foreign = await CreateAuthenticatedClientAsync("foreign");
        var collection = await CreateCollectionAsync(owner, "Reports");
        var accepted = await SendAsync(owner.Token, HttpMethod.Post, $"/api/v1/collections/{collection.Id}/ingestions:txt", "application/json", "{\"file_name\":\"report.txt\",\"content\":\"text\"}");
        var operationId = (await accepted.Content.ReadFromJsonAsync<IngestionResponse>())!.OperationId;
        await SetOperationFailureAsync(operationId);

        var ownerRead = await SendAsync(owner.Token, HttpMethod.Get, $"/api/v1/collections/{collection.Id}/operations/{operationId}");
        var foreignRead = await SendAsync(foreign.Token, HttpMethod.Get, $"/api/v1/collections/{collection.Id}/operations/{operationId}");
        var body = await ownerRead.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, ownerRead.StatusCode);
        Assert.Contains("\"failure_stage\":\"parse\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("provider secret", body, StringComparison.Ordinal);
        AssertProblem(foreignRead, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Retrieval_rejects_bounds_missing_or_foreign_collections_and_incompatible_profiles()
    {
        var owner = await CreateAuthenticatedClientAsync("owner");
        var foreign = await CreateAuthenticatedClientAsync("foreign");
        var owned = await CreateCollectionAsync(owner, "Reports");
        var foreignCollection = await CreateCollectionAsync(foreign, "Foreign reports");
        var incompatible = await AddIncompatibleCollectionAsync(owner.ServiceClientId);

        var tooMany = await SendAsync(owner.Token, HttpMethod.Post, "/api/v1/retrieval:search", "application/json", "{\"collection_ids\":[" + string.Join(',', Enumerable.Repeat($"\"{owned.Id}\"", 11)) + "],\"query\":\"q\",\"top_k\":1}");
        var foreignSearch = await SendAsync(owner.Token, HttpMethod.Post, "/api/v1/retrieval:search", "application/json", $"{{\"collection_ids\":[\"{foreignCollection.Id}\"],\"query\":\"q\",\"top_k\":1}}");
        var incompatibleSearch = await SendAsync(owner.Token, HttpMethod.Post, "/api/v1/retrieval:search", "application/json", $"{{\"collection_ids\":[\"{owned.Id}\",\"{incompatible.Id}\"],\"query\":\"q\",\"top_k\":1}}");

        AssertProblem(tooMany, HttpStatusCode.BadRequest);
        AssertProblem(foreignSearch, HttpStatusCode.NotFound);
        AssertProblem(incompatibleSearch, HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Retrieval_search_success_body_pins_the_match_key_set()
    {
        var owner = await CreateAuthenticatedClientAsync("retrieval");
        var collection = await CreateCollectionAsync(owner, "Retrieval");
        var seededChunkId = await SeedSearchableChunkAsync(collection.Id);

        var response = await SendAsync(owner.Token, HttpMethod.Post, "/api/v1/retrieval:search", "application/json", $"{{\"collection_ids\":[\"{collection.Id}\"],\"query\":\"q\",\"top_k\":5}}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        var match = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal(
            Keys("collectionId", "documentId", "documentVersionId", "chunkId", "chunkOrdinal", "chunkText", "cosineDistance"),
            PropertyNames(match));
        Assert.Equal(seededChunkId, match.GetProperty("chunkId").GetGuid());
    }

    [Fact]
    public async Task Txt_ingestion_success_body_pins_the_response_key_set()
    {
        var owner = await CreateAuthenticatedClientAsync("ingestion");
        var collection = await CreateCollectionAsync(owner, "Ingestion");

        var accepted = await SendAsync(owner.Token, HttpMethod.Post, $"/api/v1/collections/{collection.Id}/ingestions:txt", "application/json", "{\"file_name\":\"response.txt\",\"content\":\"text\"}");

        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        using var document = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        var body = document.RootElement;
        Assert.Equal(Keys("document_id", "document_version_id", "operation_id"), PropertyNames(body));
        Assert.NotEqual(Guid.Empty, body.GetProperty("document_id").GetGuid());
        Assert.NotEqual(Guid.Empty, body.GetProperty("document_version_id").GetGuid());
        Assert.NotEqual(Guid.Empty, body.GetProperty("operation_id").GetGuid());
    }

    [Fact]
    public async Task Historical_scope_exchange_is_rejected_by_the_default_feature_gate()
    {
        var historical = await CreateHistoricalClientAsync(_factory);

        var response = await _client.PostAsJsonAsync("/api/v1/auth/token", new
        {
            keyId = historical.KeyId,
            secret = historical.Secret,
            scope = $"{HistoricalScopes.UploadsWrite} {HistoricalScopes.OperationsRead}",
        });

        AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.Contains("invalid_scope", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Historical_scoped_token_is_issued_with_the_legacy_grant_and_denied_on_real_time_routes()
    {
        using var enabledFactory = new ProtectedApiFactory(fixture.ConnectionString, _contentRoot, enableHistoricalIngestion: true);
        using var enabledClient = enabledFactory.CreateClient();
        var historical = await CreateHistoricalClientAsync(enabledFactory);

        var exchange = await enabledClient.PostAsJsonAsync("/api/v1/auth/token", new
        {
            keyId = historical.KeyId,
            secret = historical.Secret,
            scope = $"{HistoricalScopes.UploadsWrite} {HistoricalScopes.OperationsRead}",
        });
        var exchangeBody = await exchange.Content.ReadAsStringAsync();
        var gate = enabledFactory.Services.GetRequiredService<HistoricalIngestionOptions>().Enabled;
        var raw = enabledFactory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["HistoricalIngestion:Enabled"];
        Assert.True(exchange.StatusCode == HttpStatusCode.OK, $"status={exchange.StatusCode} gate={gate} raw='{raw}' body={exchangeBody}");
        var scopedToken = (await exchange.Content.ReadFromJsonAsync<TokenResponse>())!;
        Assert.Equal("historical:operations.read historical:uploads.write", scopedToken.Scope);

        var legacyExchange = await enabledClient.PostAsJsonAsync("/api/v1/auth/token", new { keyId = historical.KeyId, secret = historical.Secret });
        var legacyToken = (await legacyExchange.Content.ReadFromJsonAsync<TokenResponse>())!;
        Assert.Null(legacyToken.Scope);

        var legacyCreate = await SendWithClientAsync(enabledClient, legacyToken.AccessToken, HttpMethod.Post, "/api/v1/collections", "application/json", "{\"name\":\"legacy-compatible-collection\"}");
        Assert.Equal(HttpStatusCode.Created, legacyCreate.StatusCode);

        var denied = await SendWithClientAsync(enabledClient, scopedToken.AccessToken, HttpMethod.Post, "/api/v1/collections", "application/json", "{\"name\":\"should-not-exist\"}");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Historical_scoped_token_body_pins_the_token_key_set()
    {
        using var enabledFactory = new ProtectedApiFactory(fixture.ConnectionString, _contentRoot, enableHistoricalIngestion: true);
        using var enabledClient = enabledFactory.CreateClient();
        var historical = await CreateHistoricalClientAsync(enabledFactory);

        var exchange = await enabledClient.PostAsJsonAsync("/api/v1/auth/token", new
        {
            keyId = historical.KeyId,
            secret = historical.Secret,
            scope = $"{HistoricalScopes.UploadsWrite} {HistoricalScopes.OperationsRead}",
        });

        Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
        using var document = JsonDocument.Parse(await exchange.Content.ReadAsStringAsync());
        var body = document.RootElement;
        Assert.Equal(Keys("access_token", "token_type", "expires_in", "scope"), PropertyNames(body));
        Assert.Equal("Bearer", body.GetProperty("token_type").GetString());
        Assert.Contains(HistoricalScopes.UploadsWrite, body.GetProperty("scope").GetString(), StringComparison.Ordinal);
    }

    private async Task<HistoricalClient> CreateHistoricalClientAsync(ProtectedApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var issued = await scope.ServiceProvider.GetRequiredService<CredentialOperator>().IssueAsync($"historical-{Guid.NewGuid():N}", null);

        var options = new DbContextOptionsBuilder<IngestionDbContext>()
            .UseNpgsql(fixture.ConnectionString, providerOptions => providerOptions.UseVector())
            .Options;
        await using var context = new IngestionDbContext(options);
        var legacy = new Collection(Guid.NewGuid(), issued.ServiceClientId, "legacy", DateTimeOffset.UtcNow, EmbeddingProfile.Default);
        context.Collections.Add(legacy);
        context.ServiceClientGrants.Add(new ServiceClientGrantEntity
        {
            Id = Guid.NewGuid(),
            ServiceClientId = issued.ServiceClientId,
            Scopes = $"{HistoricalScopes.UploadsWrite} {HistoricalScopes.OperationsRead}",
            CollectionId = legacy.Id,
            Version = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();
        return new HistoricalClient(issued.KeyId, issued.Secret, issued.ServiceClientId, legacy.Id);
    }

    private async Task<ApiClient> CreateAuthenticatedClientAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        var issued = await scope.ServiceProvider.GetRequiredService<CredentialOperator>().IssueAsync($"{name}-{Guid.NewGuid():N}", null);
        var exchange = await _client.PostAsJsonAsync("/api/v1/auth/token", new { keyId = issued.KeyId, secret = issued.Secret });
        var token = (await exchange.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;
        return new ApiClient(issued.ServiceClientId, token);
    }

    private async Task<CollectionResponse> CreateCollectionAsync(ApiClient client, string name)
    {
        var response = await SendAsync(client.Token, HttpMethod.Post, "/api/v1/collections", "application/json", $"{{\"name\":\"{name}\"}}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        return (await response.Content.ReadFromJsonAsync<CollectionResponse>())!;
    }

    private async Task<CollectionResponse> AddIncompatibleCollectionAsync(Guid serviceClientId)
    {
        var options = new DbContextOptionsBuilder<IngestionDbContext>()
            .UseNpgsql(fixture.ConnectionString, options => options.UseVector())
            .Options;
        await using var context = new IngestionDbContext(options);
        var collection = new Collection(Guid.NewGuid(), serviceClientId, "Other profile", DateTimeOffset.UtcNow, new EmbeddingProfile("llama.cpp", "other:1", "1", 3));
        context.Collections.Add(collection);
        await context.SaveChangesAsync();
        return new CollectionResponse(collection.Id, collection.Name);
    }

    private async Task<Guid> SeedSearchableChunkAsync(Guid collectionId)
    {
        var options = new DbContextOptionsBuilder<IngestionDbContext>()
            .UseNpgsql(fixture.ConnectionString, options => options.UseVector())
            .Options;
        await using var context = new IngestionDbContext(options);
        var collection = await context.Collections.AsNoTracking().SingleAsync(item => item.Id == collectionId);
        var profile = collection.GetEmbeddingProfile();
        var now = DateTimeOffset.UtcNow;
        var document = new Document(Guid.NewGuid(), collectionId, "source://retrieval", now);
        var versionId = Guid.NewGuid();
        document.AddVersion(versionId, "retrieval.txt", ContentHash.FromBytes("retrieval"u8), ContentReference.ForVersion(versionId), now);
        var chunk = new Chunk(Guid.NewGuid(), versionId, 1, "retrieval text");
        context.Documents.Add(document);
        context.Chunks.Add(chunk);
        context.ChunkEmbeddings.Add(new ChunkEmbedding(Guid.NewGuid(), collectionId, chunk.Id, UnitVector(profile.Dimensions)));
        await context.SaveChangesAsync();
        return chunk.Id;
    }

    private async Task SetOperationFailureAsync(Guid operationId)
    {
        var options = new DbContextOptionsBuilder<IngestionDbContext>()
            .UseNpgsql(fixture.ConnectionString, options => options.UseVector())
            .Options;
        await using var context = new IngestionDbContext(options);
        await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE operations SET \"Status\" = 'Failed', \"FailureStage\" = 'parse', \"FailureMessage\" = 'provider secret' WHERE \"Id\" = {operationId};");
    }

    private async Task<HttpResponseMessage> SendAsync(string token, HttpMethod method, string path, string? mediaType = null, string? body = null) =>
        await SendWithClientAsync(_client, token, method, path, mediaType, body);

    private static async Task<HttpResponseMessage> SendWithClientAsync(HttpClient client, string token, HttpMethod method, string path, string? mediaType, string? body)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, mediaType);
        }

        return await client.SendAsync(request);
    }

    private static void AssertProblem(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    private static string[] PropertyNames(JsonElement element) =>
        [.. element.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal)];

    private static string[] Keys(params string[] names) =>
        [.. names.OrderBy(name => name, StringComparer.Ordinal)];

    private static float[] UnitVector(int dimensions)
    {
        var vector = new float[dimensions];
        vector[0] = 1f;
        return vector;
    }

    private sealed record ApiClient(Guid ServiceClientId, string Token);
    private sealed record HistoricalClient(string KeyId, string Secret, Guid ServiceClientId, Guid LegacyCollectionId);
    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string AccessToken, [property: JsonPropertyName("scope")] string? Scope = null);
    private sealed record CollectionResponse(Guid Id, string Name);
    private sealed record IngestionResponse([property: JsonPropertyName("operation_id")] Guid OperationId);
}

public sealed class ProtectedApiFactory(string connectionString, string contentRoot, bool enableHistoricalIngestion = false) : WebApplicationFactory<global::Program>
{
    private readonly RSA _rsa = RSA.Create(2048);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
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
            ["Jwt:CurrentSigningKey:PrivateKeyPem"] = _rsa.ExportRSAPrivateKeyPem(),
            ["Jwt:ValidationKeys:0:KeyId"] = "integration-key",
            ["Jwt:ValidationKeys:0:PublicKeyPem"] = _rsa.ExportRSAPublicKeyPem(),
            ["HistoricalIngestion:Enabled"] = enableHistoricalIngestion.ToString(),
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IEmbeddingProvider>();
            services.AddSingleton<IEmbeddingProvider, SearchOnlyEmbeddingProvider>();
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
            _rsa.Dispose();
        }
    }
}

// Retrieval search needs an embedding provider that does not reach the real llama.cpp server. The stub returns
// a unit vector so seeded chunk embeddings with the same vector score a zero cosine distance.
internal sealed class SearchOnlyEmbeddingProvider : IEmbeddingProvider
{
    public Task<EmbeddingResponse> EmbedAsync(EmbeddingProfile profile, IReadOnlyList<string> inputs, CancellationToken cancellationToken)
    {
        var vectors = new List<float[]>(inputs.Count);
        foreach (var _ in inputs)
        {
            var vector = new float[profile.Dimensions];
            vector[0] = 1f;
            vectors.Add(vector);
        }

        return Task.FromResult(new EmbeddingResponse(vectors));
    }
}
