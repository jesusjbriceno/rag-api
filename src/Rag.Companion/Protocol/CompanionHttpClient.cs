using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Rag.Companion.Protocol;

public sealed record CompanionCredentials(string BaseUrl, string CompanionId, string KeyId, string Secret);

public sealed class CompanionHttpException : Exception
{
    public CompanionHttpException(HttpStatusCode statusCode, string message) : base(message) => StatusCode = statusCode;

    public HttpStatusCode StatusCode { get; }
}

public sealed class CompanionHttpClient
{
    public const string LeasePath = "/companion/v1/import-jobs/lease";

    /// <summary>The running heartbeat must be sent at least this often to keep the lease alive.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

    private readonly HttpClient _http;
    private readonly CompanionCredentials _credentials;
    private readonly Uri _baseUri;
    private readonly TimeProvider _clock;

    public CompanionHttpClient(HttpClient http, CompanionCredentials credentials, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrWhiteSpace(credentials.BaseUrl))
        {
            throw new ArgumentException("BaseUrl is required.", nameof(credentials));
        }

        _http = http;
        _credentials = credentials;
        _baseUri = new Uri(credentials.BaseUrl.EndsWith('/') ? credentials.BaseUrl : credentials.BaseUrl + "/", UriKind.Absolute);
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Returns <c>null</c> when the BFF responds <c>204</c> (no work).</summary>
    public async Task<LeaseResponse?> AcquireLeaseAsync(CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.Serialize(new LeaseRequest(), ProtocolJson.Options);
        using var response = await SendSignedAsync(HttpMethod.Post, LeasePath, body, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        if (response.StatusCode == HttpStatusCode.OK)
        {
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<LeaseResponse>(payload, ProtocolJson.Options);
        }

        throw await CreateExceptionAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EventResponse> SendEventAsync(string jobId, EventRequest eventRequest, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentNullException.ThrowIfNull(eventRequest);

        var path = $"/companion/v1/import-jobs/{Uri.EscapeDataString(jobId)}/events";
        var body = JsonSerializer.Serialize(eventRequest, ProtocolJson.Options);
        using var response = await SendSignedAsync(HttpMethod.Post, path, body, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<EventResponse>(payload, ProtocolJson.Options)
                ?? throw new CompanionHttpException(HttpStatusCode.OK, "Empty event response body.");
        }

        throw await CreateExceptionAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendSignedAsync(HttpMethod method, string path, string body, CancellationToken cancellationToken)
    {
        var bodySha256 = CompanionSigner.ComputeBodySha256(body);
        var timestamp = _clock.GetUtcNow().ToUnixTimeSeconds();
        var nonce = Guid.NewGuid().ToString("N");
        var canonical = CanonicalString.Create(method.Method, path, bodySha256, _credentials.CompanionId, _credentials.KeyId, timestamp, nonce);
        var signature = CompanionSigner.ComputeSignature(_credentials.Secret, canonical.ToString());

        var request = new HttpRequestMessage(method, new Uri(_baseUri, path))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Companion-Id", _credentials.CompanionId);
        request.Headers.TryAddWithoutValidation("X-Companion-Key-Id", _credentials.KeyId);
        request.Headers.TryAddWithoutValidation("X-Companion-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-Companion-Nonce", nonce);
        request.Headers.TryAddWithoutValidation("X-Companion-Body-SHA256", bodySha256);
        request.Headers.TryAddWithoutValidation("X-Companion-Signature", signature);

        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CompanionHttpException> CreateExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var message = string.IsNullOrWhiteSpace(payload)
            ? $"Companion endpoint returned {(int)response.StatusCode}."
            : payload;
        return new CompanionHttpException(response.StatusCode, message);
    }
}
