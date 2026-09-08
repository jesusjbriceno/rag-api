using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rag.Companion.Adapters;

namespace Rag.Companion.Ingestion;

/// <summary>
/// Submits normalized UTF-8 text to the data-plane ingestion API. It exchanges service-client
/// credentials for a short-lived bearer token (cached until near expiry), derives the SHA-256 of
/// the normalized text as the external reference, posts
/// <c>POST /api/v1/collections/{id}/ingestions:txt</c>, and polls any returned operation to its
/// terminal state. Transient token, submission, and polling failures are retried with bounded
/// exponential backoff; non-transient failures fail the file immediately.
/// </summary>
public sealed class IngestionClient
{
    /// <summary>Delay between successive operation-status polls.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan TokenRefreshSkew = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly string _keyId;
    private readonly string _secret;
    private readonly TimeProvider _clock;
    private readonly RetryPolicy _retry;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private AccessToken? _cachedToken;

    public IngestionClient(
        HttpClient http,
        string baseUrl,
        string keyId,
        string secret,
        TimeProvider? clock = null,
        RetryPolicy? retry = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new ArgumentException("BaseUrl is required.", nameof(baseUrl));
        }

        if (string.IsNullOrWhiteSpace(keyId))
        {
            throw new ArgumentException("KeyId is required.", nameof(keyId));
        }

        if (string.IsNullOrEmpty(secret))
        {
            throw new ArgumentException("Secret is required.", nameof(secret));
        }

