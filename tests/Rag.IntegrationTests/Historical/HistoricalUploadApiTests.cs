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
using Rag.Application.Auth;
using Rag.Domain;
using Rag.Infrastructure;

namespace Rag.IntegrationTests.Historical;

[Collection(PostgreSqlCollection.Name)]
public sealed class HistoricalUploadApiTests(PostgreSqlFixture fixture) : IAsyncLifetime
{
    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), $"rag-historical-tests-{Guid.NewGuid():N}");
    private HistoricalApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new HistoricalApiFactory(fixture.ConnectionString, _contentRoot);
        _client = _factory.CreateClient();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        await context.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE historical_provenance, historical_uploads, operations, chunks, document_versions, documents, collections, service_client_grants, client_credentials, service_clients CASCADE;");
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

    // ------------------------------------------------------------------
    // RED block 1 — reservation / stream / commit / status / limits
    // ------------------------------------------------------------------

    [Fact]
    public async Task Reserve_is_idempotent_and_returns_limits_correlation_and_state()
    {
        var historical = await CreateHistoricalClientAsync();
        var token = await ExchangeScopedTokenAsync(historical);
        var request = Reserve("source-a", "hello world");

        var first = await ReserveRawAsync(token, historical.CollectionId, request);
        var second = await ReserveRawAsync(token, historical.CollectionId, request);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstBody = (await first.Content.ReadFromJsonAsync<ReserveResponse>())!;
        Assert.NotEqual(Guid.Empty, firstBody.UploadId);
        Assert.Equal("reserved", firstBody.State);
        Assert.True(firstBody.Created);
        Assert.False(string.IsNullOrWhiteSpace(firstBody.CorrelationId));
        Assert.NotNull(firstBody.AcceptedLimits);
        Assert.True(firstBody.AcceptedLimits!.MaxNormalizedTextBytes > 0);
        Assert.True(firstBody.AcceptedLimits.PerClientPendingQuota > 0);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = (await second.Content.ReadFromJsonAsync<ReserveResponse>())!;
        Assert.Equal(firstBody.UploadId, secondBody.UploadId);
        Assert.False(secondBody.Created);
    }

    [Fact]
    public async Task Reserve_conflicts_when_idempotency_key_is_reused_with_different_fingerprint()
    {
        var historical = await CreateHistoricalClientAsync();
        var token = await ExchangeScopedTokenAsync(historical);
        var request = Reserve("source-a", "content", candidateId: Guid.NewGuid());

        var first = await ReserveRawAsync(token, historical.CollectionId, request);
        var conflict = await ReserveRawAsync(
            token,
            historical.CollectionId,
            request with { DeclaredBytes = request.DeclaredBytes + 1 });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task Put_content_verifies_declared_length_and_digest_and_is_idempotent()
    {
        var historical = await CreateHistoricalClientAsync();
        var token = await ExchangeScopedTokenAsync(historical);
        var content = "normalized text for upload";
        var hash = Sha256Hex(content);
        var reserved = await ReserveUploadAsync(token, historical.CollectionId, "source-put", content, idempotencyKey: "put-1");

        var published = await PutContentAsync(token, reserved.UploadId, content);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        using var publishedJson = JsonDocument.Parse(await published.Content.ReadAsStringAsync());
        Assert.Equal(
            Keys("upload_id", "state", "declared_bytes", "observed_bytes", "normalized_text_sha256"),
            PropertyNames(publishedJson.RootElement));
        var body = (await published.Content.ReadFromJsonAsync<PublishedResponse>())!;
        Assert.Equal(reserved.UploadId, body.UploadId);
        Assert.Equal("published", body.State);
        Assert.Equal(Encoding.UTF8.GetByteCount(content), body.DeclaredBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(content), body.ObservedBytes);
        Assert.Equal(hash, body.NormalizedTextSha256);

        var again = await PutContentAsync(token, reserved.UploadId, content);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(body.UploadId, (await again.Content.ReadFromJsonAsync<PublishedResponse>())!.UploadId);

        var wrongLength = await PutContentAsync(token, reserved.UploadId, "short");
        Assert.Equal(HttpStatusCode.BadRequest, wrongLength.StatusCode);

        var wrongHashUpload = await ReserveUploadAsync(token, historical.CollectionId, "source-wrong-hash", "correct content", idempotencyKey: "wrong-hash");
        var wrongHash = await PutContentAsync(token, wrongHashUpload.UploadId, "different content");
        Assert.Equal(HttpStatusCode.BadRequest, wrongHash.StatusCode);
    }

    [Fact]
    public async Task Commit_is_idempotent_and_persists_document_version_operation_and_provenance()
    {
        var historical = await CreateHistoricalClientAsync();
        var token = await ExchangeScopedTokenAsync(historical);
        var content = "committed content";
        var reserved = await ReserveUploadAsync(token, historical.CollectionId, "source-commit", content, idempotencyKey: "commit-1");
        await PutContentAsync(token, reserved.UploadId, content);

        var committed = await CommitAsync(token, reserved.UploadId);
        Assert.Equal(HttpStatusCode.OK, committed.StatusCode);
        var commitBody = (await committed.Content.ReadFromJsonAsync<CommitResponse>())!;
        Assert.NotEqual(Guid.Empty, commitBody.DocumentId);
        Assert.NotEqual(Guid.Empty, commitBody.DocumentVersionId);
        Assert.NotEqual(Guid.Empty, commitBody.OperationId);
        Assert.Equal("committed", commitBody.State);

        var again = await CommitAsync(token, reserved.UploadId);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var againBody = (await again.Content.ReadFromJsonAsync<CommitResponse>())!;
        Assert.Equal(commitBody.DocumentId, againBody.DocumentId);
        Assert.Equal(commitBody.DocumentVersionId, againBody.DocumentVersionId);
        Assert.Equal(commitBody.OperationId, againBody.OperationId);

        await using var context = CreateContext();
        var provenance = await context.HistoricalProvenance.SingleAsync(item => item.DocumentVersionId == commitBody.DocumentVersionId);
        Assert.Equal("historical_windows_manifest", provenance.SourceKind);
        Assert.Equal("source-commit", provenance.SourceDocumentKey);
        Assert.Equal(Sha256Hex(content), provenance.NormalizedTextSha256);
        Assert.Equal(commitBody.OperationId, provenance.RemoteOperationId);
    }

    [Fact]
    public async Task Get_upload_reconciles_outcome_without_repeating_content_mutation()
    {
        var historical = await CreateHistoricalClientAsync();
        var token = await ExchangeScopedTokenAsync(historical);
        var content = "reconcile me";
        var reserved = await ReserveUploadAsync(token, historical.CollectionId, "source-reconcile", content, idempotencyKey: "reconcile-1");
        await PutContentAsync(token, reserved.UploadId, content);
        await CommitAsync(token, reserved.UploadId);

        var before = await CountDocumentsAsync();
        var get = await GetUploadAsync(token, reserved.UploadId);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var body = (await get.Content.ReadFromJsonAsync<GetUploadResponse>())!;
        Assert.Equal("committed", body.State);
        Assert.NotNull(body.DocumentId);
        Assert.NotNull(body.DocumentVersionId);
        Assert.NotNull(body.OperationId);

        var after = await CountDocumentsAsync();
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Get_operation_returns_safe_telemetry_without_internal_addresses()
    {
        var historical = await CreateHistoricalClientAsync();
        var token = await ExchangeScopedTokenAsync(historical);
        var content = "telemetry content";
        var reserved = await ReserveUploadAsync(token, historical.CollectionId, "source-telemetry", content, idempotencyKey: "telemetry-1");
        await PutContentAsync(token, reserved.UploadId, content);
        var committed = (await (await CommitAsync(token, reserved.UploadId)).Content.ReadFromJsonAsync<CommitResponse>())!;

        await SetOperationSucceededAsync(committed.OperationId);
        var telemetry = _factory.Services.GetRequiredService<HistoricalTelemetry>();
        telemetry.Begin(committed.OperationId, OperationWorkloadClass.Historical);
        telemetry.RecordQueueWait(committed.OperationId, TimeSpan.FromSeconds(2));
        telemetry.RecordChunking(committed.OperationId, 2, TimeSpan.FromMilliseconds(10));
        telemetry.RecordEmbedding(committed.OperationId, 1, TimeSpan.FromMilliseconds(20));
        telemetry.RecordIndexing(committed.OperationId, TimeSpan.FromMilliseconds(30));
        telemetry.Complete(committed.OperationId, OperationTerminalState.Succeeded, DateTimeOffset.UtcNow);

        var response = await GetOperationAsync(token, historical.CollectionId, committed.OperationId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<OperationStatusResponse>())!;
        Assert.Equal("succeeded", body.Status);
        Assert.Equal(2, body.ChunkCount);
        Assert.Equal(1, body.EmbeddingCalls);
        Assert.Equal("succeeded", body.TerminalState);
        Assert.True(body.QueueWait >= TimeSpan.FromSeconds(2));
        Assert.True(body.ChunkingDuration >= TimeSpan.FromMilliseconds(10));
        Assert.True(body.EmbeddingDuration >= TimeSpan.FromMilliseconds(20));
        Assert.True(body.IndexingDuration >= TimeSpan.FromMilliseconds(30));
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("postgres", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("llama", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Normalized_text_size_cap_is_enforced_while_streaming()
    {
        using var factory = new HistoricalApiFactory(fixture.ConnectionString, _contentRoot, new Dictionary<string, string?>
        {
            ["HistoricalIngestion:MaxNormalizedTextBytes"] = "16",
        });
        using var client = factory.CreateClient();
        var historical = await CreateHistoricalClientAsync(factory, "small-cap");
        var token = await ExchangeScopedTokenAsync(historical, client);
        var content = "this is longer than sixteen bytes";
        var reserved = await ReserveUploadAsync(client, token, historical.CollectionId, "source-cap", content, idempotencyKey: "cap-1");

        var response = await PutContentAsync(client, token, reserved.UploadId, content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Per_client_pending_quota_is_enforced_with_retry_after()
    {
        using var factory = new HistoricalApiFactory(fixture.ConnectionString, _contentRoot, new Dictionary<string, string?>
        {
            ["HistoricalIngestion:PerClientPendingQuota"] = "2",
        });
        using var client = factory.CreateClient();
        var historical = await CreateHistoricalClientAsync(factory, "quota-client");
        var token = await ExchangeScopedTokenAsync(historical, client);

        var first = await _clientReserve(client, token, historical.CollectionId, "q-1", "content-1", "k-1");
        var second = await _clientReserve(client, token, historical.CollectionId, "q-2", "content-2", "k-2");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var third = await _clientReserve(client, token, historical.CollectionId, "q-3", "content-3", "k-3");
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.True(third.Headers.TryGetValues("Retry-After", out var retryAfter));
        Assert.Equal("30", Assert.Single(retryAfter!));
        Assert.Equal("application/problem+json", third.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await third.Content.ReadAsStringAsync());
        Assert.Equal("Too many requests", problem.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Total_storage_watermark_is_enforced_before_publish()
    {
        using var factory = new HistoricalApiFactory(fixture.ConnectionString, _contentRoot, new Dictionary<string, string?>
        {
            ["HistoricalIngestion:TotalStorageWatermarkBytes"] = "16",
        });
        using var client = factory.CreateClient();
        var historical = await CreateHistoricalClientAsync(factory, "watermark-client");
        var token = await ExchangeScopedTokenAsync(historical, client);
        var content = "larger than the watermark";
        var reserved = await ReserveUploadAsync(client, token, historical.CollectionId, "source-watermark", content, idempotencyKey: "watermark-1");

        var response = await PutContentAsync(client, token, reserved.UploadId, content);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Retry-After", out var retryAfter));
        Assert.Equal("30", Assert.Single(retryAfter!));
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Too many requests", problem.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Abandoned_upload_expiry_releases_pending_quota()
    {
        using var factory = new HistoricalApiFactory(fixture.ConnectionString, _contentRoot, new Dictionary<string, string?>
        {
            ["HistoricalIngestion:PerClientPendingQuota"] = "1",
            ["HistoricalIngestion:AbandonedUploadExpiry"] = "00:00:01",
        });
        using var client = factory.CreateClient();
        var historical = await CreateHistoricalClientAsync(factory, "abandoned-client");
        var token = await ExchangeScopedTokenAsync(historical, client);
        var reserved = await _clientReserve(client, token, historical.CollectionId, "source-abandoned", "content-abandoned", "abandoned-1");
        Assert.Equal(HttpStatusCode.Created, reserved.StatusCode);
        await BackdateUploadAsync((await reserved.Content.ReadFromJsonAsync<ReserveResponse>())!.UploadId);

        await Task.Delay(TimeSpan.FromSeconds(2));

        var next = await _clientReserve(client, token, historical.CollectionId, "source-next", "content-next", "k-next");
        Assert.Equal(HttpStatusCode.Created, next.StatusCode);
    }

    // ------------------------------------------------------------------
    // RED block 2 — version / provenance / fingerprint semantics
    // ------------------------------------------------------------------

    [Fact]
    public async Task Changed_normalized_content_for_same_source_creates_a_new_version()
    {
        var historical = await CreateHistoricalClientAsync();
        var token = await ExchangeScopedTokenAsync(historical);

        var first = await ReservePutCommitAsync(token, historical.CollectionId, "source-same", "first content", "same-1");
        var second = await ReservePutCommitAsync(token, historical.CollectionId, "source-same", "second different content", "same-2");

        Assert.Equal(first.DocumentId, second.DocumentId);
        Assert.NotEqual(first.DocumentVersionId, second.DocumentVersionId);

        await using var context = CreateContext();
        var versions = await context.DocumentVersions
            .Where(version => version.DocumentId == first.DocumentId)
            .OrderBy(version => version.Number)
            .ToListAsync();
        Assert.Equal(2, versions.Count);
        Assert.Equal(2, versions[1].Number);
    }

    [Fact]
    public async Task Equal_content_from_two_source_keys_remains_two_documents()
    {
        var historical = await CreateHistoricalClientAsync();
        var token = await ExchangeScopedTokenAsync(historical);
        var content = "shared content";

        var first = await ReservePutCommitAsync(token, historical.CollectionId, "source-one", content, "one-1");
        var second = await ReservePutCommitAsync(token, historical.CollectionId, "source-two", content, "two-1");

        Assert.NotEqual(first.DocumentId, second.DocumentId);
        Assert.NotEqual(first.DocumentVersionId, second.DocumentVersionId);
    }

    [Fact]
    public async Task Commit_requires_published_content_verified_before_commit()
    {
        var historical = await CreateHistoricalClientAsync();
        var token = await ExchangeScopedTokenAsync(historical);
        var reserved = await ReserveUploadAsync(token, historical.CollectionId, "source-not-published", "unpublished", idempotencyKey: "unpublished-1");

        var response = await CommitAsync(token, reserved.UploadId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ------------------------------------------------------------------
    // RED block 3 — workload classification / fairness / admission
    // ------------------------------------------------------------------

    [Fact]
    public async Task Historical_operations_are_workload_classified_separately_from_real_time()
    {
        var historical = await CreateHistoricalClientAsync();
        var token = await ExchangeScopedTokenAsync(historical);
        var committed = await ReservePutCommitAsync(token, historical.CollectionId, "source-class", "classified content", "class-1");

        await using var context = CreateContext();
        var operation = await context.Operations.SingleAsync(item => item.Id == committed.OperationId);
        Assert.Equal(OperationWorkloadClass.Historical, operation.WorkloadClass);

        var classifier = _factory.Services.GetRequiredService<IOperationWorkloadClassifier>();
        Assert.Equal(OperationWorkloadClass.Historical, classifier.Classify(operation));
    }

    [Fact]
    public async Task Claim_next_prefers_real_time_operations_even_when_historical_are_older()
    {
        var historical = await CreateHistoricalClientAsync();
        var collectionId = historical.CollectionId;
        var now = DateTimeOffset.UtcNow;

        await using (var context = CreateContext())
        {
            var historicalVersion = CreateVersion(context, collectionId, "historical.txt", "historical", now.AddMinutes(-10));
            var realTimeVersion = CreateVersion(context, collectionId, "realtime.txt", "realtime", now.AddMinutes(-5));
            var historicalOperation = Operation.CreatePending(historicalVersion.Id, now.AddMinutes(-10));
            var realTimeOperation = Operation.CreatePending(realTimeVersion.Id, now.AddMinutes(-5));
            context.Operations.AddRange(historicalOperation, realTimeOperation);
            await context.SaveChangesAsync();
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE operations SET \"WorkloadClass\" = 'Historical' WHERE \"Id\" = {historicalOperation.Id}");
        }

        var claims = _factory.Services.GetRequiredService<IOperationClaimRepository>();
        var claimed = await claims.ClaimNextAsync("fairness-probe", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.NotNull(claimed);
        Assert.Equal(OperationWorkloadClass.RealTime, claimed!.WorkloadClass);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task<ReserveResponse> ReserveUploadAsync(
        string token,
        Guid collectionId,
        string sourceKey,
        string content,
        string idempotencyKey) =>
        await ReserveUploadAsync(_client, token, collectionId, sourceKey, content, idempotencyKey);

    private static async Task<ReserveResponse> ReserveUploadAsync(
        HttpClient client,
        string token,
        Guid collectionId,
        string sourceKey,
        string content,
        string idempotencyKey)
    {
        var response = await _clientReserve(client, token, collectionId, sourceKey, content, idempotencyKey);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ReserveResponse>())!;
    }

    private static Task<HttpResponseMessage> _clientReserve(
        HttpClient client,
        string token,
        Guid collectionId,
        string sourceKey,
        string content,
        string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/historical/collections/{collectionId}/uploads")
        {
            Content = JsonContent.Create(Reserve(sourceKey, content, idempotencyKey: idempotencyKey)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private async Task<CommitResponse> ReservePutCommitAsync(
        string token,
        Guid collectionId,
        string sourceKey,
        string content,
        string idempotencyKey)
    {
        var reserved = await ReserveUploadAsync(token, collectionId, sourceKey, content, idempotencyKey);
        var published = await PutContentAsync(token, reserved.UploadId, content);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        var committed = await CommitAsync(token, reserved.UploadId);
        Assert.Equal(HttpStatusCode.OK, committed.StatusCode);
        return (await committed.Content.ReadFromJsonAsync<CommitResponse>())!;
    }

    private Task<HttpResponseMessage> PutContentAsync(string token, Guid uploadId, string content) =>
        PutContentAsync(_client, token, uploadId, content);

    private static Task<HttpResponseMessage> PutContentAsync(HttpClient client, string token, Guid uploadId, string content)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/historical/uploads/{uploadId}/content")
        {
            Content = new StringContent(content, Encoding.UTF8, "text/plain"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private async Task<HistoricalClient> CreateHistoricalClientAsync() => await CreateHistoricalClientAsync(_factory, $"historical-{Guid.NewGuid():N}");

    private async Task<HistoricalClient> CreateHistoricalClientAsync(HistoricalApiFactory factory, string name)
    {
        using var scope = factory.Services.CreateScope();
        var issued = await scope.ServiceProvider.GetRequiredService<CredentialOperator>().IssueAsync($"{name}-{Guid.NewGuid():N}", null);

        await using var context = CreateContext();
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

    private async Task<string> ExchangeScopedTokenAsync(HistoricalClient historical) =>
        await ExchangeScopedTokenAsync(historical, _client);

    private static async Task<string> ExchangeScopedTokenAsync(HistoricalClient historical, HttpClient client)
    {
        var exchange = await client.PostAsJsonAsync("/api/v1/auth/token", new
        {
            keyId = historical.KeyId,
            secret = historical.Secret,
            scope = $"{HistoricalScopes.UploadsWrite} {HistoricalScopes.OperationsRead}",
        });
        exchange.EnsureSuccessStatusCode();
        return (await exchange.Content.ReadFromJsonAsync<TokenResponse>())!.AccessToken;
    }

    private IngestionDbContext CreateContext() => new(new DbContextOptionsBuilder<IngestionDbContext>()
        .UseNpgsql(fixture.ConnectionString, options => options.UseVector())
        .Options);

    private async Task BackdateUploadAsync(Guid uploadId)
    {
        await using var context = CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE historical_uploads SET \"CreatedAt\" = clock_timestamp() - interval '1 hour' WHERE \"Id\" = {uploadId}");
    }

    private async Task SetOperationSucceededAsync(Guid operationId)
    {
        await using var context = CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE operations SET \"Status\" = 'Succeeded', \"CompletedAt\" = clock_timestamp() WHERE \"Id\" = {operationId}");
    }

    private async Task<int> CountDocumentsAsync()
    {
        await using var context = CreateContext();
        return await context.Documents.CountAsync();
    }

    private DocumentVersion CreateVersion(IngestionDbContext context, Guid collectionId, string fileName, string content, DateTimeOffset createdAt)
    {
        var document = new Document(Guid.NewGuid(), collectionId, null, createdAt);
        var versionId = Guid.NewGuid();
        var version = document.AddVersion(versionId, fileName, ContentHash.FromBytes(Encoding.UTF8.GetBytes(content)), ContentReference.ForVersion(versionId), createdAt);
        context.Documents.Add(document);
        return version;
    }

    private static string Sha256Hex(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static string[] PropertyNames(JsonElement element) =>
        [.. element.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal)];

    private static string[] Keys(params string[] names) =>
        [.. names.OrderBy(name => name, StringComparer.Ordinal)];

    private Task<HttpResponseMessage> CommitAsync(string token, Guid uploadId) =>
        SendWithTokenAsync(_client, token, HttpMethod.Post, $"/api/v1/historical/uploads/{uploadId}:commit");

    private Task<HttpResponseMessage> GetUploadAsync(string token, Guid uploadId) =>
        SendWithTokenAsync(_client, token, HttpMethod.Get, $"/api/v1/historical/uploads/{uploadId}");

    private Task<HttpResponseMessage> GetOperationAsync(string token, Guid collectionId, Guid operationId) =>
        SendWithTokenAsync(_client, token, HttpMethod.Get, $"/api/v1/historical/collections/{collectionId}/operations/{operationId}");

    private Task<HttpResponseMessage> ReserveRawAsync(string token, Guid collectionId, ReserveRequest request) =>
        SendWithTokenAsync(_client, token, HttpMethod.Post, $"/api/v1/historical/collections/{collectionId}/uploads", JsonContent.Create(request));

    private static Task<HttpResponseMessage> SendWithTokenAsync(
        HttpClient client,
        string token,
        HttpMethod method,
        string path,
        HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private static ReserveRequest Reserve(string sourceKey, string content, Guid? candidateId = null, string? idempotencyKey = null) =>
        new(
            sourceKey,
            candidateId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Sha256Hex(content),
            Encoding.UTF8.GetByteCount(content),
            idempotencyKey ?? $"idem-{Guid.NewGuid():N}",
            "document.txt",
            "txt",
            "root-alias");

    // ------------------------------------------------------------------
    // Records
    // ------------------------------------------------------------------

    private sealed record HistoricalClient(string KeyId, string Secret, Guid ServiceClientId, Guid CollectionId);

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string AccessToken);

    private sealed record ReserveRequest(
        [property: JsonPropertyName("source_document_key")] string SourceDocumentKey,
        [property: JsonPropertyName("candidate_id")] Guid? CandidateId,
        [property: JsonPropertyName("manifest_id")] Guid? ManifestId,
        [property: JsonPropertyName("run_id")] Guid? RunId,
        [property: JsonPropertyName("normalized_text_sha256")] string NormalizedTextSha256,
        [property: JsonPropertyName("declared_bytes")] long DeclaredBytes,
        [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
        [property: JsonPropertyName("display_name")] string? DisplayName,
        [property: JsonPropertyName("format")] string? Format,
        [property: JsonPropertyName("source_root_alias")] string? SourceRootAlias);

    private sealed record ReserveResponse(
        [property: JsonPropertyName("upload_id")] Guid UploadId,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("correlation_id")] string? CorrelationId,
        [property: JsonPropertyName("created")] bool Created,
        [property: JsonPropertyName("accepted_limits")] UploadLimits? AcceptedLimits);

    private sealed record UploadLimits(
        [property: JsonPropertyName("max_normalized_text_bytes")] int MaxNormalizedTextBytes,
        [property: JsonPropertyName("per_client_pending_quota")] int PerClientPendingQuota,
        [property: JsonPropertyName("total_storage_watermark_bytes")] long TotalStorageWatermarkBytes);

    private sealed record PublishedResponse(
        [property: JsonPropertyName("upload_id")] Guid UploadId,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("declared_bytes")] long DeclaredBytes,
        [property: JsonPropertyName("observed_bytes")] long ObservedBytes,
        [property: JsonPropertyName("normalized_text_sha256")] string NormalizedTextSha256);

    private sealed record CommitResponse(
        [property: JsonPropertyName("upload_id")] Guid UploadId,
        [property: JsonPropertyName("document_id")] Guid DocumentId,
        [property: JsonPropertyName("document_version_id")] Guid DocumentVersionId,
        [property: JsonPropertyName("operation_id")] Guid OperationId,
        [property: JsonPropertyName("state")] string State);

    private sealed record GetUploadResponse(
        [property: JsonPropertyName("upload_id")] Guid UploadId,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("source_document_key")] string SourceDocumentKey,
        [property: JsonPropertyName("normalized_text_sha256")] string NormalizedTextSha256,
        [property: JsonPropertyName("declared_bytes")] long DeclaredBytes,
        [property: JsonPropertyName("document_id")] Guid? DocumentId,
        [property: JsonPropertyName("document_version_id")] Guid? DocumentVersionId,
        [property: JsonPropertyName("operation_id")] Guid? OperationId,
        [property: JsonPropertyName("correlation_id")] string? CorrelationId);

    private sealed record OperationStatusResponse(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("failure_stage")] string? FailureStage,
        [property: JsonPropertyName("failure_code")] string? FailureCode,
        [property: JsonPropertyName("queue_wait")] TimeSpan QueueWait,
        [property: JsonPropertyName("chunk_count")] int ChunkCount,
        [property: JsonPropertyName("chunking_duration")] TimeSpan ChunkingDuration,
        [property: JsonPropertyName("embedding_calls")] int EmbeddingCalls,
        [property: JsonPropertyName("embedding_duration")] TimeSpan EmbeddingDuration,
        [property: JsonPropertyName("indexing_duration")] TimeSpan IndexingDuration,
        [property: JsonPropertyName("terminal_state")] string? TerminalState);
}
