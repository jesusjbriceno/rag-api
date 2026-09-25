using System.Net;
using System.Text.Json;
using Rag.HistoricalLoader.Core.Lifecycle;
using Rag.HistoricalLoader.Engine.Api;
using Rag.HistoricalLoader.Engine.Security;

namespace Rag.HistoricalLoader.UnitTests.ApiClient;

public sealed class HistoricalApiClientTests
{
    private static readonly Guid CollectionId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UploadId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DocumentId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid VersionId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid OperationId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private const string Sha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle)
        : HttpMessageHandler
    {
        public List<(string Method, string Path, string Body)> Requests { get; } = [];

        public int CommitCalls => Requests.Count(static r => r.Path.EndsWith(":commit", StringComparison.Ordinal));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method.Method, request.RequestUri!.PathAndQuery, body));
            return await handle(request, cancellationToken);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        return response;
    }

    private static HistoricalApiClient CreateClient(FakeHandler handler, bool withContentResolver = false)
    {
        var options = new HistoricalApiClientOptions(
            new Uri("https://example.test"),
            CollectionId,
            _ => Task.FromResult(new RagServiceCredential("key-id", "rag-secret")),
            _ => Task.FromResult(new CloudflareServiceToken("cf-id", "cf-secret")),
            ContentResolver: withContentResolver
                ? (key, _) => Task.FromResult(new HistoricalContent("normalized text", 15))
                : null);
        return new HistoricalApiClient(new HttpClient(handler), options);
    }

    private static string TokenBody() =>
        """{"access_token":"bearer-token-value","token_type":"Bearer","expires_in":900,"scope":"historical:uploads.write historical:operations.read"}""";

    private static Task<HttpResponseMessage> Route(FakeHandler handler, HttpRequestMessage request)
    {
        if (request.RequestUri!.AbsolutePath == "/api/v1/auth/token")
        {
            return Task.FromResult(Json(HttpStatusCode.OK, TokenBody()));
        }

        return Task.FromResult(Json(HttpStatusCode.NotFound, """{"title":"missing"}"""));
    }

    [Fact]
    public async Task Reserve_sends_contract_request_shape()
    {
        var handler = new FakeHandler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/auth/token")
            {
                return Json(HttpStatusCode.OK, TokenBody());
            }

            return Json(HttpStatusCode.Created, $$"""
                {"upload_id":"{{UploadId}}","state":"reserved","correlation_id":"corr-1","created":true}
                """);
        });
        var client = CreateClient(handler);

        var result = await client.ReserveAsync(new HistoricalReserveRequest(
            "source-key-1",
            "idem-1",
            Sha256,
            DeclaredBytes: 1234,
            CandidateId: Guid.Parse("55555555-5555-5555-5555-555555555555"),
            DisplayName: "file.txt"));

        var reserveBody = handler.Requests.Single(r => r.Path.EndsWith("/uploads", StringComparison.Ordinal) && r.Method == "POST").Body;
        using var document = JsonDocument.Parse(reserveBody);
        var root = document.RootElement;

        Assert.Equal(UploadId, result.UploadId);
        Assert.Equal("idem-1", root.GetProperty("idempotency_key").GetString());
        Assert.Equal("source-key-1", root.GetProperty("source_document_key").GetString());
        Assert.Equal(1234, root.GetProperty("declared_bytes").GetInt64());
        Assert.Equal(Sha256, root.GetProperty("normalized_text_sha256").GetString());
        Assert.Equal("file.txt", root.GetProperty("display_name").GetString());
    }

    [Fact]
    public async Task Commit_parses_document_version_and_operation_ids()
    {
        var handler = new FakeHandler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/auth/token")
            {
                return Json(HttpStatusCode.OK, TokenBody());
            }

            return Json(HttpStatusCode.OK, $$"""
                {"upload_id":"{{UploadId}}","document_id":"{{DocumentId}}","document_version_id":"{{VersionId}}","operation_id":"{{OperationId}}","state":"committed"}
                """);
        });
        var client = CreateClient(handler);

        var result = await client.CommitAsync(UploadId);

        Assert.Equal(DocumentId, result.DocumentId);
        Assert.Equal(VersionId, result.DocumentVersionId);
        Assert.Equal(OperationId, result.OperationId);
        Assert.Equal("committed", result.State);
    }

    [Fact]
    public async Task Unknown_commit_outcome_reconciles_via_idempotent_resource_without_duplicate_commit()
    {
        var handler = new FakeHandler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/auth/token")
            {
                return Json(HttpStatusCode.OK, TokenBody());
            }

            if (request.RequestUri!.AbsolutePath.EndsWith(":commit", StringComparison.Ordinal))
            {
                throw new TaskCanceledException("simulated timeout after dispatch");
            }

            return Json(HttpStatusCode.OK, $$"""
                {"upload_id":"{{UploadId}}","state":"committed","source_document_key":"source-key-1","normalized_text_sha256":"{{Sha256}}","declared_bytes":1234,"document_id":"{{DocumentId}}","document_version_id":"{{VersionId}}","operation_id":"{{OperationId}}","correlation_id":"corr-1"}
                """);
        });
        var client = CreateClient(handler);

        var transport = await Assert.ThrowsAsync<HistoricalTransportException>(() => client.CommitAsync(UploadId));
        Assert.True(transport.UnknownOutcome);

        var status = await client.GetUploadAsync(UploadId);

        Assert.Equal(DocumentId, status.DocumentId);
        Assert.Equal(OperationId, status.OperationId);
        Assert.Equal(1, handler.CommitCalls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, false, AuthBlockReason.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, false, AuthBlockReason.InsufficientScope)]
    [InlineData(HttpStatusCode.Forbidden, true, AuthBlockReason.CloudflareDenied)]
    public async Task Auth_failures_are_classified_as_blocked_auth(
        HttpStatusCode status,
        bool cloudflare,
        AuthBlockReason expectedReason)
    {
        var handler = new FakeHandler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/auth/token")
            {
                return Json(HttpStatusCode.OK, TokenBody());
            }

            var response = Json(status, """{"title":"denied"}""");
            if (cloudflare)
            {
                response.Headers.TryAddWithoutValidation("CF-Ray", "abc123");
            }

            return response;
        });
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<HistoricalAuthException>(() => client.ReserveAsync(new HistoricalReserveRequest(
            "source-key-1", "idem-1", Sha256, 1234)));

        Assert.Equal(expectedReason, exception.Reason);
        Assert.Equal("blocked_auth", exception.ErrorCode);
    }

    [Fact]
    public async Task Invalid_scope_during_exchange_is_auth_failure_not_transient()
    {
        var handler = new FakeHandler((request, _) =>
            Task.FromResult(Json(HttpStatusCode.BadRequest, """{"title":"invalid_scope"}""")));
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<HistoricalAuthException>(() => client.ReserveAsync(new HistoricalReserveRequest(
            "source-key-1", "idem-1", Sha256, 1234)));

        Assert.Equal(AuthBlockReason.InvalidScope, exception.Reason);
        Assert.Equal("blocked_auth", exception.ErrorCode);
    }

    [Fact]
    public async Task Frozen_contract_maps_auth_failure_to_blocked_auth()
    {
        var handler = new FakeHandler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/auth/token")
            {
                return Json(HttpStatusCode.OK, TokenBody());
            }

            return Json(HttpStatusCode.Unauthorized, """{"title":"Unauthorized"}""");
        });
        var client = CreateClient(handler, withContentResolver: true);
        IHistoricalApiClient api = client;

        var result = await api.ReserveAsync(new ApiOperation("idem-1", "source-key-1", Sha256));

        Assert.Equal(ApiOutcome.AuthFailure, result.Outcome);
        Assert.Equal("blocked_auth", result.ErrorCode);
    }

    [Fact]
    public async Task Client_surfaces_never_expose_sentinels()
    {
        const string ragSecret = "s3cret-rag-secret";
        const string cfSecret = "s3cret-cf-secret";
        const string bearer = "s3cret-bearer-token";
        const string content = "s3cret-document-content";
        const string absolutePath = "/home/user/s3cret/path/file.txt";

        var handler = new FakeHandler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/auth/token")
            {
                return Json(HttpStatusCode.OK, $$"""
                    {"access_token":"{{bearer}}","token_type":"Bearer","expires_in":900}
                    """);
            }

            if (request.RequestUri!.AbsolutePath.EndsWith(":commit", StringComparison.Ordinal))
            {
                throw new TaskCanceledException("simulated timeout after dispatch");
            }

            if (request.RequestUri!.AbsolutePath.EndsWith("/content", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, $$"""
                {"upload_id":"{{UploadId}}","state":"published","declared_bytes":{{content.Length}},"observed_bytes":{{content.Length}},"normalized_text_sha256":"{{Sha256}}"}
                """);
            }

            return Json(HttpStatusCode.Created, $$"""
                {"upload_id":"{{UploadId}}","state":"reserved","correlation_id":"corr-1","created":true}
                """);
        });

        var options = new HistoricalApiClientOptions(
            new Uri("https://example.test"),
            CollectionId,
            _ => Task.FromResult(new RagServiceCredential("key-id", ragSecret)),
            _ => Task.FromResult(new CloudflareServiceToken("cf-id", cfSecret)));
        var client = new HistoricalApiClient(new HttpClient(handler), options);

        var reserve = await client.ReserveAsync(new HistoricalReserveRequest("source-key-1", "idem-1", Sha256, content.Length));
        await client.PublishContentAsync(reserve.UploadId, content);

        var auth = new HistoricalAuthException(AuthBlockReason.Unauthorized);
        var transport = new HistoricalTransportException(unknownOutcome: true);
        var contract = new HistoricalContractException("contract_conflict");

        var surfaces = new[]
        {
            auth.Message,
            auth.ErrorCode,
            transport.Message,
            contract.Message,
            contract.ErrorCode,
        };
        var reserveBody = handler.Requests.Single(r => r.Path.EndsWith("/uploads", StringComparison.Ordinal)).Body;

        foreach (var surface in surfaces)
        {
            Assert.DoesNotContain(ragSecret, surface);
            Assert.DoesNotContain(cfSecret, surface);
            Assert.DoesNotContain(bearer, surface);
            Assert.DoesNotContain(content, surface);
            Assert.DoesNotContain(absolutePath, surface);
        }

        Assert.DoesNotContain(ragSecret, reserveBody);
        Assert.DoesNotContain(cfSecret, reserveBody);
        Assert.DoesNotContain(bearer, reserveBody);
        Assert.DoesNotContain(absolutePath, reserveBody);
        Assert.True(handler.Requests.All(r => !r.Path.Contains(ragSecret) && !r.Path.Contains(cfSecret) && !r.Path.Contains(bearer)));
    }
}