        _http = http;
        _baseUri = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/", UriKind.Absolute);
        _keyId = keyId;
        _secret = secret;
        _clock = clock ?? TimeProvider.System;
        _retry = retry ?? new RetryPolicy();
        _delay = delay ?? (static (delay, cancellationToken) => Task.Delay(delay, cancellationToken));
    }

    /// <summary>
    /// Submits one normalized UTF-8 document and returns once the server records it: a duplicate
    /// resolves immediately; a fresh document resolves after its operation reaches a terminal
    /// state. Throws <see cref="IngestionException"/> for failures the file cannot recover from.
    /// </summary>
    public async Task<IngestionResult> SubmitTextAsync(
        Guid collectionId,
        string fileName,
        string normalizedText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(normalizedText);
        if (collectionId == Guid.Empty)
        {
            throw new ArgumentException("A collection id is required.", nameof(collectionId));
        }

        var payload = new TxtIngestionPayload
        {
            FileName = EnsureTxtFileName(fileName),
            Content = normalizedText,
            ExternalReference = ComputeExternalReference(normalizedText),
        };
        var json = AdapterRegistry.SerializeIngestionPayload(payload);

        var token = await GetTokenAsync(cancellationToken).ConfigureAwait(false);
        var result = await PostAsync(collectionId, json, token, cancellationToken).ConfigureAwait(false);

        if (result.OperationId is null)
        {
            // Duplicate: the server returned 200 with no operation, so there is nothing to poll.
            return result;
        }

        return await PollToTerminalAsync(
            collectionId,
            result.OperationId.Value,
            result.DocumentId,
            result.DocumentVersionId,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IngestionResult> PostAsync(
        Guid collectionId,
        string json,
        AccessToken token,
        CancellationToken cancellationToken) =>
        await _retry.ExecuteAsync(
            token2 => PostOnceAsync(collectionId, json, token, token2),
            IsTransient,
            cancellationToken).ConfigureAwait(false);

    private async Task<IngestionResult> PostOnceAsync(
        Guid collectionId,
        string json,
        AccessToken token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(_baseUri, $"api/v1/collections/{collectionId}/ingestions:txt"))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            var duplicate = Deserialize<IngestionResponse>(body)
                ?? throw InvalidResponse("Ingestion returned an empty duplicate response.", response.StatusCode);
            if (duplicate.OperationId is not null)
            {
                throw InvalidResponse("A duplicate response carried an operation id.", response.StatusCode);
            }

            return new IngestionResult(duplicate.DocumentId, duplicate.DocumentVersionId, null, true);
        }

        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            var accepted = Deserialize<IngestionResponse>(body)
                ?? throw InvalidResponse("Ingestion returned an empty accepted response.", response.StatusCode);
            if (accepted.OperationId is null)
            {
                throw InvalidResponse("An accepted response omitted the operation id.", response.StatusCode);
            }

            return new IngestionResult(accepted.DocumentId, accepted.DocumentVersionId, accepted.OperationId, false);
        }

        throw MapFailure(response.StatusCode);
    }

    private async Task<IngestionResult> PollToTerminalAsync(
        Guid collectionId,
        Guid operationId,
        Guid documentId,
        Guid documentVersionId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var state = await _retry.ExecuteAsync(
                token => PollOnceAsync(collectionId, operationId, token),
                IsTransient,
                cancellationToken).ConfigureAwait(false);

            if (state == OperationState.Succeeded)
            {
                return new IngestionResult(documentId, documentVersionId, operationId, false);
            }

            if (state == OperationState.Failed)
            {
                throw new IngestionException(
                    IngestionErrorCodes.OperationFailed,
                    "The ingestion operation reached a failed terminal state.");
            }

            await _delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<OperationState> PollOnceAsync(Guid collectionId, Guid operationId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(_baseUri, $"api/v1/collections/{collectionId}/operations/{operationId}"));
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            (await GetTokenAsync(cancellationToken).ConfigureAwait(false)).Value);

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw MapFailure(response.StatusCode);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var status = Deserialize<OperationStatusResponse>(body)
            ?? throw InvalidResponse("Operation polling returned an empty response.", response.StatusCode);

        return ParseOperationState(status.Status);
    }

    private async Task<AccessToken> GetTokenAsync(CancellationToken cancellationToken)
    {
        var cached = _cachedToken;
        if (cached is not null && cached.ExpiresAt > _clock.GetUtcNow() + TokenRefreshSkew)
        {
            return cached;
        }

        var token = await _retry.ExecuteAsync(TokenExchangeAsync, IsTransient, cancellationToken).ConfigureAwait(false);
        _cachedToken = token;
        return token;
    }

    private async Task<AccessToken> TokenExchangeAsync(CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new TokenExchangeRequest(_keyId, _secret), JsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "api/v1/auth/token"))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw MapFailure(response.StatusCode);
        }

        var token = Deserialize<TokenResponse>(payload);
        if (token is null || string.IsNullOrEmpty(token.AccessToken))
        {
            throw new IngestionException(
                IngestionErrorCodes.TokenExchangeFailed,
                "Token exchange returned no access token.");
        }

        if (token.ExpiresInSeconds <= 0)
        {
            throw new IngestionException(
                IngestionErrorCodes.TokenExchangeFailed,
                "Token exchange returned an invalid expiry.");
        }

        return new AccessToken(token.AccessToken, _clock.GetUtcNow().AddSeconds(token.ExpiresInSeconds));
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new IngestionException(
                IngestionErrorCodes.TemporarilyUnavailable,
                "The ingestion API is unreachable.",
                isTransient: true,
                innerException: exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IngestionException(
                IngestionErrorCodes.TemporarilyUnavailable,
                "The ingestion request timed out.",
                isTransient: true,
                innerException: exception);
        }
    }

    private static IngestionException MapFailure(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => new IngestionException(
            IngestionErrorCodes.Unauthorized,
            "The service-client credential was rejected.",
            statusCode: statusCode),
        HttpStatusCode.BadRequest => new IngestionException(
            IngestionErrorCodes.InvalidRequest,
            "The ingestion request was rejected.",
            statusCode: statusCode),
        HttpStatusCode.NotFound => new IngestionException(
            IngestionErrorCodes.NotFound,
            "The collection or operation was not found.",
            statusCode: statusCode),
        HttpStatusCode.RequestEntityTooLarge => new IngestionException(
            IngestionErrorCodes.PayloadTooLarge,
            "The ingestion body exceeded the size limit.",
            statusCode: statusCode),
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout or (HttpStatusCode)425 =>
            new IngestionException(
                IngestionErrorCodes.TemporarilyUnavailable,
                "The ingestion API is temporarily unavailable.",
                isTransient: true,
                statusCode: statusCode),
        _ when (int)statusCode >= 500 => new IngestionException(
            IngestionErrorCodes.TemporarilyUnavailable,
            "The ingestion API failed transiently.",
            isTransient: true,
            statusCode: statusCode),
        _ => new IngestionException(
            IngestionErrorCodes.InvalidResponse,
            $"The ingestion API returned {(int)statusCode}.",
            statusCode: statusCode),
    };

    private static bool IsTransient(Exception exception) => exception switch
    {
        IngestionException ingestion => ingestion.IsTransient,
        HttpRequestException => true,
        _ => false,
    };

    private static string ComputeExternalReference(string normalizedText) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedText))).ToLowerInvariant();

    private static string EnsureTxtFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        // The data plane accepts only .txt file names; strip any directory and guarantee the suffix.
        var name = Path.GetFileName(fileName.Trim());
        return name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? name : name + ".txt";
    }

    private static OperationState ParseOperationState(string? status) => status?.ToLowerInvariant() switch
    {
        "pending" => OperationState.Pending,
        "running" => OperationState.Running,
        "succeeded" => OperationState.Succeeded,
        "failed" => OperationState.Failed,
        _ => throw new IngestionException(
            IngestionErrorCodes.InvalidResponse,
            "Operation polling returned an unknown status."),
    };

    private static IngestionException InvalidResponse(string message, HttpStatusCode statusCode) =>
        new(IngestionErrorCodes.InvalidResponse, message, statusCode: statusCode);

    private static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions);

    private static JsonSerializerOptions CreateJsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private sealed record TokenExchangeRequest(
        [property: JsonPropertyName("key_id")] string KeyId,
        [property: JsonPropertyName("secret")] string Secret);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresInSeconds);

    private sealed record IngestionResponse(
        [property: JsonPropertyName("document_id")] Guid DocumentId,
        [property: JsonPropertyName("document_version_id")] Guid DocumentVersionId,
        [property: JsonPropertyName("operation_id")] Guid? OperationId);

    private sealed record OperationStatusResponse(
        [property: JsonPropertyName("status")] string? Status);

    private sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);
}
