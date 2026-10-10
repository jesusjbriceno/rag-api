using System.Net.Http.Headers;
using Rag.AdminApp;

namespace Rag.AdminApp.Host;

public sealed record AdminProxyResponse(int StatusCode, string? ContentType, byte[] Body);

/// <summary>
/// The server-side bridge to the admin API. Every forwarded request is signed here:
/// a fresh short-lived assertion carrying the verified subject, the caller-supplied
/// idempotency key (validated by the routes and forwarded unchanged), and the API
/// machine-proof HMAC. The browser never sees or chooses the signing
/// material, the target path, or the forwarded headers; failures are logged without
/// auth material, bodies, or issued secrets.
/// </summary>
public sealed class AdminApiProxyClient(
    HttpClient httpClient,
    AdminRequestSigner signer,
    ILogger<AdminApiProxyClient> logger)
{
    public const string ClientName = "admin-api";

    /// <summary>
    /// Forwards one fixed upstream operation. Returns null when the upstream could not
    /// be reached; the caller maps that to a redacted 502.
    /// </summary>
    public async Task<AdminProxyResponse?> ForwardAsync(
        string verifiedSubject,
        string method,
        string pathAndQuery,
        byte[] body,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var signed = signer.Sign(verifiedSubject, new AdminSignableRequest(method, pathAndQuery, body), DateTimeOffset.UtcNow, idempotencyKey);
        using var request = new HttpRequestMessage(HttpMethod.Post, pathAndQuery)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        foreach (var (name, value) in signed.Headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var responseBody = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            logger.LogInformation(
                "Proxied admin operation {Method} {Path} -> {StatusCode}",
                method, pathAndQuery, (int)response.StatusCode);
            return new AdminProxyResponse((int)response.StatusCode, response.Content.Headers.ContentType?.ToString(), responseBody);
        }
        catch (Exception exception)
            when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Admin API request {Method} {Path} failed: {ExceptionType}",
                method, pathAndQuery, exception.GetType().Name);
            return null;
        }
    }
}
