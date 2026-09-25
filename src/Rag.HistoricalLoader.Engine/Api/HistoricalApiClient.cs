using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Rag.HistoricalLoader.Core.Lifecycle;
using Rag.HistoricalLoader.Engine.Security;

namespace Rag.HistoricalLoader.Engine.Api;

public enum AuthBlockReason
{
    CloudflareDenied,
    Unauthorized,
    InsufficientScope,
    InvalidScope,
}

public sealed class HistoricalAuthException(AuthBlockReason reason, string errorCode = "blocked_auth")
    : Exception("Authentication is blocked; credential or boundary correction is required.")
{
    public AuthBlockReason Reason { get; } = reason;

    public string ErrorCode { get; } = errorCode;
}

public sealed class HistoricalTransportException(bool unknownOutcome, string message = "The request failed.")
    : Exception(message)
{
    public bool UnknownOutcome { get; } = unknownOutcome;
}

public sealed class HistoricalContractException(string errorCode) : Exception($"Contract failure ({errorCode}).")
{
    public string ErrorCode { get; } = errorCode;
}

public sealed record HistoricalContent(string Value, long DeclaredBytes);

public sealed record HistoricalReserveRequest(
    string SourceDocumentKey,
    string IdempotencyKey,
    string NormalizedTextSha256,
    long DeclaredBytes,
    Guid? CandidateId = null,
    Guid? ManifestId = null,
    Guid? RunId = null,
    string? DisplayName = null,
    string? Format = null,
    string? SourceRootAlias = null);

public sealed record HistoricalReserveResponse(Guid UploadId, string State, string CorrelationId, bool Created);

public sealed record HistoricalUploadResponse(Guid UploadId, string State, long DeclaredBytes, long ObservedBytes, string NormalizedTextSha256);

public sealed record HistoricalCommitResponse(Guid UploadId, Guid DocumentId, Guid DocumentVersionId, Guid OperationId, string State);

public sealed record HistoricalUploadStatus(
    Guid UploadId,
    string State,
    string SourceDocumentKey,
    string NormalizedTextSha256,
    long DeclaredBytes,
    Guid? DocumentId,
    Guid? DocumentVersionId,
    Guid? OperationId,
    string CorrelationId);

public sealed record HistoricalOperationStatus(string Status, string? FailureStage = null, string? FailureCode = null, string? TerminalState = null)
{
    public bool IsPending => string.Equals(Status, "pending", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Status, "running", StringComparison.OrdinalIgnoreCase);
}

public sealed record HistoricalApiClientOptions(
    Uri BaseUri,
    Guid CollectionId,
    Func<CancellationToken, Task<RagServiceCredential>> RagCredentialProvider,
    Func<CancellationToken, Task<CloudflareServiceToken>> CloudflareTokenProvider,
    string Scope = "historical:uploads.write historical:operations.read",
    TimeSpan? TokenSafetyMargin = null,
    Func<string, CancellationToken, Task<HistoricalContent>>? ContentResolver = null);

