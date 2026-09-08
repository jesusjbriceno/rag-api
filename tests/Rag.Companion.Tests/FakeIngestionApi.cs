using System.Net;
using System.Net.Http;
using System.Text;

namespace Rag.Companion.Tests;

public enum RequestKind
{
    Token,
    Ingestion,
    Poll,
}

public sealed record CapturedRequest(RequestKind Kind, string Path, string? Body, string? Authorization);

/// <summary>
/// In-process fake of the data-plane ingestion API (token exchange, TXT ingestion, and operation
/// polling). It captures every request so tests can assert external references, token caching, and
/// poll sequences without a live server.
/// </summary>
public sealed class FakeIngestionApi : HttpMessageHandler
{
    private readonly List<CapturedRequest> _requests = [];
    private readonly Queue<string> _pollStatuses = new();

    public int TokenRequestCount => _requests.Count(r => r.Kind == RequestKind.Token);
    public int IngestionRequestCount => _requests.Count(r => r.Kind == RequestKind.Ingestion);
    public int PollRequestCount => _requests.Count(r => r.Kind == RequestKind.Poll);
    public IReadOnlyList<CapturedRequest> Requests => _requests;

    public string AccessToken { get; set; } = "test-access-token";
    public int ExpiresInSeconds { get; set; } = 900;

    public HttpStatusCode IngestionStatus { get; set; } = HttpStatusCode.Accepted;
    public Guid? IngestionOperationId { get; set; } = Guid.NewGuid();
    public Guid DocumentId { get; set; } = Guid.NewGuid();
    public Guid DocumentVersionId { get; set; } = Guid.NewGuid();

    public void EnqueuePollStatuses(params string[] statuses)
    {
        foreach (var status in statuses)
        {
            _pollStatuses.Enqueue(status);
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var method = request.Method.Method;
        var body = request.Content is null
            ? null
            : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
        var authorization = request.Headers.Authorization?.ToString();

        if (method == "POST" && path.EndsWith("/api/v1/auth/token", StringComparison.Ordinal))
        {
            _requests.Add(new CapturedRequest(RequestKind.Token, path, body, authorization));
            return Task.FromResult(Json(
                HttpStatusCode.OK,
                $"{{\"access_token\":\"{AccessToken}\",\"token_type\":\"Bearer\",\"expires_in\":{ExpiresInSeconds}}}"));
        }

        if (method == "POST" && path.Contains("/ingestions:txt", StringComparison.Ordinal))
        {
            _requests.Add(new CapturedRequest(RequestKind.Ingestion, path, body, authorization));

            if (IngestionStatus is not (HttpStatusCode.OK or HttpStatusCode.Accepted))
            {
                return Task.FromResult(new HttpResponseMessage(IngestionStatus));
            }

            var operationIdJson = IngestionOperationId is null ? "null" : $"\"{IngestionOperationId}\"";
            var json = $"{{\"document_id\":\"{DocumentId}\",\"document_version_id\":\"{DocumentVersionId}\",\"operation_id\":{operationIdJson}}}";
            return Task.FromResult(Json(IngestionStatus, json));
        }

        if (method == "GET" && path.Contains("/operations/", StringComparison.Ordinal))
        {
            _requests.Add(new CapturedRequest(RequestKind.Poll, path, null, authorization));
            var status = _pollStatuses.Count > 0 ? _pollStatuses.Dequeue() : "succeeded";
            return Task.FromResult(Json(HttpStatusCode.OK, $"{{\"id\":\"{Guid.NewGuid()}\",\"status\":\"{status}\"}}"));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