/// <summary>
/// Real engine HTTP client for the frozen Unit 8 historical API contract. RAG credentials are exchanged
/// for a memory-only short-lived bearer token, Cloudflare Access service-token headers are applied at the
/// outer boundary, and HTTP failures map onto the engine's stable <see cref="ApiOutcome"/> classes. Every
/// authentication failure becomes the global <c>blocked_auth</c> signal; ambiguous mutations reconcile
/// through the idempotent upload-status resource.
/// </summary>
public sealed class HistoricalApiClient : IHistoricalApiClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly Guid _collectionId;
    private readonly string _scope;
    private readonly Func<CancellationToken, Task<RagServiceCredential>> _rag;
    private readonly Func<CancellationToken, Task<CloudflareServiceToken>> _cf;
    private readonly TokenMemoryCache _tokens;
    private readonly CloudflareHeaderBuilder _cloudflare = new();
    private readonly Func<string, CancellationToken, Task<HistoricalContent>>? _content;

    // Remote ids are remembered per document (source key + normalized content hash), never per
    // idempotency key: the driver keys one mutation at a time, so its key cannot identify a document
    // across the reserve/upload/commit/poll stages.
    private readonly Dictionary<DocumentCorrelation, Guid> _uploads = new();
    private readonly Dictionary<DocumentCorrelation, Guid> _operations = new();
    private readonly object _gate = new();

    public HistoricalApiClient(HttpClient httpClient, HistoricalApiClientOptions options)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentNullException.ThrowIfNull(options);

        _baseUri = options.BaseUri ?? throw new ArgumentException("BaseUri is required.", nameof(options));
        if (options.CollectionId == Guid.Empty)
        {
            throw new ArgumentException("CollectionId is required.", nameof(options));
        }

        _collectionId = options.CollectionId;
        _scope = options.Scope;
        _rag = options.RagCredentialProvider ?? throw new ArgumentException("RagCredentialProvider is required.", nameof(options));
        _cf = options.CloudflareTokenProvider ?? throw new ArgumentException("CloudflareTokenProvider is required.", nameof(options));
        _content = options.ContentResolver;
        _tokens = new TokenMemoryCache(ExchangeTokenAsync, options.TokenSafetyMargin);
    }

    public async Task<HistoricalReserveResponse> ReserveAsync(HistoricalReserveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/v1/historical/collections/{_collectionId:D}/uploads",
            JsonContent.Create(new
            {
                source_document_key = request.SourceDocumentKey,
                candidate_id = request.CandidateId,
                manifest_id = request.ManifestId,
                run_id = request.RunId,
                normalized_text_sha256 = request.NormalizedTextSha256,
                declared_bytes = request.DeclaredBytes,
                idempotency_key = request.IdempotencyKey,
                display_name = request.DisplayName,
                format = request.Format,
                source_root_alias = request.SourceRootAlias,
            }, null, Json),
            mutation: true,
            cancellationToken).ConfigureAwait(false);

        var root = await RootAsync(response, cancellationToken).ConfigureAwait(false);
        return new HistoricalReserveResponse(
            ReadGuid(root, "upload_id"),
            ReadString(root, "state"),
            ReadString(root, "correlation_id"),
            root.GetProperty("created").GetBoolean());
    }

    public async Task<HistoricalUploadResponse> PublishContentAsync(Guid uploadId, string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var body = new StringContent(content, Encoding.UTF8, "text/plain");
        using var response = await SendAsync(
            HttpMethod.Put,
            $"/api/v1/historical/uploads/{uploadId:D}/content",
            body,
            mutation: true,
            cancellationToken).ConfigureAwait(false);

        var root = await RootAsync(response, cancellationToken).ConfigureAwait(false);
        return new HistoricalUploadResponse(
            ReadGuid(root, "upload_id"),
            ReadString(root, "state"),
            root.GetProperty("declared_bytes").GetInt64(),
            root.GetProperty("observed_bytes").GetInt64(),
            ReadString(root, "normalized_text_sha256"));
    }

    public async Task<HistoricalCommitResponse> CommitAsync(Guid uploadId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/v1/historical/uploads/{uploadId:D}:commit",
            content: null,
            mutation: true,
            cancellationToken).ConfigureAwait(false);

        var root = await RootAsync(response, cancellationToken).ConfigureAwait(false);
        return new HistoricalCommitResponse(
            ReadGuid(root, "upload_id"),
            ReadGuid(root, "document_id"),
            ReadGuid(root, "document_version_id"),
            ReadGuid(root, "operation_id"),
            ReadString(root, "state"));
    }

    public async Task<HistoricalUploadStatus> GetUploadAsync(Guid uploadId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/v1/historical/uploads/{uploadId:D}",
            content: null,
            mutation: false,
            cancellationToken).ConfigureAwait(false);

        var root = await RootAsync(response, cancellationToken).ConfigureAwait(false);
        return new HistoricalUploadStatus(
            ReadGuid(root, "upload_id"),
            ReadString(root, "state"),
            ReadString(root, "source_document_key"),
            ReadString(root, "normalized_text_sha256"),
            root.GetProperty("declared_bytes").GetInt64(),
            ReadGuidOrNull(root, "document_id"),
            ReadGuidOrNull(root, "document_version_id"),
            ReadGuidOrNull(root, "operation_id"),
            ReadString(root, "correlation_id"));
    }

    public async Task<HistoricalOperationStatus> GetOperationAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/v1/historical/collections/{_collectionId:D}/operations/{operationId:D}",
            content: null,
            mutation: false,
            cancellationToken).ConfigureAwait(false);

        var root = await RootAsync(response, cancellationToken).ConfigureAwait(false);
        return new HistoricalOperationStatus(
            ReadString(root, "status"),
            ReadStringOrNull(root, "failure_stage"),
            ReadStringOrNull(root, "failure_code"),
            ReadStringOrNull(root, "terminal_state"));
    }

    Task<ApiOperationResult> IHistoricalApiClient.ReserveAsync(ApiOperation operation, CancellationToken cancellationToken)
        => MapAsync(async () =>
        {
            var declared = (await ResolveContentAsync(operation, cancellationToken).ConfigureAwait(false)).DeclaredBytes;
            var result = await ReserveAsync(new HistoricalReserveRequest(
                operation.SourceDocumentKey,
                operation.IdempotencyKey,
                operation.ContentSha256 ?? string.Empty,
                declared), cancellationToken).ConfigureAwait(false);
            RememberUploadId(operation, result.UploadId);
            return new ApiOperationResult(ApiOutcome.Success, RemoteId: result.UploadId.ToString("D"));
        }, cancellationToken);

    Task<ApiOperationResult> IHistoricalApiClient.UploadAsync(ApiOperation operation, CancellationToken cancellationToken)
        => MapAsync(async () =>
        {
            var content = await ResolveContentAsync(operation, cancellationToken).ConfigureAwait(false);
            var result = await PublishContentAsync(GetUploadId(operation), content.Value, cancellationToken).ConfigureAwait(false);
            return new ApiOperationResult(ApiOutcome.Success, RemoteId: result.UploadId.ToString("D"));
        }, cancellationToken);

    Task<ApiOperationResult> IHistoricalApiClient.CommitAsync(ApiOperation operation, CancellationToken cancellationToken)
        => MapAsync(async () =>
        {
            var result = await CommitAsync(GetUploadId(operation), cancellationToken).ConfigureAwait(false);
            RememberOperationId(operation, result.OperationId);
            return new ApiOperationResult(ApiOutcome.Success, RemoteId: result.OperationId.ToString("D"));
        }, cancellationToken);

    Task<ApiOperationResult> IHistoricalApiClient.PollAsync(ApiOperation operation, CancellationToken cancellationToken)
        => MapAsync(async () =>
        {
            var operationId = GetOperationId(operation);
            var status = await GetOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
            return new ApiOperationResult(ApiOutcome.Success, RemoteId: operationId.ToString("D"), Pending: status.IsPending);
        }, cancellationToken);

    private async Task<IssuedToken> ExchangeTokenAsync(CancellationToken cancellationToken)
    {
        var rag = await _rag(cancellationToken).ConfigureAwait(false);
        using var request = await BuildRequestAsync(
            HttpMethod.Post,
            "/api/v1/auth/token",
            JsonContent.Create(new { keyId = rag.KeyId, secret = rag.Secret, scope = _scope }, null, Json),
            cancellationToken).ConfigureAwait(false);

        using var response = await SendAndClassifyAsync(request, mutation: false, tokenExchange: true, cancellationToken).ConfigureAwait(false);
        var root = await RootAsync(response, cancellationToken).ConfigureAwait(false);
        return new IssuedToken(
            ReadString(root, "access_token"),
            DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32()),
            ReadStringOrNull(root, "scope"));
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        bool mutation,
        CancellationToken cancellationToken)
    {
        var token = await _tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        using var request = await BuildRequestAsync(method, path, content, cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await SendAndClassifyAsync(request, mutation, tokenExchange: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpRequestMessage> BuildRequestAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, new Uri(_baseUri, path));
        _cloudflare.Apply(request, await _cf(cancellationToken).ConfigureAwait(false));
        request.Content = content;
        return request;
    }

    private async Task<HttpResponseMessage> SendAndClassifyAsync(
        HttpRequestMessage request,
        bool mutation,
        bool tokenExchange,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HistoricalTransportException(mutation, "The request timed out before the server confirmed an outcome.");
        }
        catch (HttpRequestException)
        {
            throw new HistoricalTransportException(unknownOutcome: false, "The request failed before reaching the server.");
        }

        if (!response.IsSuccessStatusCode)
        {
            ThrowForFailure(response, tokenExchange);
        }

        return response;
    }

    private static void ThrowForFailure(HttpResponseMessage response, bool tokenExchange)
    {
        var status = (int)response.StatusCode;
        if (response.Headers.TryGetValues("CF-Ray", out _) && status is 401 or 403)
        {
            throw new HistoricalAuthException(AuthBlockReason.CloudflareDenied);
        }

        if (tokenExchange)
        {
            switch (status)
            {
                case 401:
                    throw new HistoricalAuthException(AuthBlockReason.Unauthorized);
                case 400:
                    throw new HistoricalAuthException(AuthBlockReason.InvalidScope);
                case 429:
                case >= 500:
                    throw new HistoricalTransportException(unknownOutcome: false, "The credential exchange failed transiently.");
                default:
                    throw new HistoricalContractException("token_exchange_failed");
            }
        }

        switch (status)
        {
            case 401:
                throw new HistoricalAuthException(AuthBlockReason.Unauthorized);
            case 403:
                throw new HistoricalAuthException(AuthBlockReason.InsufficientScope);
            case 409:
                throw new HistoricalContractException("contract_conflict");
            case 429:
            case >= 500:
                throw new HistoricalTransportException(unknownOutcome: false, "The server reported a transient failure.");
            default:
                throw new HistoricalContractException("contract_failure");
        }
    }

    private async Task<HistoricalContent> ResolveContentAsync(ApiOperation operation, CancellationToken cancellationToken)
    {
        if (_content is null)
        {
            throw new HistoricalContractException("content_resolver_missing");
        }

        return await _content(operation.SourceDocumentKey, cancellationToken).ConfigureAwait(false);
    }

        /// <summary>
        /// Identifies the remote resources belonging to one document version. The driver keys each stage
        /// operation with its own idempotency key ("{document}:reserve", "{document}:upload", ":commit",
        /// ":poll"), so that key identifies one mutation and never one document; the source document key
        /// plus the normalized content hash stay identical across a document's reserve, upload, commit and
        /// poll stages, which is what lets the client recall the ids it was given earlier.
        /// </summary>
        private static DocumentCorrelation Correlate(ApiOperation operation)
            => new(operation.SourceDocumentKey, operation.ContentSha256);

        private readonly record struct DocumentCorrelation(string SourceDocumentKey, string? ContentSha256);

        private void RememberUploadId(ApiOperation operation, Guid uploadId)
        {
            lock (_gate)
            {
                _uploads[Correlate(operation)] = uploadId;
            }
        }

        private Guid GetUploadId(ApiOperation operation)
        {
            lock (_gate)
            {
                return _uploads.TryGetValue(Correlate(operation), out var value)
                    ? value
                    : throw new HistoricalContractException("upload_id_unresolved");
            }
        }

        private void RememberOperationId(ApiOperation operation, Guid operationId)
        {
            lock (_gate)
            {
                _operations[Correlate(operation)] = operationId;
            }
        }

        private Guid GetOperationId(ApiOperation operation)
        {
            lock (_gate)
            {
                return _operations.TryGetValue(Correlate(operation), out var value)
                    ? value
                    : throw new HistoricalContractException("operation_id_unresolved");
            }
        }

    private static async Task<ApiOperationResult> MapAsync(Func<Task<ApiOperationResult>> action, CancellationToken cancellationToken)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (HistoricalAuthException auth)
        {
            return new ApiOperationResult(ApiOutcome.AuthFailure, ErrorCode: auth.ErrorCode);
        }
        catch (HistoricalTransportException transport)
        {
            return transport.UnknownOutcome
                ? new ApiOperationResult(ApiOutcome.UnknownOutcome, ErrorCode: "unknown_outcome")
                : new ApiOperationResult(ApiOutcome.TransientFailure, ErrorCode: "transient_failure");
        }
        catch (HistoricalContractException contract)
        {
            return new ApiOperationResult(ApiOutcome.ContractFailure, ErrorCode: contract.ErrorCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new ApiOperationResult(ApiOutcome.ContractFailure, ErrorCode: "unexpected");
        }
    }

    private static async Task<JsonElement> RootAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        => await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken).ConfigureAwait(false);

    private static Guid ReadGuid(JsonElement root, string name) => root.GetProperty(name).GetGuid();

    private static string ReadString(JsonElement root, string name) => root.GetProperty(name).GetString() ?? string.Empty;

    private static string? ReadStringOrNull(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String ? value.GetString() : null;

    private static Guid? ReadGuidOrNull(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String ? value.GetGuid() : null;
}
